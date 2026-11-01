using Modules.Core.Redaction;

namespace Modules.Identity.Redaction;

/// <summary>Какие поля модуля секретны для журнала.</summary>
internal sealed class IdentitySensitiveKeys : ISensitiveKeyProvider
{
    public IReadOnlyCollection<string> Keys =>
    [
        "password_hash",
        "token_hash",

        "invitation_token",
        "activation_token",
        "reset_token",

        "code_hash",
        "data_key",
        "recovery_code",
    ];
}
