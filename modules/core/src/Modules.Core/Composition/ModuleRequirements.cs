using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Modules.Core.Composition;

/// <summary>Что модулю обязан дать проект: служба и последствие её отсутствия.</summary>
public sealed record ModuleRequirement(string Module, Type Service, string Consequence);

/// <summary>Проверка при старте: всё, что модули назвали обязательным, проект дал.</summary>
public sealed class ModuleRequirementsCheck(
    IServiceScopeFactory scopes,
    IEnumerable<ModuleRequirement> requirements,
    IEnumerable<RateLimitRule> rules,
    IOptions<MvcOptions> mvc) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var недостаёт = Недостающее().ToList();

        if (недостаёт.Count > 0)
        {
            throw new InvalidOperationException(
                "Модулям не дали обязательного окружения (решение 0111):" + Environment.NewLine
                + string.Join(Environment.NewLine, недостаёт.Select(строка => " — " + строка)));
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Чего не хватает — строками отказа; пусто, если всё на месте.</summary>
    public IEnumerable<string> Недостающее()
    {
        using var scope = scopes.CreateScope();

        var фильтр = mvc.Value.Filters.Any(Охраняет);

        foreach (var требование in requirements.DistinctBy(требование => (требование.Module, требование.Service)))
        {
            var есть = требование.Service == typeof(IEndpointGuard)
                ? фильтр
                : scope.ServiceProvider.GetService(требование.Service) is not null;

            if (!есть)
            {
                yield return $"модулю {требование.Module} нужен {требование.Service.Name}: без него {требование.Consequence}";
            }
        }

        var ограничитель = scope.ServiceProvider.GetService<IRateLimitEnforcer>();

        foreach (var правило in rules.DistinctBy(правило => (правило.Module, правило.Class)))
        {
            if (ограничитель?.Enforces(правило.Class) != true)
            {
                yield return $"модулю {правило.Module} нужен предел частоты класса «{правило.Class}» "
                    + $"({правило.Paths}): без него путь открыт для перебора";
            }
        }
    }

    private static bool Охраняет(Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata фильтр) => фильтр switch
    {
        TypeFilterAttribute тип => typeof(IEndpointGuard).IsAssignableFrom(тип.ImplementationType),
        ServiceFilterAttribute служба => typeof(IEndpointGuard).IsAssignableFrom(служба.ServiceType),
        _ => фильтр is IEndpointGuard,
    };
}

/// <summary>Требования модуля к проекту.</summary>
public static class ModuleRequirementsExtensions
{
    /// <summary>Модулю нужна служба <typeparamref name="TService"/>; без неё старт падает.</summary>
    public static IServiceCollection Require<TService>(this IServiceCollection services, string module, string consequence)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(new ModuleRequirement(module, typeof(TService), consequence));
        services.TryAddEnumerableCheck();

        return services;
    }

    /// <summary>Объявить предел частоты своего дорогого пути.</summary>
    public static IServiceCollection AddRateLimitRule(this IServiceCollection services, RateLimitRule rule)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(rule);

        services.AddSingleton(rule);
        services.TryAddEnumerableCheck();

        return services;
    }

    private static void TryAddEnumerableCheck(this IServiceCollection services) =>
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions.TryAddEnumerable(
            services, ServiceDescriptor.Singleton<IHostedService, ModuleRequirementsCheck>());
}
