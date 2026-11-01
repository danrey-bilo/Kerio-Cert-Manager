using Modules.Core.Contracts.Audit;

namespace Modules.Audit.Writing;

/// <summary>Держатель контекста запроса.</summary>
internal sealed class ScopedAuditContextAccessor : IAuditContextAccessor
{
    public AuditContext Current { get; private set; } = new();

    /// <summary>Дополнить известное.</summary>
    public void Bind(AuditContext patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        Current = Current with
        {
            RequestId = patch.RequestId ?? Current.RequestId,
            ActorIp = patch.ActorIp ?? Current.ActorIp,
            UserAgent = patch.UserAgent ?? Current.UserAgent,
            ActorId = patch.ActorId ?? Current.ActorId,
            SessionId = patch.SessionId ?? Current.SessionId,
            ActorType = patch.ActorType ?? Current.ActorType,
        };
    }
}
