namespace Modules.Core.Contracts.Audit;

/// <summary>Наблюдение за темпом раскрытия секретов.</summary>
public interface ISecretAccessMonitor
{
    /// <summary>Отметить раскрытие секретов и сказать, сработал ли порог.</summary>
    Task<bool> NoteAsync(Guid actorId, int events = 1, CancellationToken cancellationToken = default);
}

/// <summary>Тревога службы безопасности.</summary>
public sealed record AuditAlert(
    string Kind,
    Guid? ActorId,
    string Summary,
    IReadOnlyDictionary<string, object?> Details);

/// <summary>Куда уходит тревога, кроме журнала.</summary>
public interface IAuditAlertSink
{
    /// <summary>Принять тревогу.</summary>
    Task RaiseAsync(AuditAlert alert, CancellationToken cancellationToken = default);
}
