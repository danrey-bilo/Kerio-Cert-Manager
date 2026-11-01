namespace Modules.Core.Contracts.Authz;

/// <summary>Живы ли эти люди.</summary>
public interface ISubjectDirectory
{
    /// <summary>Отсеять из названных тех, кого нельзя выбрать.</summary>
    Task<IReadOnlySet<Guid>> LiveAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}
