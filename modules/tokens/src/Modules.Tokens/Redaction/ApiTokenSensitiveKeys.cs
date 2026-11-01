using Modules.Core.Redaction;

namespace Modules.Tokens.Redaction;

/// <summary>Поля модуля, значения которых не должны попадать в журнал.</summary>
internal sealed class ApiTokenSensitiveKeys : ISensitiveKeyProvider
{
    public IReadOnlyCollection<string> Keys { get; } =
    [
        "api_key",
        "key",
        "key_hash",
        "token_hash",
        "plaintext",
        "refresh_token",
        "selector",
    ];
}
