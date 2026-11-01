using Modules.Tokens.Contracts;
using Modules.Core.Contracts.Tokens;

namespace Modules.Tokens.Tokens;

/// <summary>Билет на скачивание — своей веткой службы токенов: срок запроса, без обновления.</summary>
[Modules.Core.Features.Feature("tokens.download_tickets")]
internal sealed class DownloadTicketIssuer(TokenService tokens) : IDownloadTicketIssuer
{
    /// <inheritdoc />
    public Task<TokenPair> IssueAsync(Guid userId, TimeSpan lifetime, CancellationToken cancellationToken = default) =>
        tokens.IssueAsync(new TokenIssueRequest(userId, TokenKinds.DownloadTicket, Lifetime: lifetime), cancellationToken);
}
