using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Settings.Permissions;
using Modules.Core.Contracts.Authz;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Settings.Api;

/// <summary>Настройка на экране: объявление, действующее значение и откуда оно взялось.</summary>
internal sealed record SettingOut(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("type")] SettingType Type,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("is_security")] bool IsSecurity,
    [property: JsonPropertyName("nullable")] bool Nullable,
    [property: JsonPropertyName("requires_restart")] bool RequiresRestart,
    [property: JsonPropertyName("minimum")] double? Minimum,
    [property: JsonPropertyName("maximum")] double? Maximum,
    [property: JsonPropertyName("choices")] IReadOnlyList<string>? Choices,
    [property: JsonPropertyName("value")] object? Value,
    [property: JsonPropertyName("default")] object? Default,
    [property: JsonPropertyName("source")] string Source);

/// <summary>Что править и что сбросить.</summary>
internal sealed record SettingsPatchIn(
    [property: JsonPropertyName("values")] Dictionary<string, JsonElement>? Values,
    [property: JsonPropertyName("reset")] List<string>? Reset);

/// <summary>Настройки установки.</summary>
[ApiController]
[Route("api/settings/system")]
[Modules.Core.Features.Feature("settings.admin_api")]
internal sealed class SettingsController(SettingsService settings) : ControllerBase
{
    /// <summary>Весь справочник вместе с действующими значениями и источниками.</summary>
    [HttpGet]
    [RequirePermission(SettingsPermissions.Read)]
    public Task<IReadOnlyList<SettingOut>> List(CancellationToken cancellationToken) =>
        settings.ListAsync(cancellationToken);

    /// <summary>Изменить и сбросить настройки; ответ — тот же справочник.</summary>
    [HttpPatch]
    // ⚠ Правка настроек — килобайты (решение 0118): тридцать мегабайт имён в `reset` стоили сотни мегабайт памяти.
    [RequestSizeLimit(64 * 1024)]
    [RequirePermission(SettingsPermissions.Manage)]
    [RequireRecentSecondFactor]
    public Task<IReadOnlyList<SettingOut>> Update(
        [FromBody] SettingsPatchIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        return settings.UpdateAsync(body.Values, body.Reset, cancellationToken);
    }
}
