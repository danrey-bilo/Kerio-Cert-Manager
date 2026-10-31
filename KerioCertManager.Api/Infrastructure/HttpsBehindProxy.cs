namespace KerioCertManager.Api.Infrastructure;

/// <summary>Запрос по HTTP через доверенный прокси — 308 на тот же адрес по HTTPS.</summary>
internal sealed class HttpsBehindProxy(RequestDelegate next)
{
    /// <summary>Заголовок, который ставит ForwardedHeadersMiddleware, приняв X-Forwarded-* от доверенного прокси.</summary>
    private const string Принято = "X-Original-For";

    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;

        if (request.IsHttps
            || !request.Headers.ContainsKey(Принято)
            || request.Path.StartsWithSegments("/api/health")
            || request.Path.StartsWithSegments("/api/ready")
            || !request.Host.HasValue)
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers.Location = $"https://{request.Host}{request.PathBase}{request.Path}{request.QueryString}";
        return Task.CompletedTask;
    }
}
