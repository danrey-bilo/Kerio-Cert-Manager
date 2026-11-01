using Modules.Core.Data;
using Modules.Core.Contracts.Tokens;
using Modules.Identity.Authz;
using Modules.Core.Contracts.Accounts;
using Modules.Identity.Catalog;
using Modules.Identity.Settings;
using Modules.Identity.Contracts;
using Modules.Identity.Permissions;
using Modules.Core.Redaction;
using Modules.Identity.Redaction;
using Modules.Identity.Retention;
using Modules.Identity.Naming;
using Modules.Identity.Notifications;
using Modules.Identity.Storage;
using Modules.Identity.TwoFactor;
using Modules.Identity.Mail;
using Modules.Identity.Users;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Crypto;
using Modules.Core.Features;
using Modules.Core.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Identity.Passwords;

namespace Modules.Identity;

/// <summary>Подключение модуля личностей.</summary>
public static class IdentityModule
{
    /// <summary>Подключить учётные записи.</summary>
    public static IMvcBuilder AddIdentityModule(
        this IMvcBuilder mvc, Action<IdentityModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new IdentityModuleOptions();
        configure?.Invoke(options);
        options.Check();

        var services = mvc.Services;

        services.AddSingleton(options);

        services.AddSingleton<SignInAttempts>();

        services.AddSingleton(options.Features.Filter(new IdentityOwnActions()));
        services.AddSingleton(options.Features.Filter(new IdentityPermissions()));
        services.AddSingleton(options.Features.Filter(new IdentitySettingDeclarations()));
        services.AddSingleton<IEntityConfigurationProvider, IdentityEntities>();

        // ⚠ Свой набор миграций (решение 0114): проект применяет его своей командой миграций.
        services.AddModuleSchema(Schema.IdentitySchema.Declaration);
        services.AddSingleton<ISensitiveKeyProvider, IdentitySensitiveKeys>();

        // ⚠ Имена своих ресурсов для журнала.
        services.AddScoped<IResourceNameResolver, UserNameResolver>();

        // ⚠ Часы регистрирует модуль, которому они нужны.
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IPasswordHasher>(_ => new PasswordHasher(
            maxConcurrency: options.PasswordHashConcurrency, queueLimit: options.PasswordHashQueueLimit));

        services.AddScoped<UserRepository>();
        services.AddScoped<Invitations>();
        services.AddScoped<PasswordPolicy>();
        services.AddScoped<UserService>();
        services.AddScoped<TrustedClients>();
        services.AddScoped<IClientTrust>(sp => sp.GetRequiredService<TrustedClients>());

        if (options.Features.TrustedClients)
        {
            services.Require<ISessionClientBinder>("identity", "доверие браузеру не к чему привязать");
        }

        // ⚠ Письма модуля (этап 17.6): тексты объявляет тот, кто их шлёт, а не почта.
        services.AddScoped<IdentityMailGate>();
        services.AddScoped<MailNotices>();

        // ⚠ Решение 0117: адрес установки нужен уведомлениям при любой почте.
        services.Require<IPublicOrigin>("identity", "ссылки приглашения и сброса не из чего собрать");

        if (options.Features.Mail)
        {
            services.AddSingleton<IMailTemplateProvider, IdentityMailTemplates>();
            services.Require<IMailQueue>("identity", "приглашение, сброс и код входа некому отправить письмом");
            services.Require<IUserLocale>("identity", "язык письма некому назвать");
        }

        services.AddScoped<SecondFactorPolicy>();
        services.AddScoped<LoginCodes>();
        services.AddScoped<LoginFailures>();

        // ⚠ Уведомления о событиях безопасности (подэтап 15.5.10a).
        services.AddScoped<SecurityNotices>();
        services.AddScoped<SignInService>();
        services.AddScoped<TwoFactorService>();

        services.AddScoped<StepUpService>();

        // ⚠ Окно свежего фактора называет модуль (решение 0120): настройка auth.step_up_minutes.
        services.AddScoped<StepUpFreshness>();
        services.AddScoped<IStepUpWindow>(provider => provider.GetRequiredService<StepUpFreshness>());

        services.AddSingleton<ChallengeAttempts>();

        services.AddScoped<AccessRecovery>();

        // ⚠ Перекладывание обёрток при смене мастер-ключа.
        services.AddScoped<IRewrapTarget, TwoFactorRewrap>();

        // ⚠ Память флагов человека НА ЗАПРОС (этап 23.2).
        services.AddScoped<RequestUserFlags>();

        // ⚠ Учётные записи — соседям портом (решение 0108): модуль доступа соединяет с ними роли, команды и выдачи и спрашивает флаги одного человека.
        services.AddScoped<IAccounts, AccountsSource>();

        services.TryAddScoped<IAccountAccess, NoAccountAccess>();

        services.AddScoped<IIdentityResolver, SessionIdentityResolver>();

        // ⚠ Справочник субъектов для СОСЕДНИХ модулей — контракт заведён этапом 18.4 первым серверным потребителем (перепиской).
        services.AddScoped<ISubjectDirectory, SubjectDirectory>();

        services.AddScoped<LinkCleanup>();
        services.AddHostedService<CleanupService>();

        // ⚠ Предупреждение о суперпользователе без второго фактора (решение 0008).
        services.AddHostedService<UnprotectedSuperuserWarning>();

        // ⚠ С этапа 10.5 модуль не выдаёт токенов сам.
        const string токены = " (подключите AddTokensModule() после AddIdentityModule(), решение 0017)";

        services.Require<ITokenIssuer>("identity", "войти будет нельзя" + токены);
        services.Require<ITokenRevoker>("identity", "смена пароля не выгонит прежние сессии" + токены);
        services.Require<ITokenIntrospector>("identity", "предъявленный токен некому проверить" + токены);
        services.Require<IStepUpMarker>("identity", "подтверждение второго фактора не завершит вход" + токены);
        services.Require<ISettingsReader>("identity", "сроки, пороги и правила паролей некому прочитать");

        // ⚠ Крипта хоста — тоже требование (ревизия 2026-10-26, решение 0118).
        services.Require<IIntegrityProtector>("identity", "коды из писем и отпечатки неудачных входов нечем подписать");
        services.Require<ISecretEnvelope>("identity", "секрет второго фактора нечем запечатать");

        // ⚠ ⚠ Пределы частоты своих дорогих путей модуль объявляет сам (решение 0111): числа задаёт проект, а без ограничителя старт падает.
        services.AddRateLimitRule(new RateLimitRule("identity", RateLimitClasses.SignIn,
            "/api/auth/login, /api/auth/two-factor",
            path => path.StartsWithSegments("/api/auth/login", System.StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/api/auth/two-factor", System.StringComparison.OrdinalIgnoreCase)));

        if (options.Features.ExtensionSignIn)
        {
            services.AddRateLimitRule(new RateLimitRule("identity", RateLimitClasses.SignIn,
                "/api/auth/extension/login, /api/auth/extension/two-factor",
                path => path.StartsWithSegments("/api/auth/extension/login", System.StringComparison.OrdinalIgnoreCase)
                    || path.StartsWithSegments("/api/auth/extension/two-factor", System.StringComparison.OrdinalIgnoreCase)));
        }

        // ⚠ ⚠ Двери доказательства личности ВНУТРИ открытой сессии (ревью 2026-09-15, S2): код переподтверждения, фактор и текущий пароль при смене.
        services.AddRateLimitRule(new RateLimitRule("identity", RateLimitClasses.Reauthentication,
            "/api/me/step-up, /api/me/two-factor, /api/me/password",
            path => path.StartsWithSegments("/api/me/step-up", System.StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/api/me/two-factor", System.StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/api/me/password", System.StringComparison.OrdinalIgnoreCase)));

        services.AddRateLimitRule(new RateLimitRule("identity", RateLimitClasses.OneTimeLink,
            "/api/activation, /api/password-reset",
            path => path.StartsWithSegments("/api/activation", System.StringComparison.OrdinalIgnoreCase)
                || path.StartsWithSegments("/api/password-reset", System.StringComparison.OrdinalIgnoreCase)));

        // ⚠ Без подключения части контроллеры модуля не найдутся, и получится 404 БЕЗ единой ошибки при старте.
        return mvc.AddModule(typeof(IdentityModule).Assembly, options.Features);
    }

    /// <summary>Завести первого суперпользователя.</summary>
    public static async Task<Guid> BootstrapSuperuserAsync(
        IServiceProvider services,
        string email,
        string? displayName,
        string password,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();

        var user = await scope.ServiceProvider
            .GetRequiredService<UserService>()
            .BootstrapSuperuserAsync(email, displayName, password, cancellationToken);

        return user.Id;
    }

    /// <summary>Сменить пароль существующего суперпользователя из консоли установки (0084).</summary>
    public static async Task<Guid> SetSuperuserPasswordAsync(
        IServiceProvider services, string email, string password, CancellationToken cancellationToken = default)
    {
        Восстановление(services);

        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserService>()
            .SetSuperuserPasswordAsync(email, password, cancellationToken);
        return user.Id;
    }

    /// <summary>Сбросить фактор суперпользователя с отзывом доступа для новой настройки (0084).</summary>
    public static async Task<Guid> ResetSuperuserTwoFactorAsync(
        IServiceProvider services, string email, CancellationToken cancellationToken = default)
    {
        Восстановление(services);

        await using var scope = services.CreateAsyncScope();
        var user = await scope.ServiceProvider.GetRequiredService<UserService>()
            .RequireSuperuserForRecoveryAsync(email, cancellationToken);
        await scope.ServiceProvider.GetRequiredService<TwoFactorService>()
            .ResetSuperuserForConsoleAsync(user.Id, cancellationToken);
        return user.Id;
    }

    /// <summary>Вернуть доступ к учётной записи из консоли.</summary>
    public static async Task<AccessResetResult> ResetAccessAsync(
        IServiceProvider services, string email, string password, CancellationToken cancellationToken = default)
    {
        Восстановление(services);

        await using var scope = services.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<AccessRecovery>()
            .ResetAsync(email, password, cancellationToken);
    }

    /// <summary>Отказать, если проект выключил восстановление из консоли (решение 0112).</summary>
    private static void Восстановление(IServiceProvider services)
    {
        if (!services.GetRequiredService<IdentityModuleOptions>().Features.ConsoleRecovery)
        {
            throw new Modules.Core.Errors.AppException(
                Modules.Core.Errors.ErrorCodes.PermissionDenied,
                "Восстановление суперпользователя из консоли выключено проектом (identity.console_recovery).",
                System.Net.HttpStatusCode.Forbidden);
        }
    }
}

/// <summary>Настройки подключения модуля, которые задаёт хост.</summary>
public sealed class IdentityModuleOptions
{
    /// <summary>Попыток входа в минуту на пару «адрес + почта».</summary>
    public int SignInAttemptsPerMinute { get; set; } = 10;

    /// <summary>Сколько живёт окно первичной настройки суперпользователя после консольной команды.</summary>
    public TimeSpan SuperuserSetupWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Какие поля человека справочник сотрудников показывает сверх имени.</summary>
    public DirectoryFields DirectoryFields { get; set; } = DirectoryFields.None;

    /// <summary>Издатель в приложении-аутентификаторе — название продукта.</summary>
    public string TotpIssuer { get; set; } = "Vault";

    /// <summary>Кука половины обновления веб-входа.</summary>
    public string RefreshCookieName { get; set; } = "__Host-refresh";

    /// <summary>Кука доверия браузеру.</summary>
    public string TrustCookieName { get; set; } = "__Host-trust";

    /// <summary>Кука половины обновления расширения.</summary>
    public string ExtensionRefreshCookieName { get; set; } = "__Host-extension-refresh";

    /// <summary>Кука доверия расширению.</summary>
    public string ExtensionTrustCookieName { get; set; } = "__Host-extension-trust";

    /// <summary>Идентификатор своего расширения Chrome; не задан — вход расширения не пускает никого.</summary>
    public string? ExtensionId { get; set; }

    /// <summary>Как называть расширение в списке доверенных клиентов и сессий.</summary>
    public string ExtensionClientName { get; set; } = "Browser extension";

    /// <summary>Как называть настольный клиент в списке доверенных клиентов.</summary>
    public string DesktopClientName { get; set; } = "Desktop";

    /// <summary>Код письмом не заменяет подтверждённый аутентификатор.</summary>
    public bool AuthenticatorExcludesEmailCode { get; set; }

    /// <summary>Приложение-аутентификатор обязательно всем.</summary>
    public bool RequireAuthenticatorForEveryone { get; set; }

    /// <summary>Сколько кодов можно предъявить по одной расписке входа; ноль — без предела.</summary>
    public int ChallengeCodeAttempts { get; set; }

    /// <summary>Половина обновления, пришедшая кукой, уходит только кукой.</summary>
    public bool RefreshCookieStaysCookie { get; set; }

    /// <summary>Инвариант «последний суперпользователь» считает только тех, кто может войти.</summary>
    public bool LastSuperuserCountsOnlySignInCapable { get; set; }

    /// <summary>Сколько хешей Argon2 считается одновременно; <c>null</c> — число ядер, не меньше двух.</summary>
    public int? PasswordHashConcurrency { get; set; }

    /// <summary>Сколько проверок пароля может ждать очереди; <c>null</c> — без предела.</summary>
    public int? PasswordHashQueueLimit { get; set; }

    /// <summary>Переключатели подвозможностей.</summary>
    public IdentityModuleFeatures Features { get; } = new();

    /// <summary>Проверить параметры при подключении: отказ сейчас дешевле, чем молча слабая защита.</summary>
    internal void Check()
    {
        string[] куки = [RefreshCookieName, TrustCookieName, ExtensionRefreshCookieName, ExtensionTrustCookieName];

        foreach (var имя in куки)
        {
            if (!имя.StartsWith("__Host-", StringComparison.Ordinal) || имя.Length <= 7
                || имя.Any(знак => знак is <= ' ' or >= (char)127 or '(' or ')' or '<' or '>' or '@' or ',' or ';'
                    or ':' or '\\' or '"' or '/' or '[' or ']' or '?' or '=' or '{' or '}'))
            {
                throw new InvalidOperationException(
                    $"Имя куки «{имя}» не годится: нужна приставка __Host- и знаки токена RFC 6265 (решение 0111).");
            }
        }

        if (куки.Distinct(StringComparer.Ordinal).Count() != куки.Length)
        {
            throw new InvalidOperationException("Имена куки учётных записей совпадают: каждая кука — своя.");
        }

        if (ExtensionId is { Length: > 0 } id && (id.Length != 32 || id.Any(знак => знак is < 'a' or > 'p')))
        {
            throw new InvalidOperationException(
                $"Идентификатор расширения «{id}» не похож на идентификатор Chrome: 32 буквы a–p.");
        }

        if (ChallengeCodeAttempts < 0)
        {
            throw new InvalidOperationException("ChallengeCodeAttempts не бывает меньше нуля: ноль — без предела.");
        }

        if (PasswordHashConcurrency is < 1)
        {
            throw new InvalidOperationException("PasswordHashConcurrency — хотя бы один хеш одновременно.");
        }

        if (PasswordHashQueueLimit is < 0)
        {
            throw new InvalidOperationException("PasswordHashQueueLimit не бывает меньше нуля.");
        }

        if (Features.SelfServiceReset && !Features.Mail)
        {
            throw new InvalidOperationException(
                "Самостоятельный сброс пароля без писем бесполезен: ссылка забывшему уходит только письмом. "
                + "Выключите identity.self_service_reset или включите identity.mail.");
        }
    }
}

/// <summary>Переключатели модуля учётных записей (решение 0111).</summary>
public sealed class IdentityModuleFeatures() : ModuleFeatures("identity")
{
    /// <summary>Справочник сотрудников для выбора людей и команд.</summary>
    [Feature("identity.directory", Description = "Справочник сотрудников для выбора людей и команд: имя, по параметру — адрес и отдел")]
    public bool Directory { get; set; } = true;

    /// <summary>Самостоятельный сброс пароля письмом.</summary>
    [Feature("identity.self_service_reset", Description = "Забывший пароль сам просит ссылку сброса письмом")]
    public bool SelfServiceReset { get; set; } = true;

    /// <summary>Вход расширения браузера.</summary>
    [Feature("identity.extension_sign_in", Description = "Вход расширения браузера паролем и вторым фактором, своё обновление сессии")]
    public bool ExtensionSignIn { get; set; } = true;

    /// <summary>Письма: приглашение, сброс, код входа.</summary>
    [Feature("identity.mail", Description = "Письма учётных записей: приглашение, ссылка сброса, код входа и переподтверждения")]
    public bool Mail { get; set; } = true;

    /// <summary>Доверенные клиенты: вход без второго фактора в пределах срока.</summary>
    [Feature("identity.trusted_clients", Description = "Доверенные браузеры и клиенты: вход без второго фактора в пределах срока, список и отзыв")]
    public bool TrustedClients { get; set; } = true;

    /// <summary>Восстановление суперпользователя из консоли.</summary>
    [Feature("identity.console_recovery", Description = "Смена пароля и сброс второго фактора суперпользователя командой из консоли")]
    public bool ConsoleRecovery { get; set; } = true;

    /// <summary>Переподтверждение личности кодом письмом.</summary>
    [Feature("identity.step_up_email", Description = "Переподтверждение личности кодом письмом: письмо для текущей сессии и приём этого кода")]
    public bool StepUpEmail { get; set; } = true;

    /// <inheritdoc />
    protected override IEnumerable<FeatureOwnership> Owned() =>
    [
        new("identity.directory", Permissions: [Permissions.IdentityPermissions.Directory]),
        new("identity.trusted_clients",
            Settings: [Settings.IdentitySettings.TrustedDeviceDays.Name],
            Actions: [Contracts.IdentityActions.TrustedClientGranted.Name, Contracts.IdentityActions.TrustedClientRenamed.Name]),
    ];
}
