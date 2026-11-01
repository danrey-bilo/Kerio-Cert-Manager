namespace Modules.Core.Contracts.Settings;

/// <summary>Внешний адрес установки.</summary>
public interface IPublicOrigin
{
    /// <summary>Адрес, заданный настройкой.</summary>
    Task<string?> ConfiguredAsync(CancellationToken cancellationToken = default);

    /// <summary>Адрес для показа: заданный, иначе — выведенный из текущего запроса.</summary>
    Task<string?> ResolveAsync(CancellationToken cancellationToken = default);
}
