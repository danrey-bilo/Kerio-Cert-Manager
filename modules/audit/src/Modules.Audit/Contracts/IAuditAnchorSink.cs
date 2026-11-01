namespace Modules.Audit.Contracts;

/// <summary>Голова одной цепочки журнала на момент якоря.</summary>
public sealed record AuditAnchorHead(string Partition, long Length, string Hash);

/// <summary>Куда уносится якорь цепочки журнала — за пределы базы (решения 0007, 0113).</summary>
public interface IAuditAnchorSink
{
    /// <summary>Унести головы цепочек.</summary>
    Task PublishAsync(IReadOnlyList<AuditAnchorHead> heads, CancellationToken cancellationToken = default);
}
