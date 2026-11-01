using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Domain;

/// <summary>Одна выданная возможность предъявляться системе.</summary>
internal sealed class AuthToken
{
    public Guid Id { get; set; }

    /// <summary>Чей.</summary>
    public Guid UserId { get; set; }

    /// <summary>Вид: <see cref="TokenKinds"/>.</summary>
    public string Kind { get; set; } = TokenKinds.Session;

    /// <summary>Как токен называется в списке.</summary>
    public string? Name { get; set; }

    /// <summary>Откуда предъявляли в последний раз.</summary>
    public System.Net.IPAddress? Address { get; set; }

    /// <summary>К какому устройству привязан.</summary>
    public string? DeviceRef { get; set; }

    /// <summary>Права, положенные в ключ, — подмножество прав хозяина.</summary>
    public string? Scopes { get; set; }

    /// <summary>Когда личность за этим токеном подтверждали последний раз.</summary>
    public DateTimeOffset? StepUpAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Когда токеном пользовались в последний раз.</summary>
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>Когда кончится, сколько бы им ни пользовались.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Когда отозван.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Почему отозван.</summary>
    public string? RevokedReason { get; set; }
}
