namespace Modules.Core.Contracts.Authz;

/// <summary>Одно звено цепочки предков ресурса.</summary>
public sealed record ResourceNode(string ResourceType, Guid Id, bool InheritsAccess = true);

/// <summary>Владелец типа ресурса рассказывает, кто над кем стоит.</summary>
public interface IResourceHierarchy
{
    /// <summary>Тип ресурса, про который этот источник знает.</summary>
    string ResourceType { get; }

    /// <summary>Цепочка от самого ресурса к корню.</summary>
    Task<IReadOnlyList<ResourceNode>> ChainAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Цепочки сразу для списка ресурсов.</summary>
    async Task<IReadOnlyDictionary<Guid, IReadOnlyList<ResourceNode>>> ChainManyAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var чепочки = new Dictionary<Guid, IReadOnlyList<ResourceNode>>();

        foreach (var id in ids.Distinct())
        {
            чепочки[id] = await ChainAsync(id, cancellationToken).ConfigureAwait(false);
        }

        return чепочки;
    }
}
