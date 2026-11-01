using Modules.Tokens.Catalog;
using Modules.Tokens.Naming;
using Modules.Tokens.Retention;
using Modules.Tokens.Settings;
using Modules.Tokens.Tokens;
using Modules.Core.Data;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Authz;
using Modules.Core.Contracts.Settings;
using Modules.Core.Contracts.Tokens;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Tokens.Storage;
using Modules.Core.Contracts.Data;
using Modules.Core.Features;
using Modules.Core.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Modules.Tokens;

/// <summary>Подключение модуля предъявляемых токенов.</summary>
public sealed class TokensModuleOptions
{
    /// <summary>Приставка по умолчанию — нейтральная: имя задаёт проект.</summary>
    public const string DefaultApiKeyPrefix = "ak_";

    /// <summary>Приставка ключа интеграции: <c>&lt;приставка&gt;&lt;селектор:8&gt;_&lt;секрет:43&gt;</c>.</summary>
    public string ApiKeyPrefix { get; set; } = DefaultApiKeyPrefix;

    /// <summary>Переключатели подвозможностей.</summary>
    public TokensModuleFeatures Features { get; } = new();
}

/// <summary>Переключатели модуля токенов (решения 0111, 0112).</summary>
public sealed class TokensModuleFeatures() : ModuleFeatures("tokens")
{
    /// <summary>Свои ключи интеграции.</summary>
    [Feature("tokens.api_keys", Description = "Свои ключи интеграции: завести с областью прав, перечислить, отозвать")]
    public bool ApiKeys { get; set; } = true;

    /// <summary>Перечень чужих токенов для администратора.</summary>
    [Feature("tokens.inventory", Description = "Перечень чужих ключей, сессий и устройств для администратора и точечный отзыв")]
    public bool Inventory { get; set; } = true;

    /// <summary>Уведомления человеку о его сессиях.</summary>
    [Feature("tokens.session_notices", Description = "Уведомления о сессиях: вход с незнакомого устройства и повтор потраченного токена обновления")]
    public bool SessionNotices { get; set; } = true;

    /// <summary>Токены машин — долгая пара настольного клиента.</summary>
    [Feature("tokens.device_tokens", Description = "Токены машин: долгая пара настольного клиента со своим горизонтом")]
    public bool DeviceTokens { get; set; } = true;

    /// <summary>Билеты на скачивание установщика.</summary>
    [Feature("tokens.download_tickets", Description = "Билеты на скачивание: короткий токен в куке для страницы загрузки клиента")]
    public bool DownloadTickets { get; set; } = true;

    /// <summary>Выдаётся и опознаётся ли этот вид токена.</summary>
    internal bool Issues(string kind) => kind switch
    {
        TokenKinds.Device => DeviceTokens,
        TokenKinds.DownloadTicket => DownloadTickets,
        TokenKinds.ApiKey => ApiKeys,
        _ => true,
    };

    /// <inheritdoc />
    protected override IEnumerable<FeatureOwnership> Owned() =>
    [
        new("tokens.api_keys",
            Settings: [Settings.TokenSettings.MaxPerUser.Name, Settings.TokenSettings.MaxLifetimeDays.Name],
            Actions: [Contracts.ApiTokenActions.Created.Name, Contracts.ApiTokenActions.KeyRevoked.Name]),
        new("tokens.device_tokens", Settings: [Settings.TokenSettings.DeviceAbsoluteDays.Name]),
        new("tokens.inventory",
            Permissions:
            [
                Permissions.ApiTokenPermissions.Read, Permissions.ApiTokenPermissions.Revoke,
                Permissions.ApiTokenPermissions.Manage, Permissions.ApiTokenPermissions.RevokeAny,
            ]),
    ];
}

