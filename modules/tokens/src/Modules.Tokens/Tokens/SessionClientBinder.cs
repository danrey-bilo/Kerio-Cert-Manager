using Modules.Tokens.Domain;
using Modules.Tokens.Contracts;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace Modules.Tokens.Tokens;

/// <summary>Метка клиента относится к текущей живой сессии, а не к предъявленному идентификатору.</summary>
internal sealed class SessionClientBinder(AppDbContext db, TimeProvider clock) : ISessionClientBinding
{
    public async Task BindAsync(Guid userId, Guid sessionId, string clientRef, CancellationToken cancellationToken = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Привязка требует транзакцию");
        var updated = await db.Set<AuthToken>().Where(row => row.UserId == userId && row.Id == sessionId
            && row.Kind == TokenKinds.Session && row.RevokedAt == null && row.ExpiresAt > clock.GetUtcNow())
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.DeviceRef, clientRef), cancellationToken);
        if (updated != 1) throw new AppException(ErrorCodes.SessionExpired, "Сессия завершена", System.Net.HttpStatusCode.Unauthorized);
    }
}
