namespace Modules.Tokens.Contracts;

/// <summary>Имена таблиц модуля.</summary>
public static class ApiTokenTables
{
    /// <summary>Выданные токены.</summary>
    public const string Tokens = "auth_tokens";

    /// <summary>Секреты выданных токенов.</summary>
    public const string Secrets = "auth_token_secrets";
}
