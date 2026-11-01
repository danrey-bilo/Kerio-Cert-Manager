using Modules.Identity.Domain;
using Modules.Identity.Settings;
using Modules.Core.Contracts.Settings;

namespace Modules.Identity.TwoFactor;

/// <summary>Каким быть второму фактору у этого человека.</summary>
internal sealed class SecondFactorPolicy(
    TwoFactorService factors, Mail.IdentityMailGate mail, ISettingsReader settings, IdentityModuleOptions? options = null)
{
    /// <summary>Письмо не заменяет подтверждённый аутентификатор (решение 0120).</summary>
    private bool БезПисьма => options?.AuthenticatorExcludesEmailCode == true;

    /// <summary>Каким будет фактор при следующем входе этого человека.</summary>
    public async Task<string> ResolveAsync(User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var завёл = await factors.ConfirmedAsync(user.Id, cancellationToken);

        // ⚠ ⚠ Подтверждённый аутентификатор при AuthenticatorExcludesEmailCode решает всё (решение 0120): явно выбранная почта его не заменяет.
        if (БезПисьма && завёл)
        {
            return SecondFactorMethods.Totp;
        }

        // ⚠ Аутентификатор перевешивает УМОЛЧАНИЕ СИСТЕМЫ, но не собственный выбор человека.
        var выбран = user.SecondFactorMethod
            ?? (завёл
                ? SecondFactorMethods.Totp
                : await settings.GetAsync(IdentitySettings.SecondFactor, cancellationToken));

        if (выбран == SecondFactorMethods.Totp && завёл)
        {
            return SecondFactorMethods.Totp;
        }

        // ⚠ ⚠ Почта настроена — спрашиваем письмом, ДАЖЕ у того, кто завёл аутентификатор.
        if (await mail.CanSendAsync(cancellationToken))
        {
            return SecondFactorMethods.Email;
        }

        return завёл ? SecondFactorMethods.Totp : SecondFactorMethods.None;
    }

    /// <summary>Чем этот человек может подтвердить вход прямо сейчас.</summary>
    public async Task<IReadOnlyList<string>> AvailableAsync(
        User user, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var способы = new List<string>(2);
        var завёл = await factors.ConfirmedAsync(user.Id, cancellationToken);

        if (завёл)
        {
            способы.Add(SecondFactorMethods.Totp);
        }

        // ⚠ При AuthenticatorExcludesEmailCode список.
        if (!(БезПисьма && завёл) && await MailAsync(cancellationToken))
        {
            способы.Add(SecondFactorMethods.Email);
        }

        return способы;
    }

    /// <summary>Есть ли чем отправить письмо — независимо от того, кому оно положено.</summary>
    public Task<bool> MailAsync(CancellationToken cancellationToken = default) => mail.CanSendAsync(cancellationToken);

    /// <summary>Годится ли значение как СОБСТВЕННЫЙ выбор.</summary>
    public static bool Выбираем(string? method) =>
        method is null || SecondFactorMethods.Choosable.Contains(method, StringComparer.Ordinal);
}
