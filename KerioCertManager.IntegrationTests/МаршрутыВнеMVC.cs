using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace KerioCertManager.IntegrationTests;

/// <summary>Каждый маршрут вне MVC объявлен публичным — осознанно, а не по забывчивости.</summary>
[Collection(KerioCertManagerHost.Collection)]
public sealed class МаршрутыВнеMVC(KerioCertManagerHost host)
{
    [Fact]
    public void Каждый_маршрут_вне_MVC_объявлен_публичным()
    {
        var непомеченные = ВнеMvc()
            .Where(маршрут => маршрут.Metadata.GetMetadata<PublicEndpointAttribute>() is null)
            .Select(маршрут => маршрут.DisplayName ?? "без имени")
            .ToList();

        непомеченные.ShouldBeEmpty(
            "маршрут вне MVC не виден фильтру входа и открыт всем. Непомеченные: "
            + string.Join(" | ", непомеченные));
    }

    /// <summary>Сторож действительно видит маршруты вне MVC — и знает их поимённо.</summary>
    [Fact]
    public void Сторож_видит_маршруты_вне_MVC()
    {
        var шаблоны = ВнеMvc().OfType<RouteEndpoint>()
            .Select(маршрут => маршрут.RoutePattern.RawText ?? string.Empty)
            .ToList();

        шаблоны.ShouldContain("/api/health");
        шаблоны.ShouldContain("/api/ready");
        шаблоны.ShouldContain("/activate");
        шаблоны.ShouldContain("/password-reset");
        шаблоны.ShouldContain("/");
        шаблоны.ShouldContain("/assets/{file}");
    }

    private IReadOnlyList<Endpoint> ВнеMvc() =>
        [.. host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(маршрут => маршрут.Metadata.GetMetadata<ControllerActionDescriptor>() is null)];
}
