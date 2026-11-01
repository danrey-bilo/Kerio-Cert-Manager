namespace Modules.Tokens.Contracts;

/// <summary>Метка клиента на живой сессии — открытый контракт токенов (решения 0085, 0113).</summary>
public interface ISessionClientBinding
{
    /// <summary>Пометить сессию <paramref name="sessionId"/> пользователя меткой клиента.</summary>
    Task BindAsync(Guid userId, Guid sessionId, string clientRef, CancellationToken cancellationToken = default);
}
