using System.Net;

namespace Modules.Core.Contracts.Tokens;

/// <summary>Откуда пришёл запрос — для списка «где я вошёл».</summary>
public sealed record RequestOrigin(IPAddress? Address, string? UserAgent);

/// <summary>Выданная пара: короткий токен доступа и долгий токен обновления.</summary>
public sealed record TokenPair(
    string Access,
    DateTimeOffset AccessExpiresAt,
    string Refresh,
    DateTimeOffset RefreshExpiresAt,
    Guid TokenId)
{
    /// <summary>Печать без секретов: запись попадает в лог, в отладчик и в текст исключения.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("секреты скрыты: Access, Refresh");

        return true;
    }
}

/// <summary>Что выдать и кому.</summary>
public sealed record TokenIssueRequest(
    Guid UserId,
    string Kind,
    string? Name = null,
    System.Net.IPAddress? Address = null,
    string? DeviceRef = null,
    IReadOnlyList<string>? Scopes = null,
    TimeSpan? Lifetime = null,
    DateTimeOffset? StepUpAt = null)
{
    /// <summary>Права, которыми ограничен ключ интеграции.</summary>
    public IReadOnlyList<string>? Scopes { get; init; } = Scopes;

    /// <summary>Сколько жить.</summary>
    public TimeSpan? Lifetime { get; init; } = Lifetime;
}

/// <summary>Выдать предъявляемый токен от имени человека.</summary>
public interface ITokenIssuer
{
    /// <summary>Выдать пару человеку, про которого вызывающий уже решил, что пускает.</summary>
    Task<TokenPair> IssueAsync(TokenIssueRequest request, CancellationToken cancellationToken = default);

    /// <summary>Обменять токен обновления на новую пару.</summary>
    Task<TokenPair> RotateAsync(string refresh, RequestOrigin origin, CancellationToken cancellationToken = default);
}
