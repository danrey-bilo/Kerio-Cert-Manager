namespace Modules.Identity.Contracts;

/// <summary>Порт учётных записей: привязать живую сессию своего пользователя к записи доверия в общей транзакции.</summary>
public interface ISessionClientBinder
{
    /// <summary>Привязать сессию <paramref name="sessionId"/> пользователя к метке клиента.</summary>
    Task BindAsync(Guid userId, Guid sessionId, string clientRef, CancellationToken cancellationToken = default);
}
