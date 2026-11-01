namespace Modules.Core.Contracts.Tokens;

/// <summary>Какие виды токенов гасить.</summary>
[Flags]
public enum TokenKindMask
{
    /// <summary>Ничего.</summary>
    None = 0,

    /// <summary>Входы браузером.</summary>
    Sessions = 1,

    /// <summary>Настольные клиенты.</summary>
    Devices = 2,

    /// <summary>Ключи интеграции.</summary>
    ApiKeys = 4,

    /// <summary>Билеты на скачивание установщика.</summary>
    DownloadTickets = 8,

    /// <summary>То, что гасит смена пароля.</summary>
    PasswordChange = Sessions | Devices | DownloadTickets,

    /// <summary>Всё: удаление человека, «отозвать всё» и восстановление доступа.</summary>
    Everything = Sessions | Devices | ApiKeys | DownloadTickets,
}

/// <summary>Погасить предъявляемые токены человека.</summary>
public interface ITokenRevoker
{
    /// <summary>Закрыть одну строку — «выйти отсюда».</summary>
    Task<bool> RevokeAsync(
        Guid tokenId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Закрыть всё, что подходит под маску.</summary>
    Task<int> RevokeAllAsync(
        Guid userId, string reason, TokenKindMask kinds, CancellationToken cancellationToken = default);

    /// <summary>Закрыть всё, кроме одной строки, — «выгнать остальных».</summary>
    Task<int> RevokeOthersAsync(
        Guid userId,
        Guid? keep,
        string reason,
        TokenKindMask kinds,
        CancellationToken cancellationToken = default);

    /// <summary>Закрыть всё, что привязано к одному устройству.</summary>
    Task<int> RevokeByDeviceAsync(
        string deviceRef, string reason, CancellationToken cancellationToken = default);

    /// <summary>Удалить строки человека насовсем — единственный случай, когда именно удалить.</summary>
    Task<int> DeleteAllAsync(Guid userId, CancellationToken cancellationToken = default);
}
