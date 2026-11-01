using Modules.Identity.Settings;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;

namespace Modules.Identity.TwoFactor;

/// <summary>Окно свежего второго фактора: отметка токена плюс <c>auth.step_up_minutes</c>.</summary>
internal sealed class StepUpFreshness(ISettingsReader settings, TimeProvider clock) : IStepUpWindow
{
    /// <inheritdoc />
    public async Task<int> MinutesAsync(CancellationToken cancellationToken = default) =>
        Math.Max(1, await settings.GetAsync(IdentitySettings.StepUpMinutes, cancellationToken));

    /// <summary>Свежа ли отметка прямо сейчас — и окно, по которому это решено.</summary>
    public async Task<(bool Fresh, int Minutes)> CheckAsync(DateTimeOffset? stepUpAt, CancellationToken cancellationToken = default)
    {
        var минут = await MinutesAsync(cancellationToken);

        return (stepUpAt is { } когда && когда >= clock.GetUtcNow().AddMinutes(-минут), минут);
    }

    /// <summary>До какого момента отметка открывает опасные действия.</summary>
    public async Task<DateTimeOffset?> UntilAsync(DateTimeOffset? stepUpAt, CancellationToken cancellationToken = default)
    {
        if (stepUpAt is not { } когда)
        {
            return null;
        }

        var до = когда.AddMinutes(await MinutesAsync(cancellationToken));

        return clock.GetUtcNow() <= до ? до : null;
    }
}
