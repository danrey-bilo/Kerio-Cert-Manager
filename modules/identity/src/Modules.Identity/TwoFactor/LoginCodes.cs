using System.Globalization;
using System.Security.Cryptography;
using Modules.Identity.Domain;
using Modules.Identity.Mail;
using Modules.Identity.Settings;
using Modules.Identity.Users;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;

namespace Modules.Identity.TwoFactor;

/// <summary>Одноразовый код входа, уходящий письмом.</summary>
internal sealed class LoginCodes(
    Invitations invitations,
    Mail.IdentityMailGate queue,
    ISettingsReader settings)
{
    /// <summary>Сколько цифр в коде.</summary>
    private const int Цифр = 6;

    /// <summary>Завести код и послать его письмом.</summary>
    public async Task<MailQueueResult> IssueAsync(User user, CancellationToken cancellationToken = default,
        string? binding = null, string purpose = Purposes.LoginCode)
    {
        ArgumentNullException.ThrowIfNull(user);

        // ⚠ Прежние коды человека выдача УДАЛЯЕТ, а не гасит — разбор у Invitations.IssueCodeAsync.
        var код = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

        var минут = Math.Max(
            1, await settings.GetAsync(IdentitySettings.TwoFactorChallengeMinutes, cancellationToken));

        var язык = await queue.LocaleAsync(user.Id, cancellationToken);

        var result = await queue.EnqueueAsync(
            user.Email,
            IdentityMailKinds.LoginCode,
            язык,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["code"] = код,
                ["minutes"] = минут.ToString(CultureInfo.InvariantCulture),
            },
            // ⚠ Письмо с кодом живёт столько же, сколько код (решение 0110): опоздавшее не уйдёт.
            expiresAt: DateTimeOffset.UtcNow.AddMinutes(минут),
            cancellationToken: cancellationToken);

        if (result == MailQueueResult.Queued)
        {
            await invitations.IssueCodeAsync(
                user.Id, purpose, TimeSpan.FromMinutes(минут), код, cancellationToken, binding);
        }

        return result;
    }

    /// <summary>Подошёл ли код.</summary>
    public async Task<bool> VerifyAsync(
        Guid userId, string code, CancellationToken cancellationToken = default,
        string? binding = null, string purpose = Purposes.LoginCode)
    {
        // ⚠ ⚠ Поиск по паре «человек, хеш кода» (ревью 2026-09-15, S2, logincode-1).
        var чей = await invitations.ConsumeCodeAsync(userId, code, purpose, cancellationToken, binding);

        return чей == userId;
    }

    /// <summary>Длина кода — им проверяется, что поле на экране одно на оба способа.</summary>
    public static int Длина => Цифр;
}
