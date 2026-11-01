namespace Modules.Core.Contracts.Audit;

/// <summary>Имя события журнала: <c>credential.reveal</c>, <c>auth.login</c>.</summary>
public readonly record struct AuditAction(string Name, string? Title = null)
{
    public override string ToString() => Name;
}

/// <summary>Группа событий одного модуля.</summary>
public sealed record AuditActionGroup(
    string Key,
    IReadOnlyList<AuditAction> Actions,
    string? Title = null);

/// <summary>Модуль объявляет события, которые умеет писать.</summary>
public interface IAuditActionProvider
{
    /// <summary>Группы событий этого модуля.</summary>
    IReadOnlyList<AuditActionGroup> Groups { get; }
}
