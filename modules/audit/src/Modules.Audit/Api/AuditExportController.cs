using System.Globalization;
using System.Text;
using System.Text.Json;
using Modules.Audit.Catalog;
using Modules.Audit.Contracts;
using Modules.Audit.Permissions;
using Modules.Audit.Reading;
using Modules.Audit.Settings;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Errors;
using Modules.Core.Features;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Audit.Api;

/// <summary>Выгрузка журнала файлом.</summary>
[ApiController]
[Route("api/audit")]
[Feature("audit.export")]
internal sealed class AuditExportController(
    AuditReader reader,
    ResourceNames names,
    AuditActionCatalog catalog,
    ISettingsReader settings,
    ICurrentUser user,
    IAuditWriter audit,
    AppDbContext db,
    AuditModuleOptions options,
    TimeProvider clock) : ControllerBase
{
    /// <summary>Выгрузка журнала файлом.</summary>
    [HttpPost("export")]
    [RequirePermission(AuditPermissions.Read)]
    [RequirePermission(AuditPermissions.Export)]
    public async Task<IActionResult> Export(
        [FromQuery(Name = "format")] string format = "csv",
        [FromQuery(Name = "actor_id")] Guid? actorId = null,
        [FromQuery(Name = "action")] string? action = null,
        [FromQuery(Name = "module")] string? module = null,
        [FromQuery(Name = "resource_type")] string? resourceType = null,
        [FromQuery(Name = "resource_id")] Guid? resourceId = null,
        [FromQuery(Name = "result")] AuditResult? result = null,
        [FromQuery(Name = "since")] DateTimeOffset? since = null,
        [FromQuery(Name = "until")] DateTimeOffset? until = null,
        [FromQuery(Name = "actor_ip")] string? actorIp = null,
        [FromQuery(Name = "session_id")] Guid? sessionId = null,
        [FromQuery(Name = "request_id")] string? requestId = null,
        CancellationToken cancellationToken = default)
    {
        if (format is not ("csv" or "json"))
        {
            throw new AppException(
                ErrorCodes.Validation, "Формат выгрузки бывает csv или json",
                System.Net.HttpStatusCode.BadRequest);
        }

        var limit = await settings.GetAsync(AuditSettings.ExportRowLimit, cancellationToken);
        var query = new AuditQuery(
            actorId, action, AuditRows.Module(catalog, module), resourceType, resourceId, result,
            AuditRows.Since(since, options, clock), until?.ToUniversalTime(),
            AuditRows.ActorIp(actorIp), sessionId, requestId);

        var rows = await reader.ExportAsync(query, limit + 1, cancellationToken);
        var truncated = rows.Count > limit;

        if (truncated)
        {
            rows = [.. rows.Take(limit)];
        }

        var resolved = await names.ResolveAsync(rows, user.Id!.Value, cancellationToken);
        var items = rows.Select(row => AuditRows.Map(catalog, row, resolved)).ToList();

        audit.Write(new AuditEntry(
            AuditActions.Export,
            ActorId: user.Id,
            Changes: new Dictionary<string, object?>
            {
                ["format"] = format,
                ["rows"] = items.Count,
                ["truncated"] = truncated,
            }));

        await db.SaveChangesAsync(cancellationToken);

        // ⚠ ⚠ ⚠ ТЕЛО ПИШЕТСЯ ПРЯМО В ОТВЕТ, А НЕ СОБИРАЕТСЯ В ПАМЯТИ (этап 60.5).
        Response.ContentType = format == "json" ? "application/json" : "text/csv";
        Response.Headers.ContentDisposition =
            $"attachment; filename=audit.{format}";

        if (format == "json")
        {
            await JsonSerializer.SerializeAsync(
                Response.Body, items, JsonOptions, cancellationToken);
        }
        else
        {
            await CsvAsync(Response.Body, items, cancellationToken);
        }

        return new EmptyResult();
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    /// <summary>Колонки выгрузки.</summary>
    private static readonly string[] CsvColumns =
    [
        "id", "ts", "actor_id", "actor_type", "actor_ip", "user_agent", "permission",
        "action", "module", "resource_type", "resource_id", "resource_name", "result",
        "reason", "changes", "request_id", "session_id", "occurred_at",
    ];

    /// <summary>Записать CSV прямо в поток ответа.</summary>
    private static async Task CsvAsync(
        Stream target, IReadOnlyList<AuditEntryOut> items, CancellationToken cancellationToken)
    {
        // ⚠ UTF-8 без BOM: с ним файл ломает разбор у половины инструментов, а Excel и без него читает выгрузку правильно, если разделитель запятая.
        await using var text = new StreamWriter(target, new UTF8Encoding(false), leaveOpen: true);

        await text.WriteLineAsync(string.Join(',', CsvColumns));

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await text.WriteLineAsync(string.Join(',', new[]
            {
                item.Id.ToString(CultureInfo.InvariantCulture),
                item.Ts.ToString("O", CultureInfo.InvariantCulture),
                item.ActorId?.ToString(),
                item.ActorType,
                item.ActorIp,
                item.UserAgent,
                item.Permission,
                item.Action,
                item.Module,
                item.ResourceType,
                item.ResourceId?.ToString(),
                item.ResourceName,
                item.Result,
                item.Reason,
                item.Changes?.ToJsonString(JsonOptions),
                item.RequestId,
                item.SessionId?.ToString(),

                item.OccurredAt?.ToString("O", CultureInfo.InvariantCulture),
            }.Select(Escape)));
        }
    }

    /// <summary>Экранирование поля CSV.</summary>
    internal static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // ⚠ Ведущие TAB и CR — тоже (ревизия 2026-10-26, решение 0118).
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.AsSpan().ContainsAny(SpecialCsv)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static readonly System.Buffers.SearchValues<char> SpecialCsv =
        System.Buffers.SearchValues.Create([',', '"', '\n', '\r']);
}
