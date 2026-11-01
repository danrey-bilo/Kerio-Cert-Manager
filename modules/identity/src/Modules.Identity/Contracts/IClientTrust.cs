namespace Modules.Identity.Contracts;

/// <summary>Связь доверия входа с зарегистрированным приложением; хранением владеет модуль учётных записей.</summary>
public interface IClientTrust
{
    /// <summary>Связать действующее доверие приложения; транзакция вызывающего обязательна.</summary>
    Task BindDeviceAsync(Guid userId, string? clientRef, Guid deviceId, CancellationToken cancellationToken = default);

    /// <summary>Отозвать доверие и связанные сессии машины в транзакции её отзыва.</summary>
    Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken = default);
}
