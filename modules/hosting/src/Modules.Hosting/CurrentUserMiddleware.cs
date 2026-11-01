using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;

namespace Modules.Hosting;

/// <summary>Личность запроса: заполняется один раз, читается всеми.</summary>
public sealed class RequestCurrentUser : ICurrentUser
{
    public Guid? Id { get; private set; }

    public bool IsSuperuser { get; private set; }

    public Guid? SessionId { get; private set; }
    public string? ClientRef { get; private set; }

    /// <summary>Чем предъявились.</summary>
    public string? Kind { get; private set; }

    public bool MustChangePassword { get; private set; }

    public bool MustEnrollTwoFactor { get; private set; }

    public DateTimeOffset? StepUpAt { get; private set; }

    /// <summary>Чем пришли.</summary>
    public AuditActorType ActorType { get; private set; } = AuditActorType.User;

    /// <summary>Запомнить, кто пришёл.</summary>
    public void Bind(ResolvedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        // ⚠ Отказ, а не молчаливое приведение к человеку.
        if (identity.ActorType == AuditActorType.System)
        {
            throw new InvalidOperationException(
                "Личность запроса назвалась системой. «Система» — это фоновая задача, и по "
                + "HTTP она не приходит; распознаватель обязан назвать человека или ключ API.");
        }

        Id = identity.UserId;
        IsSuperuser = identity.IsSuperuser;
        SessionId = identity.SessionId;
        ClientRef = identity.ClientRef;
        Kind = identity.Kind;
        MustChangePassword = identity.MustChangePassword;
        MustEnrollTwoFactor = identity.MustEnrollTwoFactor;
        StepUpAt = identity.StepUpAt;
        ActorType = identity.ActorType;
    }
}

/// <summary>Спрашивает распознаватели, кто пришёл, и запоминает ответ.</summary>
public sealed class CurrentUserMiddleware(
    RequestCurrentUser current,
    IEnumerable<IIdentityResolver> resolvers,
    IAuditContextAccessor audit) : IMiddleware
{
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        // ⚠ На публичных путях личность не распознаётся вовсе, и это не оптимизация.
        if (context.GetEndpoint()?.Metadata.GetMetadata<PublicEndpointAttribute>() is not null)
        {
            await next(context);

            return;
        }

        var request = new HttpIdentityRequest(context);

        foreach (var resolver in resolvers)
        {
            if (await resolver.ResolveAsync(request, context.RequestAborted) is not { } identity)
            {
                continue;
            }

            current.Bind(identity);

            audit.Bind(new AuditContext(
                ActorId: identity.UserId,
                SessionId: identity.SessionId,
                ActorType: identity.ActorType));

            break;
        }

        await next(context);
    }
}

/// <summary>То немногое из запроса, что видит распознаватель.</summary>
public sealed class HttpIdentityRequest(HttpContext context) : IIdentityRequest
{
    public string? Header(string name) =>
        context.Request.Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    public string? Cookie(string name) =>
        context.Request.Cookies.TryGetValue(name, out var value) ? value : null;
}
