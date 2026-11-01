namespace Modules.Core.Contracts.Accounts;

/// <summary>Источник субъектов справочника «кому можно что-то выдать или написать».</summary>
public interface IDirectorySource
{
    /// <summary>Все субъекты этого источника, кроме удалённых.</summary>
    Task<IReadOnlyList<DirectorySubject>> SubjectsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Субъект справочника.</summary>
public sealed record DirectorySubject(Guid Id, string Type, string Name, bool IsActive);
