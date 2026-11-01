using Modules.Audit.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Reading;

/// <summary>Разрешение имён ресурсов на странице журнала.</summary>
internal sealed class ResourceNames(
    IEnumerable<IResourceNameResolver> resolvers,
    ILogger<ResourceNames> log)
{
    private readonly Dictionary<string, IResourceNameResolver> _byType = ByType(resolvers);

    /// <summary>Разложить резолверы по типу — с внятным отказом на спор.</summary>
    internal static Dictionary<string, IResourceNameResolver> ByType(
        IEnumerable<IResourceNameResolver> resolvers)
    {
        var byType = new Dictionary<string, IResourceNameResolver>(StringComparer.Ordinal);

        foreach (var resolver in resolvers)
        {
            if (byType.TryGetValue(resolver.ResourceType, out var первый))
            {
                throw new InvalidOperationException(
                    $"Тип ресурса '{resolver.ResourceType}' называют двое: "
                    + $"{первый.GetType().Name} и {resolver.GetType().Name}. "
                    + "Имя типа общее на всю систему, и владелец у него должен быть один.");
            }

            byType[resolver.ResourceType] = resolver;
        }

        return byType;
    }

    /// <summary>Назвать всё, что можно назвать на этой странице.</summary>
    public async Task<IReadOnlyDictionary<(string Type, Guid Id), string>> ResolveAsync(
        IReadOnlyList<AuditLogEntry> page,
        Guid readerId,
        CancellationToken cancellationToken = default)
    {
        var names = new Dictionary<(string, Guid), string>();

        var byType = page
            .Where(entry => entry.ResourceType is not null && entry.ResourceId is not null)
            .GroupBy(entry => entry.ResourceType!, StringComparer.Ordinal);

        foreach (var group in byType)
        {
            if (!_byType.TryGetValue(group.Key, out var resolver))
            {
                continue;
            }

            var ids = group.Select(entry => entry.ResourceId!.Value).Distinct().ToList();

            try
            {
                var resolved = await resolver.ResolveAsync(ids, readerId, cancellationToken);

                foreach (var (id, name) in resolved)
                {
                    names[(group.Key, id)] = name;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                log.LogWarning(
                    "audit_name_resolver_failed {ResourceType} {Resolver} {Error}",
                    group.Key, resolver.GetType().Name, SafeError.Describe(error));
            }
        }

        return names;
    }
}
