namespace Modules.Tokens.Domain;

/// <summary>Сам предъявляемый секрет — то, что уезжает клиенту.</summary>
internal sealed class AuthTokenSecret
{
    public Guid Id { get; set; }

    /// <summary>Чьей строке принадлежит.</summary>
    public Guid TokenId { get; set; }

    /// <summary>Вид: <see cref="SecretKinds"/>.</summary>
    public string Kind { get; set; } = SecretKinds.Access;

    /// <summary>Открытая половина ключа интеграции — по ней строку находят.</summary>
    public string? Selector { get; set; }

    /// <summary>SHA-256 от самого секрета.</summary>
    public byte[] Hash { get; set; } = [];

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Когда секретом воспользовались.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>Виды секретов.</summary>
internal static class SecretKinds
{
    /// <summary>Короткоживущий, предъявляется на каждом запросе.</summary>
    public const string Access = "access";

    /// <summary>Долгоживущий, обменивается на новую пару и гасится при этом.</summary>
    public const string Refresh = "refresh";

    /// <summary>Ключ интеграции: предъявляется как есть, не обменивается ни на что.</summary>
    public const string Key = "key";

    /// <summary>Все допустимые значения — для ограничения в базе.</summary>
    public static readonly string[] All = [Access, Refresh, Key];
}
