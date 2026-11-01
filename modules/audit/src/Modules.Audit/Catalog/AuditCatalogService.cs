using Modules.Audit.Reading;
using Modules.Core.Contracts.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Modules.Audit.Catalog;

/// <summary>Собирает справочник событий при старте.</summary>
internal sealed class AuditCatalogService(
    IServiceProvider services, ILogger<AuditCatalogService> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<AuditActionCatalog>();

        // ⚠ И то же самое для имён ресурсов.
        using var scope = services.CreateScope();

        var byType = ResourceNames.ByType(
            scope.ServiceProvider.GetServices<IResourceNameResolver>());

        log.LogInformation(
            "audit_catalog_ready {Groups} {Actions} {ResourceTypes}",
            catalog.Groups.Count,
            catalog.Groups.Sum(group => group.Actions.Count),
            byType.Count);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
