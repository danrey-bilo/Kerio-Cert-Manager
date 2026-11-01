using System.Text.Json;
using System.Text.Json.Serialization;
using Modules.Core.Contracts.Authz;
using Modules.Core.Features;
using Microsoft.AspNetCore.Mvc;

namespace Modules.Settings.Api;

/// <summary>Личные предпочтения человека: что задано, что действует и из чего выбирать.</summary>
internal sealed record PreferencesOut(
    [property: JsonPropertyName("user_id")] Guid UserId,
    [property: JsonPropertyName("overrides")] IReadOnlyDictionary<string, object?> Overrides,
    [property: JsonPropertyName("effective")] IReadOnlyDictionary<string, object?> Effective,
    [property: JsonPropertyName("catalog")] IReadOnlyList<SettingOut> Catalog,
    [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt);

/// <summary>Что задать и что вернуть к общему.</summary>
internal sealed record PreferencesPatchIn(
    [property: JsonPropertyName("values")] Dictionary<string, JsonElement>? Values,
    [property: JsonPropertyName("reset")] List<string>? Reset);

/// <summary>Свои предпочтения интерфейса.</summary>
[ApiController]
[Route("api/me/preferences")]
[Feature("settings.user_preferences")]
internal sealed class MePreferencesController(
    PreferencesService preferences, ICurrentUser current) : ControllerBase
{
    /// <summary>Свои предпочтения: заданное, действующее и справочник.</summary>
    [HttpGet]
    [AuthenticatedEndpoint("свои предпочтения: субъект из личности, подменить нечем")]
    public Task<PreferencesOut> Get(CancellationToken cancellationToken) =>
        preferences.ForAsync(current.Id!.Value, cancellationToken);

    /// <summary>Задать своё и вернуть заданное к общему.</summary>
    [HttpPatch]
    // ⚠ Правка настроек — килобайты (решение 0118): тридцать мегабайт имён в `reset` стоили сотни мегабайт памяти.
    [RequestSizeLimit(64 * 1024)]
    [AuthenticatedEndpoint("свои предпочтения: субъект из личности, подменить нечем")]
    public Task<PreferencesOut> Patch(
        [FromBody] PreferencesPatchIn body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);

        return preferences.UpdateAsync(current.Id!.Value, body.Values, body.Reset, cancellationToken);
    }
}
