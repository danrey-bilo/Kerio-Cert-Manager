using System.Net;
using Modules.Core.Contracts.Audit;

namespace Modules.Hosting;

/// <summary>Привязка контекста запроса к журналу: адрес, агент, номер обращения.</summary>
public sealed class AuditContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuditContextAccessor audit)
    {
        audit.Bind(new AuditContext
        {
            RequestId = context.Items.TryGetValue(RequestIdMiddleware.ItemKey, out var id) ? id as string : null,
            ActorIp = Normalize(context.Connection.RemoteIpAddress),
            UserAgent = Trim(context.Request.Headers.UserAgent.FirstOrDefault()),
        });

        await next(context);
    }

    /// <summary>Адрес в вид, который принимает колонка <c>inet</c>.</summary>
    private static IPAddress? Normalize(IPAddress? address) => address switch
    {
        null => null,
        { IsIPv4MappedToIPv6: true } => address.MapToIPv4(),
        _ => address,
    };

    /// <summary>Агент обрезается: заголовок пишет клиент, а в журнале он лежит вечно.</summary>
    private static string? Trim(string? value) => value switch
    {
        null or "" => null,
        { Length: > 512 } => value[..512],
        _ => value,
    };
}
