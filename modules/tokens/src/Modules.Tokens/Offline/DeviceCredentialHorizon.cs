using Modules.Tokens.Settings;
using Modules.Tokens.Contracts;
using Modules.Core.Contracts.Settings;

namespace Modules.Tokens.Offline;

/// <summary>Горизонт токена машины — для тех, кто продолжает её доверие без сети.</summary>
[Modules.Core.Features.Feature("tokens.device_tokens")]
internal sealed class DeviceCredentialHorizon(ISettingsReader settings) : IDeviceCredentialHorizon
{
    public string Setting => "auth.device_absolute_days";

    public async Task<TimeSpan?> HorizonAsync(CancellationToken cancellationToken = default)
    {
        var дней = await settings.GetAsync(TokenSettings.DeviceAbsoluteDays, cancellationToken);

        // ⚠ Ноль и отрицательное здесь не «не ограничиваю», а испорченное значение: настройка объявлена с минимумом 1.
        return дней > 0 ? TimeSpan.FromDays(дней) : null;
    }
}
