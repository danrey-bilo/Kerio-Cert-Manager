using Modules.Audit.Contracts;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Modules.Audit.Partitioning;

/// <summary>Создание партиций наперёд и проверка, что их хватает.</summary>
internal sealed class PartitionMaintenance(
    AppDbContext db,
    IDbContextFactory<AppDbContext> contexts,
    AuditModuleOptions options)
{
    /// <summary>На сколько месяцев вперёд держать партиции.</summary>
    public const int MonthsAhead = 2;

    /// <summary>Сегодняшняя дата <b>по базе</b>, а не по процессу.</summary>
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken = default)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            command.CommandText = "SELECT (now() AT TIME ZONE 'UTC')::date";
            var value = await command.ExecuteScalarAsync(cancellationToken);

            return value switch
            {
                DateOnly date => date,
                DateTime moment => DateOnly.FromDateTime(moment),
                _ => throw new InvalidOperationException(
                    $"Неожиданный тип даты от базы: {value?.GetType().Name ?? "null"}"),
            };
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>Какие партиции уже есть.</summary>
    public async Task<IReadOnlySet<string>> ExistingAsync(CancellationToken cancellationToken = default)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            command.CommandText =
                """
                SELECT c.relname
                FROM pg_class c
                JOIN pg_inherits i ON i.inhrelid = c.oid
                WHERE i.inhparent = to_regclass('audit_log')
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                names.Add(reader.GetString(0));
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return names;
    }

    /// <summary>Сколько заведение партиции ждёт родителя журнала, мс.</summary>
    internal const int LockTimeoutMs = 2000;

    /// <summary>Создать недостающие партиции.</summary>
    public async Task<IReadOnlyList<string>> EnsureAsync(
        DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var date = today ?? await TodayAsync(cancellationToken);
        var existing = await ExistingAsync(cancellationToken);
        var created = new List<string>();

        // ⚠ Партицию заводит ФУНКЦИЯ, а не прямой DDL (этап 11.5).
        await using var owner = options.MaintenanceConnectionString is { Length: > 0 } maintenance
            ? await contexts.CreateDbContextAsync(cancellationToken)
            : null;

        if (owner is not null)
        {
            owner.Database.SetConnectionString(options.MaintenanceConnectionString!);
        }

        var executor = owner ?? db;

        // ⚠ ⚠ Ожидание замка ограничено (ревизия 2026-10-26, решение 0118).
        await executor.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await executor.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {LockTimeoutMs + "ms"}, false)", cancellationToken);

            for (var offset = 0; offset <= MonthsAhead; offset++)
            {
                var month = AuditSchema.MonthStart(date).AddMonths(offset);
                var name = AuditSchema.PartitionName(month);

                if (existing.Contains(name))
                {
                    continue;
                }

                await executor.Database.ExecuteSqlRawAsync(
                    AuditPrivileges.EnsurePartitionCall(month), cancellationToken);

                created.Add(name);
            }
        }
        finally
        {
            await executor.Database.ExecuteSqlRawAsync("RESET lock_timeout", CancellationToken.None);
            await executor.Database.CloseConnectionAsync();
        }

        return created;
    }

    /// <summary>Каких партиций не хватает прямо сейчас.</summary>
    public async Task<IReadOnlyList<string>> MissingAsync(
        DateOnly? today = null, CancellationToken cancellationToken = default)
    {
        var date = today ?? await TodayAsync(cancellationToken);
        var existing = await ExistingAsync(cancellationToken);

        return
        [
            .. Enumerable.Range(0, 2)
                .Select(offset => AuditSchema.PartitionName(AuditSchema.MonthStart(date).AddMonths(offset)))
                .Where(name => !existing.Contains(name)),
        ];
    }

    /// <summary>Есть ли строки в запасной партиции.</summary>
    public async Task<bool> DefaultPartitionHasRowsAsync(CancellationToken cancellationToken = default)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        await db.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            command.CommandText = $"SELECT 1 FROM {AuditSchema.DefaultPartition} LIMIT 1";
            return await command.ExecuteScalarAsync(cancellationToken) is not null;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
