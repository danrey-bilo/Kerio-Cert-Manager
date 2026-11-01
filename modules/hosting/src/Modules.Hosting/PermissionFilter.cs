using System.Collections.Concurrent;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Tokens;
using Modules.Core.Errors;
using Modules.Core.Composition;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Modules.Hosting;

/// <summary>Проверка прав на каждом эндпоинте — один раз и для всех модулей.</summary>
public sealed class PermissionFilter(
    ICurrentUser user,
    IPermissionChecker permissions,
    IAuthorizationAuditor auditor,
    ILogger<PermissionFilter> log,
    IStepUpWindow? stepUpWindow = null) : IAsyncAuthorizationFilter, IEndpointGuard
{
    /// <summary>Что объявил эндпоинт — разобранное ОДИН РАЗ, а не на каждый запрос.</summary>
    internal sealed record Объявление(
        bool Открыт,
        bool ПриСменеПароля,
        bool ПриВключенииФактора,
        bool КлючуМожно,
        bool БилетуМожно,
        bool ТребуетВхода,
        bool СужаетПоРесурсу,
        bool СвежийФактор,
        IReadOnlyList<RequirePermissionAttribute> Права);

    private static readonly ConcurrentDictionary<ActionDescriptor, Объявление> Разобранные = new();

    internal static Объявление Разобрать(ActionDescriptor descriptor)
    {
        var открыт = false;
        var приСменеПароля = false;
        var приВключенииФактора = false;
        var ключуМожно = false;
        var билетуМожно = false;
        var требуетВхода = false;
        var сужаетПоРесурсу = false;
        var свежийФактор = false;
        List<RequirePermissionAttribute>? права = null;

        foreach (var пометка in descriptor.EndpointMetadata)
        {
            switch (пометка)
            {
                case PublicEndpointAttribute:
                    открыт = true;
                    break;
                case AllowWhilePasswordChangeRequiredAttribute:
                    приСменеПароля = true;
                    break;
                case AllowWhileTwoFactorEnrollmentRequiredAttribute:
                    приВключенииФактора = true;
                    break;
                case AllowApiTokenAttribute:
                    ключуМожно = true;
                    break;
                case AllowDownloadTicketAttribute:
                    билетуМожно = true;
                    break;
                case AuthenticatedEndpointAttribute:
                    требуетВхода = true;
                    break;
                case ResourceScopedAuthorizationAttribute:
                    сужаетПоРесурсу = true;
                    break;
                case RequireRecentSecondFactorAttribute:
                    свежийФактор = true;
                    break;
                case RequirePermissionAttribute право:
                    (права ??= []).Add(право);
                    break;
                default:
                    break;
            }
        }

        // ⚠ ⚠ Открыт только тот, кто НЕ объявил ничего, кроме открытости (решение 0110, разбор 2026-10-21).
        var толькоОткрыт = открыт && права is null && !требуетВхода && !сужаетПоРесурсу && !свежийФактор;

        return new Объявление(
            толькоОткрыт, приСменеПароля, приВключенииФактора, ключуМожно, билетуМожно,
            требуетВхода, сужаетПоРесурсу, свежийФактор, права ?? []);
    }

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var объявлено = Разобранные.GetOrAdd(context.ActionDescriptor, Разобрать);

        if (объявлено.Открыт)
        {
            return;
        }

        // ⚠ Ограничение проверяется ДО прав и до всего остального.
        if (user.MustChangePassword
            && !объявлено.ПриСменеПароля)
        {
            throw new AppException(
                ErrorCodes.PasswordChangeRequired,
                "Сначала смените пароль",
                System.Net.HttpStatusCode.Forbidden);
        }

        if (user.MustEnrollTwoFactor
            && !объявлено.ПриВключенииФактора)
        {
            throw new AppException(
                ErrorCodes.TwoFactorEnrollmentRequired,
                "Сначала включите второй фактор",
                System.Net.HttpStatusCode.Forbidden);
        }

        // ⚠ Третье ограничение, и умолчание у него ОБРАТНОЕ двум предыдущим: те снимают запрет пометкой, это накладывает запрет и снимается пометкой.
        if (user.ActorType == AuditActorType.ApiToken
            && !объявлено.КлючуМожно)
        {
            throw new AppException(
                ErrorCodes.ApiTokenNotAllowed,
                "Этот адрес закрыт для ключей интеграции",
                System.Net.HttpStatusCode.Forbidden);
        }

        // ⚠ ⚠ Билет на скачивание годится ТОЛЬКО там, где это объявлено, и умолчание здесь обратное всем прочим пометкам.
        if (user.Kind == TokenKinds.DownloadTicket
            && !объявлено.БилетуМожно)
        {
            throw new AppException(
                ErrorCodes.PermissionDenied,
                "Билет на скачивание годится только для раздачи клиента",
                System.Net.HttpStatusCode.Forbidden);
        }

        var required = объявлено.Права;
        var authenticated = объявлено.ТребуетВхода;

        // ⚠ ⚠ Пятое состояние (подэтап 19.5.3): гейта нет, потому что решение принимается по КАЖДОМУ ресурсу внутри действия.
        if (required.Count == 0 && объявлено.СужаетПоРесурсу)
        {
            if (user.Id is null)
            {
                throw new AppException(
                    ErrorCodes.Unauthenticated, "Требуется вход", System.Net.HttpStatusCode.Unauthorized);
            }

            await CheckSecondFactorAsync(объявлено, context.HttpContext.RequestAborted);
            return;
        }

        if (required.Count == 0 && authenticated)
        {
            if (user.Id is null)
            {
                throw new AppException(
                    ErrorCodes.Unauthenticated, "Требуется вход", System.Net.HttpStatusCode.Unauthorized);
            }

            await CheckSecondFactorAsync(объявлено, context.HttpContext.RequestAborted);
            return;
        }

        if (required.Count == 0)
        {
            // ⚠ Не «пропустить, раз ничего не сказано».
            log.LogError(
                "endpoint_without_permission {Action}",
                context.ActionDescriptor.DisplayName);

            throw AppException.PermissionDenied();
        }

        if (user.Id is not { } actor)
        {
            throw new AppException(
                ErrorCodes.Unauthenticated, "Требуется вход", System.Net.HttpStatusCode.Unauthorized);
        }

        foreach (var requirement in required)
        {
            await CheckAsync(context, actor, requirement, context.HttpContext.RequestAborted);
        }
        await CheckSecondFactorAsync(объявлено, context.HttpContext.RequestAborted);
    }

    /// <summary>Свежий второй фактор для действий под <see cref="RequireRecentSecondFactorAttribute"/>.</summary>
    private async Task CheckSecondFactorAsync(Объявление объявлено, CancellationToken cancellationToken)
    {
        if (!объявлено.СвежийФактор)
        {
            return;
        }

        var минут = stepUpWindow is null
            ? IStepUpWindow.DefaultMinutes
            : Math.Max(1, await stepUpWindow.MinutesAsync(cancellationToken));

        if (user.StepUpAt is null || user.StepUpAt < DateTimeOffset.UtcNow.AddMinutes(-минут))
        {
            throw new AppException(ErrorCodes.SecondFactorRequired,
                "Подтвердите личность кодом второго фактора.", System.Net.HttpStatusCode.Forbidden,
                new Dictionary<string, object?>(StringComparer.Ordinal) { ["window_minutes"] = минут });
        }
    }

    /// <summary>Одно требование: решить, записать решение, отказать.</summary>
    private async Task CheckAsync(
        AuthorizationFilterContext context,
        Guid actor,
        RequirePermissionAttribute requirement,
        CancellationToken cancellationToken)
    {
        var (resourceType, resourceId) = Resource(context, requirement);

        if (requirement.ResourceType is { Length: > 0 }
            && resourceId is null
            && !requirement.ResourceOptional)
        {
            await RecordAsync(
                actor, requirement, allow: false, "идентификатор ресурса не разобран",
                resourceType, null, cancellationToken);

            throw AppException.PermissionDenied();
        }

        // ⚠ ⚠ Ресурс необязателен и не назван — право спрашивается ГЛОБАЛЬНО, то есть без типа.
        if (requirement.ResourceOptional && resourceId is null)
        {
            resourceType = null;
        }

        if (user.IsSuperuser && resourceId is null)
        {
            // ⚠ Только ли читает запрос — факт хоста.
            var читает = HttpMethods.IsGet(context.HttpContext.Request.Method)
                || HttpMethods.IsHead(context.HttpContext.Request.Method)
                || HttpMethods.IsOptions(context.HttpContext.Request.Method);

            await RecordAsync(
                actor, requirement, allow: true, "суперпользователь",
                resourceType, resourceId, cancellationToken, bypass: true, readOnly: читает);

            return;
        }

        var allow = await permissions.CanAsync(
            actor, requirement.Permission, resourceType, resourceId, cancellationToken);

        await RecordAsync(
            actor, requirement, allow, allow ? "право выдано" : "права нет",
            resourceType, resourceId, cancellationToken);

        if (!allow)
        {
            throw AppException.PermissionDenied();
        }
    }

    private Task RecordAsync(
        Guid actor,
        RequirePermissionAttribute requirement,
        bool allow,
        string reason,
        string? resourceType,
        Guid? resourceId,
        CancellationToken cancellationToken,
        bool bypass = false,
        bool readOnly = false) =>
        auditor.RecordAsync(
            actor,
            new AuditDecision(allow, requirement.Permission, reason, bypass, readOnly),
            resourceType,
            resourceId,
            cancellationToken);

    /// <summary>Ресурс, на котором проверяется право.</summary>
    private static (string? Type, Guid? Id) Resource(
        AuthorizationFilterContext context, RequirePermissionAttribute requirement)
    {
        if (requirement.ResourceType is not { Length: > 0 } type)
        {
            return (null, null);
        }

        var name = requirement.From ?? "id";

        var raw = context.RouteData.Values.TryGetValue(name, out var routed)
            ? routed?.ToString()
            : context.HttpContext.Request.Query[name].FirstOrDefault();

        return (type, Guid.TryParse(raw, out var id) ? id : null);
    }
}
