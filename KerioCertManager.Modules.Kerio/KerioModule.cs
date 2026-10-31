using KerioCertManager.Modules.Kerio.Client;
using KerioCertManager.Modules.Kerio.Contracts;
using KerioCertManager.Modules.Kerio.Localization;
using KerioCertManager.Modules.Kerio.Notifications;
using KerioCertManager.Modules.Kerio.Schema;
using KerioCertManager.Modules.Kerio.Secrets;
using KerioCertManager.Modules.Kerio.Settings;
using KerioCertManager.Modules.Kerio.Storage;
using KerioCertManager.Modules.Kerio.Sync;
using KerioCertManager.Modules.KeyManager.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Core.Composition;
using Modules.Core.Contracts.Accounts;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Mail;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Features;
using Modules.Crypto;

namespace KerioCertManager.Modules.Kerio;

/// <summary>Подключение модуля Kerio Connect.</summary>
public sealed class KerioModuleOptions
{
    /// <summary>Поднимать ли фоновый цикл синхронизации в этом процессе; решает хост по роли процесса.</summary>
    public bool RunWorker { get; set; }

    /// <summary>Разрешить адреса Kerio на этом компьютере (loopback) — только тесты и разработка.</summary>
    public bool AllowLoopback { get; set; }

    /// <summary>Переключатели модуля (своих подвозможностей нет).</summary>
    public KerioModuleFeatures Features { get; } = new();
}

/// <summary>Переключатели модуля Kerio Connect.</summary>
public sealed class KerioModuleFeatures() : ModuleFeatures("kerio");

/// <summary>Kerio Connect: серверы, проверка, установка сертификатов из Key Manager Plus, уведомления.</summary>
public static class KerioModule
{
    public static IMvcBuilder AddKerioModule(this IMvcBuilder mvc, Action<KerioModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new KerioModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IEntityConfigurationProvider, KerioTables>();
        services.AddModuleSchema(KerioSchema.Declaration);
        services.AddSingleton<ISettingDeclarationProvider, KerioSettingDeclarations>();
        services.AddSingleton<IMailTemplateProvider, KerioMail>();
        services.TryAddSingleton(_ => ServerTexts.FromAssembly());
        services.AddSingleton<IAuditActionProvider, KerioAuditActions>();

        // ⚠ Запечатанный пароль — вместе со своей целью ротации мастер-ключа.
        services.AddScoped<ServerPasswords>();
        services.AddScoped<IRewrapTarget, ServerPasswordRewrap>();

        services.TryAddSingleton<ITlsInspector, TlsInspector>();
        services.TryAddScoped<IKerioConnector, KerioConnector>();

        services.AddScoped<ServerInspection>();
        services.AddScoped<KerioDeployer>();
        services.AddScoped<KerioNotifier>();
        services.AddScoped<ServerLeases>();
        services.AddScoped<SyncCycle>();

        services.TryAddSingleton<SourceWakeup>();

        if (options.RunWorker)
        {
            services.AddHostedService<SyncWorker>();
        }

        const string модуль = "kerio";

        // ⚠ Проба и тест открывают соединения к любому узлу и порту, проверка входит в Kerio паролем даже после отказа.
        services.AddRateLimitRule(new RateLimitRule(
            модуль,
            RateLimitClasses.Sync,
            "/api/kerio/servers/probe, /api/kerio/servers/test, /api/kerio/servers/{id}/check",
            ДорогойПуть));

        services.Require<ICertificateSource>(модуль, "сертификаты для установки неоткуда взять — подключите модуль Key Manager Plus");
        services.Require<IMailQueue>(модуль, "отчёты об установке и предупреждения некому отправить письмом");
        services.Require<ISettingsReader>(модуль, "настройки установки некому прочитать");
        services.Require<ISecretEnvelope>(модуль, "пароль учётной записи Kerio нечем запечатать");
        services.Require<IAccounts>(модуль, "получателей писем по умолчанию неоткуда взять");

        return mvc.AddModule(typeof(KerioModule).Assembly, options.Features);
    }

    /// <summary>Пути, что ходят в сеть к Kerio по запросу человека.</summary>
    internal static bool ДорогойПуть(Microsoft.AspNetCore.Http.PathString path)
    {
        if (!path.StartsWithSegments("/api/kerio/servers", StringComparison.OrdinalIgnoreCase, out var остаток))
        {
            return false;
        }

        var части = (остаток.Value ?? string.Empty).ToLowerInvariant().Split('/', StringSplitOptions.RemoveEmptyEntries);

        return части switch
        {
            ["probe" or "test"] => true,
            [_, "check"] => true,
            _ => false,
        };
    }
}
