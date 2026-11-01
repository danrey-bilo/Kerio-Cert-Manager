namespace Modules.Tokens.Contracts;

/// <summary>Машинные коды ошибок модуля токенов.</summary>
public static class TokenErrorCodes
{
    /// <summary>Право есть у хозяина ключа, но в сам ключ не положено.</summary>
    public const string TokenScopeForbidden = "TOKEN_SCOPE_FORBIDDEN";

    /// <summary>Ключ с таким именем у человека уже есть — живой или истёкший, но не отозванный.</summary>
    public const string ApiKeyNameTaken = "API_KEY_NAME_TAKEN";

    /// <summary>Строку машины закрывает учёт машин, а не инвентарь токенов.</summary>
    public const string DeviceRevokeViaDevices = "DEVICE_REVOKE_VIA_DEVICES";
}
