using Modules.Identity.Domain;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;

namespace Modules.Identity.Users;

/// <summary>Распознаёт человека по предъявленному токену.</summary>
internal sealed class SessionIdentityResolver(
    ITokenIntrospector tokens,
    Authz.RequestUserFlags flags,
    AppDbContext db) : IIdentityResolver
{
    /// <summary>Схема, которой помечается токен доступа.</summary>
    private const string Scheme = "Bearer ";

    public async Task<ResolvedIdentity?> ResolveAsync(
        IIdentityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var header = request.Header("Authorization");

        // ⚠ ⚠ Два способа предъявить секрет, и они НЕ равноправны.
        var presented = header is not null && header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)
            ? header[Scheme.Length..].Trim()
            : request.Cookie(TokenKinds.DownloadTicketCookie)?.Trim();

        var кукой = presented is not null && header?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) != true;

        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        // ⚠ Дешёвая проверка формы ДО обращения к базе.
        if (!tokens.Recognises(presented))
        {
            return null;
        }

        if (await tokens.IntrospectAsync(presented, cancellationToken) is not { } token)
        {
            return null;
        }

        // ⚠ ⚠ Кукой предъявляется ТОЛЬКО билет.
        if (кукой && token.Kind != TokenKinds.DownloadTicket)
        {
            return null;
        }

        // ⚠ Состояние человека читается из базы на КАЖДОМ запросе.
        var found = await (
            from user in db.Set<User>()
            where user.Id == token.UserId
            select new
            {
                user.Id,
                user.IsActive,
                user.DeletedAt,
                user.IsSuperuser,
                user.MustChangePassword,
            })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null || !found.IsActive || found.DeletedAt is not null)
        {
            throw new AppException(
                ErrorCodes.UserInactive, "Учётная запись отключена.", System.Net.HttpStatusCode.Unauthorized);
        }

        // ⚠ Флаги отдаются в память запроса НАСТОЯЩИМИ.
        flags.Remember(found.Id, new Modules.Core.Contracts.Accounts.AccountFlags(found.IsActive, found.IsSuperuser));

        // ⚠ Требование фактора считается тем же запросом.
        var enroll = token.Kind == TokenKinds.Session && token.StepUpAt is null;

        return new ResolvedIdentity(
            found.Id,

            // ⚠ МАШИНЕ ФЛАГ НАИБОЛЬШИХ ПРАВ НЕ ОТДАЁТСЯ НИКОГДА, даже если её хозяин — суперпользователь.
            found.IsSuperuser && token.ActorType != AuditActorType.ApiToken,
            // ⚠ Вид субъекта считает ВЛАДЕЛЕЦ ВИДОВ, а не мы: настольный клиент — это человек, ключ интеграции — машина, и знать это правило нам незачем.
            token.ActorType,
            token.TokenId,
            // ⚠ ⚠ Ограничения приезжают в личность НЕЗАВИСИМО от вида предъявленного, и билет на скачивание — не исключение.
            found.MustChangePassword,
            enroll,

            // ⚠ Отметка приезжает С ТОКЕНОМ и не пересчитывается здесь.
            token.StepUpAt,

            // ⚠ Вид предъявленного — не наше знание, а знание владельца видов; мы его только доносим (решение 0033).
            token.Kind,
            token.ClientRef);
    }
}
