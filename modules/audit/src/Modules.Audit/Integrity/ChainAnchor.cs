using Modules.Audit.Contracts;
using Modules.Audit.Writing;
using Modules.Core.Contracts.Audit;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Modules.Core.Composition;
using Modules.Core.Errors;

namespace Modules.Audit.Integrity;

/// <summary>Голова цепочки одного месяца на момент якоря.</summary>
internal sealed record ChainHead(string Partition, long Seq, byte[] Hash);

/// <summary>Суточный якорь: последний хеш каждой цепочки — записью в журнал и строкой в лог.</summary>
internal sealed class ChainAnchor(
    IDbContextFactory<AppDbContext> contexts,
    ChangesRedactor redactor,
    IAuditContextAccessor context,
    TimeProvider clock,
    ILogger<ChainAnchor> log,
    IEnumerable<IAuditAnchorSink> sinks,
    AdvisoryLockSpace? locks = null)
{
    /// <summary>Ключ межпроцессной блокировки якоря.</summary>
    internal const ushort AdvisoryLockNumber = 2;

    // ⚠ Пространство ключей задаёт проект (решение 0111): у EAMS прежнее, и число замка не меняется.
    private long AdvisoryLockKey => (locks ?? AdvisoryLockSpace.Default).Key(AdvisoryLockNumber);

    /// <summary>Записать якорь.</summary>
    public async Task<IReadOnlyList<ChainHead>> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        // ⚠ Транзакция — ЧЕРЕЗ стратегию выполнения.
        var strategy = db.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();

            return await AnchorAsync(db, cancellationToken);
        });
    }

    /// <summary>Унести головы в каждый приёмник — после фиксации, по одному, без общего отказа.</summary>
    private async Task ВынестиAsync(IReadOnlyList<ChainHead> heads, CancellationToken cancellationToken)
    {
        IReadOnlyList<AuditAnchorHead> головы =
            [.. heads.Select(head => new AuditAnchorHead(head.Partition, head.Seq, Convert.ToHexStringLower(head.Hash)))];

        foreach (var приёмник in sinks)
        {
            try
            {
                await приёмник.PublishAsync(головы, cancellationToken);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogError(
                    "audit_chain_anchor_sink_failed {Sink} {Error}",
                    приёмник.GetType().Name, SafeError.Describe(error));
            }
        }
    }

    /// <summary>Одна попытка.</summary>
    private async Task<IReadOnlyList<ChainHead>> AnchorAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await TryLockAsync(db, cancellationToken))
        {
            return [];
        }

        var heads = await HeadsAsync(db, cancellationToken);

        if (heads.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);

            return [];
        }

        foreach (var head in heads)
        {
            db.Add(AuditRow.From(
                new AuditEntry(
                    AuditActions.Anchor,
                    ActorType: AuditActorType.System,
                    ResourceType: "audit_partition",
                    Changes: new Dictionary<string, object?>
                    {
                        ["partition"] = head.Partition,
                        ["chain_length"] = head.Seq,
                        ["chain_hash"] = Convert.ToHexStringLower(head.Hash),
                    }),
                context.Current,
                clock.GetUtcNow(),
                redactor));
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // ⚠ Вынос — половина смысла якоря: он уносит значение ИЗ базы.
        await ВынестиAsync(heads, cancellationToken);

        return heads;
    }

    /// <summary>Головы всех цепочек — одним чтением, до любой записи.</summary>
    private static async Task<IReadOnlyList<ChainHead>> HeadsAsync(
        AppDbContext db, CancellationToken cancellationToken)
    {
        var heads = new List<ChainHead>();

        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText =
            $"SELECT partition, last_seq, last_hash FROM {AuditChain.HeadTable} ORDER BY partition";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            heads.Add(new ChainHead(
                reader.GetString(0),
                reader.GetInt64(1),
                await reader.GetFieldValueAsync<byte[]>(2, cancellationToken)));
        }

        return heads;
    }

    /// <summary>Досталась ли нам блокировка.</summary>
    private async Task<bool> TryLockAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();

        command.CommandText = $"SELECT pg_try_advisory_xact_lock({AdvisoryLockKey})";
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }
}
