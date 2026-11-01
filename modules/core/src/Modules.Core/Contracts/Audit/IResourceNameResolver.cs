namespace Modules.Core.Contracts.Audit;

/// <summary>Модуль-владелец типа ресурса называет свои объекты для журнала.</summary>
public interface IResourceNameResolver
{
    /// <summary>Тип ресурса, который этот резолвер умеет называть.</summary>
    string ResourceType { get; }

    /// <summary>Назвать объекты по идентификаторам.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ResolveAsync(
        IReadOnlyCollection<Guid> ids,
        Guid readerId,
        CancellationToken cancellationToken = default);
}
