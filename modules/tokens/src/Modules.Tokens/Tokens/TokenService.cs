using System.Security.Cryptography;
using System.Text;
using Modules.Tokens.Contracts;
using Modules.Tokens.Naming;
using Modules.Tokens.Domain;
using Modules.Tokens.Settings;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Data;
using Modules.Core.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Modules.Tokens.Tokens;

/// <summary>Выдача, обмен и проверка предъявляемых секретов.</summary>
internal sealed class TokenService(
    AppDbContext db,
    ITokenRevoker revoker,
    ISettingsReader settings,
    Authz.PresentedToken предъявленный,
    LastSeenWriter touch,
    IAuditWriter audit,
    ISecurityEventWriter security,
    ExpiredKeyNotices истёкшие,
    TimeProvider clock,
    ILogger<TokenService> log,
    TokensModuleOptions? options = null,
    Notifications.SessionNotices? notices = null) : ITokenIssuer, ITokenIntrospector
{
    /// <summary>Кому сказать о сессии — или никому (решение 0112, <c>tokens.session_notices</c>).</summary>
    private Notifications.SessionNotices? Уведомления =>
        options?.Features.SessionNotices == false ? null : notices;

    // ⚠ Приставку ключа задаёт проект (решение 0111); без параметров — нейтральная модуля.
    private readonly string KeyPrefix = options?.ApiKeyPrefix ?? TokensModuleOptions.DefaultApiKeyPrefix;

    public bool Recognises(string presented) => Ours(presented);

    public async Task<TokenIdentity?> IntrospectAsync(
        string presented, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(presented);

        if (!Ours(presented))
        {
            return null;
        }

        // ⚠ Ключ интеграции — своя ветка, и она НЕ ищет по хешу.
        if (Ключ(presented))
        {
            // ⚠ Без подвозможности ключей предъявленный ключ не опознаётся вовсе (решение 0111).
            if (options?.Features.ApiKeys == false)
            {
                return null;
            }

            return await KeyIntrospectAsync(presented, cancellationToken);
        }

        var now = clock.GetUtcNow();
        var hash = Hash(presented);

        var found = await (
            from secret in db.Set<AuthTokenSecret>()
            join owner in db.Set<AuthToken>() on secret.TokenId equals owner.Id
            where secret.Hash == hash && secret.Kind == SecretKinds.Access
            select new { secret.ExpiresAt, Token = owner })

            // ⚠ Без отслеживания: этот путь проходит КАЖДЫЙ запрос с токеном, а менять здесь нечего — только читать.
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        // ⚠ Выключенный вид не опознаётся, как ключ без подвозможности ключей (решение 0113): запрос идёт дальше анонимным.
        if (options?.Features.Issues(found.Token.Kind) == false)
        {
            return null;
        }

        // ⚠ Отзыв проверяется РАНЬШЕ срока, и это не косметика.
        if (found.Token.RevokedAt is not null)
        {
            throw Expired(CodeFor(found.Token.RevokedReason), Explain(found.Token.RevokedReason));
        }

        // ⚠ ⚠ ⚠ СНАЧАЛА СЕССИЯ, ПОТОМ ДОСТУП, и порядок несущий (ревью 2026-09-15, решение 0080).
        if (found.Token.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        if (found.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.TokenExpired, "Токен доступа истёк: обменяйте пару.");
        }

        // ⚠ Вид едет В САМОЙ ЛИЧНОСТИ с 2026-08-29 (решение 0033).
        var identity = new TokenIdentity(
            found.Token.UserId,
            found.Token.Id,
            ActorTypeOf(found.Token.Kind),
            ScopesOf(found.Token),
            found.Token.StepUpAt,
            found.Token.Kind,
            found.Token.DeviceRef);

        // ⚠ Кладём в область запроса ВИД и область действия — их спросит обёртка сужения прав.
        предъявленный.Remember(identity, found.Token.Kind);

        return identity;
    }

    /// <summary>Выдать пару человеку, про которого вызывающий уже решил, что пускает.</summary>
    public Task<TokenPair> IssueAsync(
        TokenIssueRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequireTransaction();

        if (!TokenKinds.All.Contains(request.Kind))
        {
            // ⚠ Отказ, а не подстановка сессии.
            throw new ArgumentException(
                $"Неизвестный вид токена «{request.Kind}». Известны: {string.Join(", ", TokenKinds.All)}.",
                nameof(request));
        }

        // ⚠ Вид, выключенный проектом (решение 0113), не выдаётся и через общий контракт.
        if (options?.Features.Issues(request.Kind) == false)
        {
            throw new ArgumentException($"Вид токена «{request.Kind}» выключен проектом.", nameof(request));
        }

        return IssueAsync(request, clock.GetUtcNow(), cancellationToken);
    }

    /// <summary>Сказать человеку, если вход пришёл с устройства, которого раньше не видели.</summary>
    private async Task УведомитьОНезнакомомAsync(
        TokenIssueRequest request, CancellationToken cancellationToken)
    {
        if (request.Kind != TokenKinds.Session || Уведомления is not { } уведомления)
        {
            return;
        }

        // ⚠ Один запрос с двумя EXISTS вместо счёта и второго запроса (ревизия 2026-10-26, решение 0118).
        var итог = await db.Database
            .SqlQuery<Знакомство>($"""
                SELECT
                    EXISTS (SELECT 1 FROM auth_tokens WHERE user_id = {request.UserId} AND kind = {TokenKinds.Session}) AS "Было",
                    EXISTS (SELECT 1 FROM auth_tokens WHERE user_id = {request.UserId} AND kind = {TokenKinds.Session}
                        AND name IS NOT DISTINCT FROM {request.Name}) AS "Знакомо"
                """)
            .SingleAsync(cancellationToken);

        if (итог.Было && !итог.Знакомо)
        {
            await уведомления.UnknownDeviceAsync(request.UserId, request.Name, cancellationToken);
        }
    }

    /// <summary>Были ли у человека сессии и знакомо ли имя этой.</summary>
    private sealed record Знакомство(bool Было, bool Знакомо);

    public async Task<TokenPair> RotateAsync(
        string refresh, RequestOrigin origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(origin);

        var now = clock.GetUtcNow();
        var hash = Hash(refresh);

        // ⚠ Одной выборкой, а не двумя.
        var found = await (
            from candidate in db.Set<AuthTokenSecret>()
            join owner in db.Set<AuthToken>() on candidate.TokenId equals owner.Id
            where candidate.Hash == hash && candidate.Kind == SecretKinds.Refresh
            select new { Secret = candidate, Token = owner })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");

        var secret = found.Secret;
        var token = found.Token;

        // ⚠ Выключенный вид не обменивается (решение 0113) — ответ тот же, что у незнакомой половины.
        if (options?.Features.Issues(token.Kind) == false)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        if (token.RevokedAt is not null)
        {
            throw Expired(CodeFor(token.RevokedReason), Explain(token.RevokedReason));
        }

        if (token.ExpiresAt <= now || secret.ExpiresAt <= now)
        {
            throw Expired(ErrorCodes.SessionExpired, "Сессия кончилась. Войдите заново.");
        }

        // ⚠ Окно читается один раз и ДО обеих проверок повтора: им решается и повтор, замеченный чтением, и повтор, замеченный гашением (ниже).
        var grace = Окно(await settings.GetAsync(TokenSettings.RefreshGraceSeconds, cancellationToken));

        if (secret.UsedAt is { } usedAt)
        {
            if (Повтор(now, usedAt, grace))
            {
                throw await КражаAsync(token, now - usedAt, cancellationToken);
            }

            log.LogDebug("refresh_token_race {TokenId} {Age}", token.Id, now - usedAt);
        }

        var выдано = await db.InTransactionAsync(
            async (inner) =>
            {
                // ⚠ Гашение условным обновлением, а не присваиванием: одновременные запросы иначе оба сочли бы себя первыми.
                var погашено = await db.Set<AuthTokenSecret>()
                    .Where(row => row.Id == secret.Id && row.UsedAt == null)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UsedAt, now), inner);

                // ⚠ ⚠ Ноль строк — половину погасил ОДНОВРЕМЕННЫЙ запрос, прочитавший её вместе с нами (чтение выше идёт без блокировки).
                if (погашено == 0)
                {
                    var когда = await db.Set<AuthTokenSecret>()
                        .AsNoTracking()
                        .Where(row => row.Id == secret.Id)
                        .Select(row => row.UsedAt)
                        .SingleAsync(inner);

                    if (когда is { } использован && Повтор(now, использован, grace))
                    {
                        return (TokenPair?)null;
                    }
                }

                await db.Set<AuthToken>()
                    .Where(row => row.Id == token.Id)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LastSeenAt, now), inner);

                var pair = await IssueAsync(
                    new TokenIssueRequest(token.UserId, token.Kind, origin.UserAgent, origin.Address, token.DeviceRef),
                    now,
                    inner,
                    token.Id,
                    token.ExpiresAt);

                await db.SaveChangesAsync(inner);

                return (TokenPair?)pair;
            },
            cancellationToken);

        return выдано ?? throw await КражаAsync(token, TimeSpan.Zero, cancellationToken);
    }

    /// <summary>Предъявленная половина — уже потраченная и не гонка вкладок?</summary>
    private static bool Повтор(DateTimeOffset now, DateTimeOffset usedAt, TimeSpan grace) =>
        grace <= TimeSpan.Zero || now - usedAt > grace;

    /// <summary>Наименьшее ненулевое окно снисхождения: повтор обмена клиентом со своим путём по сети.</summary>
    internal static readonly TimeSpan НаименьшееОкно = TimeSpan.FromSeconds(10);

    /// <summary>Действующее окно снисхождения: ноль — строгий режим, иначе не короче <see cref="НаименьшееОкно"/>.</summary>
    internal static TimeSpan Окно(int секунд) =>
        секунд <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(Math.Max(секунд, НаименьшееОкно.TotalSeconds));

    /// <summary>Закрыть сессию как украденную и вернуть отказ, который бросит вызывающий.</summary>
    private async Task<AppException> КражаAsync(AuthToken token, TimeSpan age, CancellationToken cancellationToken)
    {
        await db.InTransactionAsync(
            async (inner) =>
            {
                await revoker.RevokeAsync(token.Id, RevokeReasons.Stolen, inner);

                // ⚠ Уведомление ВНУТРИ той же транзакции (подэтап 15.5.10b): сессия отзывается здесь, и человека сейчас выкинет.
                if (Уведомления is { } уведомления)
                {
                    await уведомления.TokenReuseAsync(token.UserId, inner);
                }

                return true;
            },
            cancellationToken);

        log.LogWarning("refresh_token_reuse {TokenId} {UserId} {Age}", token.Id, token.UserId, age);

        return Expired(ErrorCodes.SessionRevoked, Explain(RevokeReasons.Stolen));
    }

    /// <summary>Живые строки человека — «где я вошёл».</summary>
    public Task<List<AuthToken>> ListAsync(
        Guid userId, string kind, CancellationToken cancellationToken = default) =>
        db.Set<AuthToken>()
            .AsNoTracking()
            .Where(row => row.UserId == userId
                && row.Kind == kind
                && row.RevokedAt == null
                && row.ExpiresAt > clock.GetUtcNow())
            .OrderByDescending(row => row.LastSeenAt)
            .ToListAsync(cancellationToken);

    /// <summary>Распознать ключ интеграции.</summary>
    private async Task<TokenIdentity?> KeyIntrospectAsync(string presented, CancellationToken cancellationToken)
    {
        var selector = presented.Substring(KeyPrefix.Length, SelectorLength);

        var found = await (
            from secret in db.Set<AuthTokenSecret>()
            join owner in db.Set<AuthToken>() on secret.TokenId equals owner.Id
            where secret.Selector == selector && secret.Kind == SecretKinds.Key
            select new { secret.Hash, secret.ExpiresAt, Token = owner })
            .AsNoTracking()
            .FirstOrDefaultAsync(cancellationToken);

        if (found is null || !SecretMatches(presented, found.Hash))
        {
            // ⚠ Один и тот же ответ на «селектора нет» и «секрет не тот»: иначе перебор по селектору отличал бы существующие ключи от несуществующих.
            log.LogWarning("api_key_unknown {Selector}", selector);

            throw Expired(ErrorCodes.SessionExpired, "Ключ интеграции недействителен.");
        }

        var now = clock.GetUtcNow();

        if (found.Token.RevokedAt is not null)
        {
            throw Expired(CodeFor(found.Token.RevokedReason), Explain(found.Token.RevokedReason));
        }

        if (found.ExpiresAt <= now || found.Token.ExpiresAt <= now)
        {
            // ⚠ ⚠ Событием безопасности — своим подключением, а не в единицу работы запроса (решение 0110, находка 28).
            if (истёкшие.ПораСообщить(found.Token.Id))
            {
                await security.RecordAsync(
                    new SecurityEvent(
                        ApiTokenActions.ExpiredPresented,
                        ActorId: found.Token.UserId,
                        Summary: "Предъявлен истёкший ключ интеграции — интеграцию пора выключить или выдать новый ключ",
                        Details: new Dictionary<string, object?>
                        {
                            ["token_id"] = found.Token.Id,
                            ["name"] = found.Token.Name,
                        }),
                    cancellationToken);
            }

            throw Expired(ErrorCodes.SessionExpired, "Срок ключа интеграции истёк.");
        }

        // ⚠ Отметка подтверждения у ключа интеграции не читается и не ставится.
        var identity = new TokenIdentity(
            found.Token.UserId,
            found.Token.Id,
            AuditActorType.ApiToken,
            ScopesOf(found.Token),
            StepUpAt: null,
            Kind: found.Token.Kind);

        предъявленный.Remember(identity, found.Token.Kind);

        touch.Schedule(found.Token.Id, found.Token.LastSeenAt);

        return identity;
    }

    /// <summary>Выдать пару, заведя строку или продлив существующую.</summary>
    private async Task<TokenPair> IssueAsync(
        TokenIssueRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        Guid? existing = null,
        DateTimeOffset? existingExpires = null)
    {
        if (request.Kind == TokenKinds.ApiKey)
        {
            return await IssueKeyAsync(request, now, cancellationToken);
        }

        // ⚠ ⚠ Билет на скачивание — своим путём (решение 0110, разбор 2026-10-21, находка 27).
        if (request.Kind == TokenKinds.DownloadTicket)
        {
            return IssueTicket(request, now);
        }

        var accessMinutes = Math.Max(1, await settings.GetAsync(TokenSettings.AccessTtlMinutes, cancellationToken));
        var idleMinutes = Math.Max(1, await settings.GetAsync(TokenSettings.SessionIdleMinutes, cancellationToken));
        // ⚠ Горизонт настольного клиента свой (решение 0034).
        var absoluteHours = request.Kind == TokenKinds.Device
            ? 24 * Math.Max(1, await settings.GetAsync(TokenSettings.DeviceAbsoluteDays, cancellationToken))
            : Math.Max(1, await settings.GetAsync(TokenSettings.SessionAbsoluteHours, cancellationToken));

        var tokenId = existing ?? Guid.CreateVersion7();
        var expires = existingExpires ?? now.AddHours(absoluteHours);

        if (existing is null)
        {
            // ⚠ ДО добавления строки: после — новая сессия уже в трекере, и устройство стало бы знакомым само себе.
            await УведомитьОНезнакомомAsync(request, cancellationToken);

            db.Add(new AuthToken
            {
                Id = tokenId,
                UserId = request.UserId,
                Kind = request.Kind,
                Name = request.Name,
                Address = request.Address,
                DeviceRef = request.DeviceRef,
                CreatedAt = now,
                LastSeenAt = now,
                ExpiresAt = expires,
                StepUpAt = request.StepUpAt,
            });
        }

        var access = NewSecret();
        var refresh = NewSecret();

        var accessExpires = now.AddMinutes(accessMinutes);

        // ⚠ Срок обновления упирается в абсолютный срок строки.
        var refreshExpires = Min(now.AddMinutes(idleMinutes), expires);

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Access,
            Hash = Hash(access),
            IssuedAt = now,
            ExpiresAt = accessExpires,
        });

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Refresh,
            Hash = Hash(refresh),
            IssuedAt = now,
            ExpiresAt = refreshExpires,
        });

        return new TokenPair(access, accessExpires, refresh, refreshExpires, tokenId);
    }

    /// <summary>Наибольший срок билета на скачивание: билет одноразовый по смыслу.</summary>
    internal static readonly TimeSpan TicketMaxLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Срок билета, если вызывающий его не назвал.</summary>
    private static readonly TimeSpan TicketDefaultLifetime = TimeSpan.FromMinutes(2);

    /// <summary>Выдать билет на скачивание: один секрет доступа со сроком запроса, без обновления.</summary>
    private TokenPair IssueTicket(TokenIssueRequest request, DateTimeOffset now)
    {
        var срок = request.Lifetime is { } заданный && заданный > TimeSpan.Zero
            ? (заданный < TicketMaxLifetime ? заданный : TicketMaxLifetime)
            : TicketDefaultLifetime;

        var expires = now.Add(срок);
        var tokenId = Guid.CreateVersion7();

        db.Add(new AuthToken
        {
            Id = tokenId,
            UserId = request.UserId,
            Kind = TokenKinds.DownloadTicket,
            Name = request.Name,
            Address = request.Address,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = expires,
        });

        var access = NewSecret();

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Access,
            Hash = Hash(access),
            IssuedAt = now,
            ExpiresAt = expires,
        });

        return new TokenPair(access, expires, string.Empty, expires, tokenId);
    }

    /// <summary>Завести ключ интеграции.</summary>
    private async Task<TokenPair> IssueKeyAsync(
        TokenIssueRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var предел = Math.Max(1, await settings.GetAsync(TokenSettings.MaxLifetimeDays, cancellationToken));
        var срок = request.Lifetime ?? TimeSpan.FromDays(предел);

        if (срок <= TimeSpan.Zero || срок > TimeSpan.FromDays(предел))
        {
            throw new AppException(
                ErrorCodes.Validation,
                $"Срок ключа — от одного дня до {предел}.",
                System.Net.HttpStatusCode.BadRequest);
        }

        var selector = NewSelector();
        var secret = $"{KeyPrefix}{selector}_{NewSecret()}";
        var expires = now.Add(срок);
        var tokenId = Guid.CreateVersion7();

        db.Add(new AuthToken
        {
            Id = tokenId,
            UserId = request.UserId,
            Kind = TokenKinds.ApiKey,
            Name = request.Name,
            Address = request.Address,
            Scopes = System.Text.Json.JsonSerializer.Serialize(request.Scopes ?? []),
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = expires,
        });

        db.Add(new AuthTokenSecret
        {
            Id = Guid.CreateVersion7(),
            TokenId = tokenId,
            Kind = SecretKinds.Key,
            Selector = selector,
            Hash = Hash(secret),
            IssuedAt = now,
            ExpiresAt = expires,
        });

        audit.Write(new AuditEntry(
            ApiTokenActions.Created,
            ActorId: request.UserId,
            ResourceType: TokenResources.ApiKey,
            ResourceId: tokenId,
            Changes: new Dictionary<string, object?>
            {
                // ⚠ Самого ключа здесь нет и быть не может.
                ["name"] = request.Name,
                ["expires_at"] = expires,
                ["scopes"] = request.Scopes ?? [],
            }));

        // ⚠ Секрет обновления не выдаётся: срок обновления равен сроку ключа, и второй возвращаемой строкой идёт пустая.
        return new TokenPair(secret, expires, string.Empty, expires, tokenId);
    }

    /// <summary>Восемь символов открытого селектора.</summary>
    private static string NewSelector() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders
            .Base64UrlEncode(RandomNumberGenerator.GetBytes(6))[..SelectorLength];

    /// <summary>Вид субъекта по виду токена.</summary>
    private static AuditActorType ActorTypeOf(string kind) =>
        kind == TokenKinds.ApiKey ? AuditActorType.ApiToken : AuditActorType.User;

    /// <summary>Права ключа.</summary>
    private static IReadOnlyList<string>? ScopesOf(AuthToken token) =>
        token.Scopes is null
            ? null
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(token.Scopes);

    private static AppException Expired(string code, string message) =>
        new(code, message, System.Net.HttpStatusCode.Unauthorized);

    /// <summary>Машинный код отказа по причине закрытия.</summary>
    private static string CodeFor(string? reason) => reason switch
    {
        RevokeReasons.UserInactive => ErrorCodes.UserInactive,

        // ⚠ ⚠ Отзыв устройства — свой код (ревью 2026-09-15, S2).
        RevokeReasons.DeviceRevoked or LegacyDeviceRevoked => ErrorCodes.DeviceRevoked,

        // ⚠ SESSION_REVOKED, а НЕ новый TOKEN_REVOKED, и это не забывчивость.
        _ => ErrorCodes.SessionRevoked,
    };

    /// <summary>Человеческое объяснение, почему доступ кончился.</summary>
    private static string Explain(string? reason) => reason switch
    {
        RevokeReasons.SignedOut => "Вы вышли из системы.",
        RevokeReasons.Revoked => "Сессия закрыта. Войдите заново.",
        RevokeReasons.Stolen => "Обнаружено повторное использование токена — сессия закрыта. "
            + "Если это были не вы, сообщите администратору.",
        RevokeReasons.UserInactive => "Учётная запись отключена.",
        RevokeReasons.PasswordChanged => "Пароль изменён — войдите заново.",

        // ⚠ Две строки ДОПИСАНЫ при переезде (этап 10.5.4), и это не украшение.
        RevokeReasons.TwoFactorEnabled => "Вы включили второй фактор — прежние сессии закрыты. "
            + "Войдите заново.",
        RevokeReasons.TwoFactorReset => "Администратор снял второй фактор — прежние сессии "
            + "закрыты. Войдите заново.",
        RevokeReasons.DeviceRevoked or LegacyDeviceRevoked => "Это устройство отозвано. Ключи "
            + "офлайн-копии на нём стираются; чтобы работать дальше, войдите и зарегистрируйте его заново.",
        RevokeReasons.DeviceReissued => "Этому устройству выдан новый токен — войдите заново.",

        _ => "Сессия кончилась. Войдите заново.",
    };

    /// <summary>Причина отзыва устройства в прежней записи — свободным текстом.</summary>
    private const string LegacyDeviceRevoked = "устройство отозвано";

    private static DateTimeOffset Min(DateTimeOffset first, DateTimeOffset second) =>
        first < second ? first : second;

    /// <summary>32 байта случайности в URL-безопасном виде.</summary>
    private static string NewSecret() =>
        Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Длина секрета в символах: 32 байта в base64url без выравнивания.</summary>
    private const int SecretLength = 43;

    /// <summary>Годна ли приставка ключа: строчные латинские, цифры, подчёркивание на конце, до 16 знаков.</summary>
    internal static void CheckPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > 16 || !prefix.EndsWith('_')
            || !prefix.All(знак => знак is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        {
            throw new InvalidOperationException(
                $"Приставка ключа интеграции «{prefix}» не годится: строчные латинские буквы, цифры и подчёркивание, на конце подчёркивание, не длиннее 16 знаков.");
        }
    }

    /// <summary>Длина открытого селектора.</summary>
    private const int SelectorLength = 8;

    /// <summary>Полная длина ключа: приставка, селектор, разделитель, секрет.</summary>
    private int KeyLength => KeyPrefix.Length + SelectorLength + 1 + SecretLength;

    /// <summary>Похоже ли предъявленное на <b>наш</b> секрет.</summary>
    internal bool Ours(string token) => Ключ(token) || Сессия(token);

    /// <summary>Секрет сессии или устройства: ровно сорок три символа base64url.</summary>
    private static bool Сессия(string token) =>
        token.Length == SecretLength && Base64Url(token);

    /// <summary>Ключ интеграции: <c>eams_ak_&lt;селектор:8&gt;_&lt;секрет:43&gt;</c>.</summary>
    private bool Ключ(string token) =>
        token.Length == KeyLength
        && token.StartsWith(KeyPrefix, StringComparison.Ordinal)
        && token[KeyPrefix.Length + SelectorLength] == '_'
        && Base64Url(token.AsSpan(KeyPrefix.Length, SelectorLength))
        && Base64Url(token.AsSpan(KeyPrefix.Length + SelectorLength + 1));

    /// <summary>Все символы — из алфавита base64url без выравнивания.</summary>
    private static bool Base64Url(ReadOnlySpan<char> value)
    {
        foreach (var symbol in value)
        {
            var allowed = symbol is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                or '-' or '_';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 от секрета.</summary>
    private static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    /// <summary>Совпал ли предъявленный секрет с хранимым хешем — за постоянное время.</summary>
    internal static bool SecretMatches(string presented, byte[] stored) =>
        CryptographicOperations.FixedTimeEquals(Hash(presented), stored);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is not null)
        {
            return;
        }

        throw new InvalidOperationException(
            "Выдача токенов обязана идти внутри транзакции вызывающего: иначе вход состоится "
            + "отдельно от обнуления счётчика неудач и от строки журнала (правило 15).");
    }
}
