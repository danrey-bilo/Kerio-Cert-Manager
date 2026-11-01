using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Modules.Core.Errors;
using Modules.Core.Composition;

namespace Modules.Hosting;

/// <summary>Пределы одного класса: в минуту с адреса и в минуту на человека, если он есть.</summary>
public sealed record RateLimitPolicy(int PerAddress, int? PerUser);

/// <summary>Пределы частоты дорогих путей: правила объявляют модули, пределы классов задаёт проект.</summary>
public sealed class ModuleRateLimits : IRateLimitEnforcer
{
    private readonly IReadOnlyList<RateLimitRule> _правила;
    private readonly IReadOnlyDictionary<string, RateLimitPolicy> _пределы;

    /// <summary>Пределы по правилам модулей и политике проекта.</summary>
    public ModuleRateLimits(IEnumerable<RateLimitRule> rules, IReadOnlyDictionary<string, RateLimitPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(policies);

        _правила = [.. rules];
        _пределы = policies;

        if (policies.Keys.FirstOrDefault(класс => !RateLimitClasses.All.Contains(класс)) is { } чужой)
        {
            throw new InvalidOperationException($"Предел задан незнакомому классу «{чужой}»; известны: "
                + string.Join(", ", RateLimitClasses.All) + ".");
        }
    }

    /// <inheritdoc />
    public bool Enforces(string rateLimitClass) => _пределы.ContainsKey(rateLimitClass);

    /// <summary>Класс и пределы пути или <c>null</c>, если путь не дорогой.</summary>
    public (string Class, RateLimitPolicy Policy)? Of(PathString path)
    {
        var значение = path.Value;

        if (значение is { Length: > 1 } && значение[^1] == '/')
        {
            path = new PathString(значение.TrimEnd('/') is { Length: > 0 } снято ? снято : "/");
        }

        foreach (var правило in _правила)
        {
            if (правило.Matches(path) && _пределы.TryGetValue(правило.Class, out var предел))
            {
                return (правило.Class, предел);
            }
        }

        return null;
    }

    /// <summary>Ключ адреса: IPv4 целиком, IPv6 — сетью /64.</summary>
    public static string AddressKey(IPAddress? address)
    {
        if (address is null)
        {
            return "неизвестно";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var байты = address.GetAddressBytes();

        Array.Clear(байты, 8, 8);

        return $"{new IPAddress(байты)}/64";
    }

    /// <summary>Отказ 429 общим конвертом ошибки и с <c>Retry-After</c>.</summary>
    public static async Task RejectAsync(HttpContext context, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (retryAfter is { } через)
        {
            context.Response.Headers.RetryAfter = ((int)через.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        }

        await context.Response.WriteAsJsonAsync(
            new ErrorResponse(
                ErrorCodes.RateLimited,
                "Слишком много попыток. Подождите немного.",
                null,
                context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id) ? id as string ?? string.Empty : string.Empty),
            cancellationToken);
    }
}

/// <summary>Подключение пределов частоты модулей.</summary>
public static class ModuleRateLimitsExtensions
{
    /// <summary>Ограничивать дорогие пути модулей пределами проекта.</summary>
    public static IServiceCollection AddModuleRateLimits(
        this IServiceCollection services, IReadOnlyDictionary<string, RateLimitPolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(policies);

        services.AddSingleton(provider => new ModuleRateLimits(provider.GetServices<RateLimitRule>(), policies));
        services.AddSingleton<IRateLimitEnforcer>(provider => provider.GetRequiredService<ModuleRateLimits>());

        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var пределы = context.RequestServices.GetRequiredService<ModuleRateLimits>();

                if (пределы.Of(context.Request.Path) is not { } найдено)
                {
                    return RateLimitPartition.GetNoLimiter("прочее");
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{найдено.Class}:{ModuleRateLimits.AddressKey(context.Connection.RemoteIpAddress)}",
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = найдено.Policy.PerAddress,
                        Window = TimeSpan.FromMinutes(1),

                        QueueLimit = 0,
                    });
            });

            limiter.OnRejected = (context, token) => new ValueTask(ModuleRateLimits.RejectAsync(
                context.HttpContext,
                context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter) ? retryAfter : null,
                token));
        });

        return services;
    }
}
