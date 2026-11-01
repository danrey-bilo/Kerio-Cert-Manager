using System.Text.Json.Nodes;
using Modules.Audit.Catalog;
using Modules.Audit.Domain;
using Modules.Audit.Reading;
using Modules.Core.Errors;

namespace Modules.Audit.Api;

/// <summary>Строки журнала наружу и фильтр по модулю — общие для чтения и выгрузки.</summary>
internal static class AuditRows
{
    /// <summary>Развернуть имя модуля в перечень его событий.</summary>
    public static IReadOnlyList<string>? Module(AuditActionCatalog catalog, string? module)
    {
        if (string.IsNullOrEmpty(module))
        {
            return null;
        }

        return catalog.ActionsOf(module) ?? throw new AppException(
            ErrorCodes.Validation,
            "Неизвестный модуль в фильтре",
            System.Net.HttpStatusCode.BadRequest,
            new Dictionary<string, object?>
            {
                ["module"] = module,
                ["known"] = catalog.Groups.Select(group => group.Key).ToList(),
            });
    }

    /// <summary>Адрес фильтра: пусто — фильтра нет; неразбираемый — отказ.</summary>
    public static DateTimeOffset? Since(DateTimeOffset? since, AuditModuleOptions options, TimeProvider clock) =>
        since?.ToUniversalTime()
        ?? (options.DefaultWindow is { } окно ? clock.GetUtcNow() - окно : null);

    public static string? ActorIp(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || System.Net.IPAddress.TryParse(value, out _))
        {
            return value;
        }

        throw new AppException(
            ErrorCodes.Validation,
            "Адрес в фильтре не разобран",
            System.Net.HttpStatusCode.BadRequest,
            new Dictionary<string, object?> { ["field"] = "actor_ip" });
    }

    public static AuditEntryOut Map(
        AuditActionCatalog catalog, AuditLogEntry row, IReadOnlyDictionary<(string, Guid), string> names)
    {
        string? name = null;

        if (row.ResourceType is { } type && row.ResourceId is { } id
            && names.TryGetValue((type, id), out var resolved))
        {
            name = resolved;
        }

        return new AuditEntryOut(
            row.Id, row.Ts, row.ActorId, row.ActorType, row.ActorIp?.ToString(), row.UserAgent,
            row.Permission, row.Action, catalog.GroupOf(row.Action),
            row.ResourceType, row.ResourceId, name, row.Result,
            row.Reason,
            row.ChangesJson is null ? null : JsonNode.Parse(row.ChangesJson),
            row.RequestId, row.SessionId, row.OccurredAt);
    }
}
