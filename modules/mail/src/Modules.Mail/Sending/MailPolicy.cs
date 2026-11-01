using Modules.Mail.Settings;
using Modules.Core.Contracts.Settings;

namespace Modules.Mail.Sending;

/// <summary>Восемь настроек почты, свёрнутые в одно значение.</summary>
internal sealed record MailPolicy(
    bool Enabled,
    string? Host,
    int Port,
    string Security,
    string? Username,
    string FromName,
    string? FromAddress,
    int RatePerHour,
    int MaxAttempts)
{
    /// <summary>Настроена ли почта настолько, чтобы письмо имело шанс уйти.</summary>
    public bool Configured =>
        Enabled && !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);

    /// <summary>Прочитать все девять.</summary>
    public static async Task<MailPolicy> ReadAsync(
        ISettingsReader settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new MailPolicy(
            await settings.GetAsync(MailSettings.Enabled, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpHost, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpPort, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpSecurity, cancellationToken),
            await settings.GetAsync(MailSettings.SmtpUsername, cancellationToken),
            await settings.GetAsync(MailSettings.FromName, cancellationToken),
            await settings.GetAsync(MailSettings.FromAddress, cancellationToken),
            await settings.GetAsync(MailSettings.RatePerHour, cancellationToken),
            await settings.GetAsync(MailSettings.MaxAttempts, cancellationToken));
    }
}