public static class TokensModule
{
    /// <summary>Подключить предъявляемые токены.</summary>
    public static IMvcBuilder AddTokensModule(this IMvcBuilder mvc, Action<TokensModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new TokensModuleOptions();

        configure?.Invoke(options);
        Tokens.TokenService.CheckPrefix(options.ApiKeyPrefix);

        var services = mvc.Services;

        services.AddSingleton(options);

        services.AddSingleton<IEntityConfigurationProvider, AuthTokenEntities>();

        // ⚠ Свой набор миграций (решение 0114): проект применяет его своей командой миграций.
        services.AddModuleSchema(Schema.TokensSchema.Declaration);

        services.AddSingleton<Tokens.ExpiredKeyNotices>();

        services.AddSingleton(options.Features.Filter(new ApiTokenOwnActions()));
        services.AddSingleton(options.Features.Filter(new TokenSettingDeclarations()));

        // ⚠ TryAdd, а не Add: соседи регистрируют то же самое, и второй Add молча заменил бы общий источник времени своим.
        services.TryAddSingleton(TimeProvider.System);

        if (options.Features.SessionNotices)
        {
            services.AddScoped<Notifications.SessionNotices>();
        }

        services.AddScoped<TokenService>();
        services.AddScoped<ITokenIssuer>(sp => sp.GetRequiredService<TokenService>());
        services.AddScoped<ITokenIntrospector>(sp => sp.GetRequiredService<TokenService>());

        services.AddScoped<ITokenRevoker, TokenRevoker>();

        // ⚠ Токены машин и билеты — своими контрактами и по выбору проекта (решение 0113).
        if (options.Features.DeviceTokens)
        {
            services.AddScoped<Contracts.IDeviceTokenIssuer, DeviceTokenIssuer>();
            services.AddScoped<Contracts.IDeviceCredentialHorizon, Offline.DeviceCredentialHorizon>();
        }

        if (options.Features.DownloadTickets)
        {
            services.AddScoped<Contracts.IDownloadTicketIssuer, DownloadTicketIssuer>();
        }

        services.AddScoped<IStepUpMarker, StepUpMarker>();
        services.AddScoped<Contracts.ISessionClientBinding, SessionClientBinder>();

        services.AddScoped<ApiKeyService>();

        services.AddScoped<TokenInventory>();
        services.AddSingleton(options.Features.Filter(new Permissions.ApiTokenPermissions()));

        // ⚠ Singleton, и это существенно.
        services.AddSingleton<LastSeenWriter>();

        services.AddSingleton<Modules.Core.Redaction.ISensitiveKeyProvider, Redaction.ApiTokenSensitiveKeys>();

        services.AddScoped<TokenCleanup>();
        services.AddHostedService<CleanupService>();

        // ⚠ Отсутствие чужого контракта роняет не сборку, а ПЕРВЫЙ ЖИВОЙ ЗАПРОС — разбор в самом классе.
        services.Require<IPermissionChecker>("tokens",
            "сужать права ключей не на чем: движок прав даёт модуль доступа, и его подключают раньше");
        services.Require<ISettingsReader>("tokens", "сроки сессий и ключей некому прочитать");

        services.Require<ISecurityEventWriter>("tokens", "предъявленный истёкший ключ некому записать событием безопасности");
        services.Require<IAccounts>("tokens", "инвентарю не узнать, суперпользователь ли владелец строки");

        services.AddScoped<Authz.PresentedToken>();

        services.AddSingleton<Authz.DangerousPermissions>();

        Сузить(services);

        services.AddScoped<IResourceNameResolver, AuthTokenNameResolver>();

        // ⚠ Без подключения части контроллеры модуля не найдутся, и получится 404 БЕЗ единой ошибки при старте.
        return mvc.AddModule(typeof(TokensModule).Assembly, options.Features);
    }

    /// <summary>Обернуть чужую проверку прав своим сужением.</summary>
    private static void Сузить(IServiceCollection services)
    {
        var внутренний = services.LastOrDefault(service => service.ServiceType == typeof(IPermissionChecker))
            ?? throw new InvalidOperationException(
                "Обёртка сужения прав не нашла, что оборачивать: IPermissionChecker никем не "
                + "зарегистрирован. AddTokensModule() обязан идти ПОСЛЕ AddAccessModule() — его даёт модуль доступа; "
                + "иначе ключ интеграции получит полные права своего хозяина.");

        services.Remove(внутренний);

        services.Add(new ServiceDescriptor(
            typeof(IPermissionChecker),
            provider => new Authz.ScopedPermissionChecker(
                (IPermissionChecker)Родить(внутренний, provider),
                provider.GetRequiredService<ICurrentUser>(),
                provider.GetRequiredService<Authz.PresentedToken>(),
                provider.GetRequiredService<Authz.DangerousPermissions>()),
            внутренний.Lifetime));
    }

    /// <summary>Построить то, что было зарегистрировано до подмены.</summary>
    private static object Родить(ServiceDescriptor описание, IServiceProvider provider) =>
        описание switch
        {
            { ImplementationFactory: { } фабрика } => фабрика(provider),
            { ImplementationInstance: { } экземпляр } => экземпляр,
            { ImplementationType: { } тип } => ActivatorUtilities.CreateInstance(provider, тип),
            _ => throw new InvalidOperationException(
                "IPermissionChecker зарегистрирован способом, которого обёртка сужения не знает."),
        };
}
