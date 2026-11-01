using System.Reflection;
using Modules.Core.Contracts.Authz;
using Modules.Core.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Core.Composition;

/// <summary>Подключение модуля к MVC одной строкой: части, отбор контроллеров, переключатели, проверка окружения.</summary>
public static class ModuleMvc
{
    /// <summary>Подключить сборку модуля с его переключателями.</summary>
    public static IMvcBuilder AddModule(this IMvcBuilder mvc, Assembly own, ModuleFeatures features)
    {
        ArgumentNullException.ThrowIfNull(mvc);
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(features);

        features.Seal();

        var services = mvc.Services;

        services.AddSingleton(features);
        services.AddSingleton(features.GetType(), features);

        mvc.ConfigureApplicationPartManager(parts =>
        {
            if (!parts.FeatureProviders.OfType<ModuleControllers>().Any(provider => provider.Assembly.Equals(own)))
            {
                parts.FeatureProviders.Add(new ModuleControllers(own, features));
            }
        });

        if (own.GetTypes().Any(IsControllerShape))
        {
            services.Require<IEndpointGuard>(features.Module,
                "эндпоинты модуля не проверяют права, и закрытое отдаётся открытым (подключите PermissionFilter)");
            services.Require<ICurrentUser>(features.Module,
                "личность запроса неизвестна, и любой вход читается как анонимный");
        }

        // ⚠ Решение 0117: модуль, объявивший события журнала, их пишет — и без писателя падал бы на первом действии, а не при старте.
        if (own.GetTypes().Any(тип => тип is { IsAbstract: false, IsInterface: false }
            && typeof(Contracts.Audit.IAuditActionProvider).IsAssignableFrom(тип)))
        {
            services.Require<Contracts.Audit.IAuditWriter>(features.Module,
                "действия модуля некому записать в журнал, а без строки журнала действие не состоится");
        }

        return mvc.AddApplicationPart(own);
    }

    private static bool IsControllerShape(Type type) =>
        type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
        && !type.IsDefined(typeof(Microsoft.AspNetCore.Mvc.NonControllerAttribute))
        && (type.Name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase)
            || type.IsDefined(typeof(Microsoft.AspNetCore.Mvc.ControllerAttribute)));
}
