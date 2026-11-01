using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Contracts;

/// <summary>Билет на скачивание — открытый контракт токенов (решения 0074, 0110, 0113).</summary>
public interface IDownloadTicketIssuer
{
    /// <summary>Выдать билет человеку <paramref name="userId"/> на <paramref name="lifetime"/>.</summary>
    Task<TokenPair> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default);
}
