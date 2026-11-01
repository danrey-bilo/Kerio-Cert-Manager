using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Authz;

/// <summary>Чем предъявились в этом запросе.</summary>
internal sealed class PresentedToken
{
    /// <summary>Что распознали.</summary>
    public TokenIdentity? Identity { get; private set; }

    /// <summary>Вид предъявленного: <c>session</c>, <c>api_key</c>, … <c>null</c> — не было.</summary>
    public string? Kind { get; private set; }

    /// <summary>Запомнить распознанное.</summary>
    public void Remember(TokenIdentity identity, string kind)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);

        Identity = identity;
        Kind = kind;
    }
}
