using System.Security.Cryptography;
using System.Text;
using Modules.Audit.Contracts;
using Modules.Audit.Domain;
using Modules.Audit.Settings;
using Modules.Audit.Writing;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Modules.Core.Composition;

namespace Modules.Audit.Retention;

/// <summary>Чем кончился прогон срока хранения.</summary>
internal sealed record RetentionResult(bool Ran, IReadOnlyList<string> Dropped, IReadOnlyList<string> Scheduled);

/// <summary>Удаление старых записей журнала.</summary>
[Modules.Core.Features.Feature("audit.retention")]
internal sealed class AuditRetention(
    IDbContextFactory<AppDbContext> contexts,
    ISettingsReader settings,
    ChangesRedactor redactor,
    IAuditContextAccessor context,
    AuditModuleOptions options,
    IEnumerable<IAuditAlertSink> sinks,
    TimeProvider clock,
    ILogger<AuditRetention> log,
    AdvisoryLockSpace? locks = null)
{
    /// <summary>Тип ресурса у отметок назначения и отмены: ресурс — партиция журнала.</summary>
    internal const string PartitionResource = "audit_partition";

    /// <summary>Идентификатор партиции как ресурса журнала — выводится из имени.</summary>
    internal static Guid PartitionId(string name) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("audit-partition:" + name)).AsSpan(0, 16));

    /// <summary>Ключ межпроцессной блокировки.</summary>
    internal const ushort AdvisoryLockNumber = 1;

    /// <summary>Сколько уборка ждёт родителя журнала, прежде чем уступить до следующего прохода, мс.</summary>
    internal const int LockTimeoutMs = 3000;

    // ⚠ Пространство ключей задаёт проект (решение 0111): у EAMS прежнее, и число замка не меняется.
    private long AdvisoryLockKey => (locks ?? AdvisoryLockSpace.Default).Key(AdvisoryLockNumber);

    /// <summary>Удалить всё, что старше срока.</summary>
    public async Task<RetentionResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var months = await settings.GetAsync(AuditSettings.RetentionMonths, cancellationToken);

        int? keep = null;

        if (months is { } заданный && заданный > 0)
        {
            // ⚠ ⚠ Граница проекта сильнее настройки установки (решение 0110).
            keep = Math.Max(заданный, Math.Max(1, options.MinimumRetentionMonths));

            if (keep > заданный)
            {
                log.LogWarning("audit_retention_below_floor {Configured} {Floor}", заданный, keep);
            }
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        if (options.MaintenanceConnectionString is { Length: > 0 } maintenance)
        {
            db.Database.SetConnectionString(maintenance);
        }

        // ⚠ Транзакция открывается ЧЕРЕЗ стратегию выполнения, а не напрямую.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            return await SweepAsync(db, keep, cancellationToken);
        });
    }

    /// <summary>Одна попытка уборки.</summary>
    private async Task<RetentionResult> SweepAsync(
        AppDbContext db, int? keep, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryLockAsync(db, cancellationToken))
        {
            return new RetentionResult(Ran: false, [], []);
        }

        var cutoff = keep is { } месяцев
            ? AuditSchema.MonthStart(await TodayAsync(db, cancellationToken)).AddMonths(-месяцев)
            : DateOnly.MinValue;
        var партиции = await PartitionsAsync(db, cancellationToken);
        var now = clock.GetUtcNow();
        var отсрочка = options.RetentionDropDelay < TimeSpan.Zero ? TimeSpan.Zero : options.RetentionDropDelay;
        var отметки = await ОтметкиAsync(db, партиции.Select(partition => partition.Name), cancellationToken);

        bool Назначена(string name) =>
            отметки.TryGetValue(name, out var отметка) && отметка.Action == AuditActions.RetentionScheduled.Name;

        var просрочены = партиции
            .Where(partition => partition.Month < cutoff)
            .OrderBy(partition => partition.Month)
            .ToList();

        var назначить = просрочены.Where(partition => !Назначена(partition.Name)).ToList();

        // ⚠ Удаляется только назначенное И выдержавшее отсрочку И просроченное сейчас: срок, поднятый после назначения, сюда партию уже не пускает.
        var doomed = просрочены
            .Where(partition => Назначена(partition.Name) && отметки[partition.Name].Ts + отсрочка <= now)
            .ToList();

        var отменить = партиции
            .Where(partition => partition.Month >= cutoff && Назначена(partition.Name))
            .ToList();

        foreach (var partition in назначить)
        {
            db.Add(Отметка(AuditActions.RetentionScheduled, partition.Name, new Dictionary<string, object?>
            {
                ["partition"] = partition.Name,
                ["retention_months"] = keep,
                ["cutoff"] = cutoff.ToString("yyyy-MM-dd"),
                ["drop_after"] = now + отсрочка,
            }, now));
        }

        foreach (var partition in отменить)
        {
            db.Add(Отметка(AuditActions.RetentionCancelled, partition.Name, new Dictionary<string, object?>
            {
                ["partition"] = partition.Name,
                ["retention_months"] = keep,
            }, now));
        }

        if (doomed.Count == 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            if (keep is { } срок)
            {
                await ТревогаAsync(назначить, срок, now + отсрочка, cancellationToken);
            }

            return new RetentionResult(Ran: keep is not null, [], [.. назначить.Select(partition => partition.Name)]);
        }

        // ⚠ ⚠ Сначала удаление, потом строки журнала — одной транзакцией (ревизия 2026-10-26, решение 0118).
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('lock_timeout', {LockTimeoutMs + "ms"}, true)", cancellationToken);

        foreach (var partition in doomed)
        {
            // ⚠ Партицию сносит ФУНКЦИЯ, а не прямой `DROP TABLE` (этап 11.5).
#pragma warning disable EF1002 // Имя уходит значением в функцию, а не идентификатором
            await db.Database.ExecuteSqlRawAsync(
                AuditPrivileges.DropPartitionCall(partition.Name), cancellationToken);
#pragma warning restore EF1002
        }

        db.Add(AuditRow.From(
            new AuditEntry(
                AuditActions.Retention,
                ActorType: AuditActorType.System,
                Changes: new Dictionary<string, object?>
                {
                    ["retention_months"] = keep,
                    ["cutoff"] = cutoff.ToString("yyyy-MM-dd"),
                    ["partitions"] = doomed.Select(partition => partition.Name).ToList(),
                }),
            context.Current,
            clock.GetUtcNow(),
            redactor));

        await db.SaveChangesAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        log.LogWarning(
            "audit_retention_dropped {Partitions} {Cutoff}",
            string.Join(", ", doomed.Select(partition => partition.Name)), cutoff);

        await ТревогаAsync(назначить, keep!.Value, now + отсрочка, cancellationToken);

        return new RetentionResult(
            Ran: true,
            [.. doomed.Select(partition => partition.Name)],
            [.. назначить.Select(partition => partition.Name)]);
    }

    /// <summary>Строка-отметка о партиции: назначение или отмена удаления.</summary>
    private AuditLogEntry Отметка(
        AuditAction action, string partition, Dictionary<string, object?> changes, DateTimeOffset now) =>
        AuditRow.From(
            new AuditEntry(
                action,
                ActorType: AuditActorType.System,
                ResourceType: PartitionResource,
                ResourceId: PartitionId(partition),
                Changes: changes),
            context.Current,
            now,
            redactor);

    /// <summary>Последняя отметка каждой партиции — назначение или отмена.</summary>
    private static async Task<Dictionary<string, (string Action, DateTimeOffset Ts)>> ОтметкиAsync(
        AppDbContext db, IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var поИд = names.ToDictionary(PartitionId, name => name);
        var иды = поИд.Keys.Select(id => (Guid?)id).ToList();
        string[] виды = [AuditActions.RetentionScheduled.Name, AuditActions.RetentionCancelled.Name];

        var строки = await db.Set<AuditLogEntry>()
            .AsNoTracking()
            .Where(entry => entry.ResourceType == PartitionResource && иды.Contains(entry.ResourceId))
            .Where(entry => виды.Contains(entry.Action))
            .Select(entry => new { entry.ResourceId, entry.Action, entry.Ts, entry.Id })
            .ToListAsync(cancellationToken);

        return строки
            .GroupBy(строка => строка.ResourceId!.Value)
            .ToDictionary(
                группа => поИд[группа.Key],
                группа => группа
                    .OrderByDescending(строка => строка.Ts)
                    .ThenByDescending(строка => строка.Id)
                    .Select(строка => (строка.Action, строка.Ts))
                    .First());
    }

    /// <summary>Поднять тревогу о назначенном удалении — после фиксации, как у событий безопасности.</summary>
    private async Task ТревогаAsync(
        IReadOnlyList<(string Name, DateOnly Month)> назначены, int keep, DateTimeOffset dropAfter,
        CancellationToken cancellationToken)
    {
        if (назначены.Count == 0)
        {
            return;
        }

        var имена = назначены.Select(partition => partition.Name).ToList();

        log.LogWarning("audit_retention_scheduled {Partitions} {DropAfter}", string.Join(", ", имена), dropAfter);

        var alert = new AuditAlert(
            AuditActions.RetentionScheduled.Name,
            ActorId: null,
            "Назначено удаление старых записей журнала. Поднимите срок хранения до указанного времени, чтобы отменить.",
            new Dictionary<string, object?>
            {
                ["partitions"] = имена,
                ["retention_months"] = keep,
                ["drop_after"] = dropAfter,
            });

        foreach (var sink in sinks)
        {
            try
            {
                await sink.RaiseAsync(alert, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(
                    "audit_retention_alert_failed {Sink} {Error}", sink.GetType().Name, SafeError.Describe(error));
            }
        }
    }

    /// <summary>Досталась ли нам блокировка.</summary>
    private async Task<bool> TryLockAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = $"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey})";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Сегодня по часам базы — партиции нарезаны по UTC, а не по поясу процесса.</summary>
    private static async Task<DateOnly> TodayAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = "SELECT (now() AT TIME ZONE 'UTC')::date";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return await command.ExecuteScalarAsync(cancellationToken) switch
        {
            DateOnly date => date,
            DateTime moment => DateOnly.FromDateTime(moment),
            var other => throw new InvalidOperationException(
                $"Неожиданный тип даты от базы: {other?.GetType().Name ?? "null"}"),
        };
    }

    /// <summary>Помесячные партиции журнала.</summary>
    private static async Task<IReadOnlyList<(string Name, DateOnly Month)>> PartitionsAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var partitions = new List<(string, DateOnly)>();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            """
            SELECT c.relname
            FROM pg_class c
            JOIN pg_inherits i ON i.inhrelid = c.oid
            WHERE i.inhparent = to_regclass('audit_log')
            """;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);

            if (name != AuditSchema.DefaultPartition && AuditSchema.TryParsePartition(name, out var month))
            {
                partitions.Add((name, month));
            }
        }

        return partitions;
    }
}
