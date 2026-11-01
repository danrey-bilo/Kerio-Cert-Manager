using Modules.Audit.Catalog;
using Modules.Audit.Permissions;
using Modules.Audit.Reading;
using Modules.Core.Api;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Audit.Api;

/// <summary>Журнал: чтение и справочник событий; выгрузка — <see cref="AuditExportController"/>.</summary>
[ApiController]
[Route("api/audit")]
[Modules.Core.Features.Feature("audit.read_api")]
internal sealed class AuditController(
    AuditReader reader,
    ResourceNames names,
    AuditActionCatalog catalog,
    ICurrentUser user,
    AuditModuleOptions options,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Справочник событий для фильтра.</summary>
    [HttpGet("actions")]
    [RequirePermission(AuditPermissions.Read)]
    public AuditActionsOut Actions()
    {
        return new AuditActionsOut(
            [
                .. catalog.Groups.Select(group => new AuditActionGroupOut(
                    group.Key,
                    group.Title,
                    [.. group.Actions.Select(action => action.Name)])),
            ],
            catalog.Groups
                .SelectMany(group => group.Actions)
                .Where(action => action.Title is not null)
                .ToDictionary(action => action.Name, action => action.Title!, StringComparer.Ordinal));
    }

    /// <summary>Страница журнала.</summary>
    [HttpGet]
    [RequirePermission(AuditPermissions.Read)]
    public async Task<AuditPageOut> Page(
        [FromQuery(Name = "actor_id")] Guid? actorId,
        [FromQuery(Name = "action")] string? action,
        [FromQuery(Name = "module")] string? module,
        [FromQuery(Name = "resource_type")] string? resourceType,
        [FromQuery(Name = "resource_id")] Guid? resourceId,
        [FromQuery(Name = "result")] AuditResult? result,
        [FromQuery(Name = "since")] DateTimeOffset? since,
        [FromQuery(Name = "until")] DateTimeOffset? until,
        [FromQuery(Name = "actor_ip")] string? actorIp = null,
        [FromQuery(Name = "session_id")] Guid? sessionId = null,
        [FromQuery(Name = "request_id")] string? requestId = null,
        [FromQuery(Name = "limit")] int limit = AuditReader.DefaultLimit,
        [FromQuery(Name = "cursor")] string? cursor = null,
        CancellationToken cancellationToken = default)
    {
        var query = new AuditQuery(
            actorId, action, AuditRows.Module(catalog, module), resourceType, resourceId, result,
            AuditRows.Since(since, options, clock), until?.ToUniversalTime(),
            AuditRows.ActorIp(actorIp), sessionId, requestId);

        var (rows, hasMore) = await reader.PageAsync(
            query,
            PageLimit.Require(limit, AuditReader.DefaultLimit, AuditReader.MaxLimit),
            AuditCursor.Decode(cursor),
            cancellationToken);

        var resolved = await names.ResolveAsync(rows, user.Id!.Value, cancellationToken);

        return new AuditPageOut(
            [.. rows.Select(row => AuditRows.Map(catalog, row, resolved))],
            hasMore ? AuditCursor.Encode(rows[^1].Ts, rows[^1].Id) : null);
    }

}
