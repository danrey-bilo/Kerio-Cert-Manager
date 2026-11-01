using Modules.Audit.Contracts;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Integrity;

/// <summary>Приёмник якоря по умолчанию — строка лога.</summary>
internal sealed class LogAnchorSink(ILogger<ChainAnchor> log) : IAuditAnchorSink
{
    /// <inheritdoc />
    public Task PublishAsync(IReadOnlyList<AuditAnchorHead> heads, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(heads);

        foreach (var head in heads)
        {
            log.LogInformation("audit_chain_anchor {Partition} {Length} {Hash}", head.Partition, head.Length, head.Hash);
        }

        return Task.CompletedTask;
    }
}
