namespace Modules.Core.Contracts.Quotas;

/// <summary>Владелец типа ресурса отвечает, кто за этот объект платит.</summary>
public interface IQuotaPayerResolver
{
    /// <summary>Тип ресурса, о котором этот резолвер умеет отвечать.</summary>
    string ResourceType { get; }

    /// <summary>Кто платит за этот объект.</summary>
    Task<QuotaSubject?> ResolveAsync(Guid resourceId, CancellationToken cancellationToken = default);

    /// <summary>Плательщики пачкой: объекта, которого нет, в ответе нет.</summary>
    async Task<IReadOnlyDictionary<Guid, QuotaSubject>> ResolveManyAsync(
        IReadOnlyCollection<Guid> resourceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);

        var итог = new Dictionary<Guid, QuotaSubject>();

        foreach (var id in resourceIds)
        {
            if (await ResolveAsync(id, cancellationToken) is { } плательщик)
            {
                итог[id] = плательщик;
            }
        }

        return итог;
    }
}
