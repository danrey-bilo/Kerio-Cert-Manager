using Modules.Identity.Settings;
using System.Net;
using System.Text;
using Modules.Core.Contracts.Settings;
using Modules.Core.Errors;
using Modules.Identity.Passwords;

namespace Modules.Identity.Users;

/// <summary>Требования к паролю.</summary>
internal sealed class PasswordPolicy(ISettingsReader settings)
{
    /// <summary>Проверить пароль.</summary>
    public async Task EnsureAsync(string password, CancellationToken cancellationToken = default)
    {
        var codes = new List<string>();
        var reasons = new List<string>();

        var minLength = await settings.GetAsync(IdentitySettings.PasswordMinLength, cancellationToken);

        if (password.Length < minLength)
        {
            codes.Add("min_length");
            reasons.Add($"минимум {minLength} символов");
        }

        // ⚠ ⚠ ⚠ ВЕРХНЯЯ ГРАНИЦА — В БАЙТАХ, А НЕ В СИМВОЛАХ, и это не педантизм.
        if (Encoding.UTF8.GetByteCount(password) > PasswordHasher.MaxPasswordBytes)
        {
            codes.Add("max_length");
            reasons.Add($"не длиннее {PasswordHasher.MaxPasswordBytes} байт");
        }

        await CheckAsync(
            IdentitySettings.RequireUppercase, char.IsUpper, "uppercase", "заглавная буква",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            IdentitySettings.RequireLowercase, char.IsLower, "lowercase", "строчная буква",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            IdentitySettings.RequireDigit, char.IsDigit, "digit", "цифра",
            password, codes, reasons, cancellationToken);
        await CheckAsync(
            IdentitySettings.RequireSymbol, symbol => !char.IsLetterOrDigit(symbol), "symbol", "спецсимвол",
            password, codes, reasons, cancellationToken);

        if (codes.Count == 0)
        {
            return;
        }

        // ⚠ Сам пароль в текст ошибки не попадает ни при каких обстоятельствах: ответы валидации уходят в логи клиента и в отчёты об ошибках.
        throw new AppException(
            ErrorCodes.PasswordPolicyViolation,
            "Пароль не соответствует требованиям: " + string.Join(", ", reasons),
            HttpStatusCode.BadRequest,
            new Dictionary<string, object?> { ["codes"] = codes, ["unmet"] = reasons });
    }

    /// <summary>Действующие требования — чтобы экран показал их ДО попытки.</summary>
    public async Task<PasswordRules> CurrentAsync(CancellationToken cancellationToken = default) =>
        new(
            await settings.GetAsync(IdentitySettings.PasswordMinLength, cancellationToken),
            PasswordHasher.MaxPasswordBytes,
            await settings.GetAsync(IdentitySettings.RequireUppercase, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireLowercase, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireDigit, cancellationToken),
            await settings.GetAsync(IdentitySettings.RequireSymbol, cancellationToken));

    private async Task CheckAsync(
        SettingKey<bool> key,
        Func<char, bool> predicate,
        string code,
        string reason,
        string password,
        List<string> codes,
        List<string> reasons,
        CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(key, cancellationToken) && !password.Any(predicate))
        {
            codes.Add(code);
            reasons.Add(reason);
        }
    }
}

/// <summary>Действующие требования к паролю.</summary>
internal sealed record PasswordRules(
    int MinLength,
    int MaxLengthBytes,
    bool RequireUppercase,
    bool RequireLowercase,
    bool RequireDigit,
    bool RequireSymbol);
