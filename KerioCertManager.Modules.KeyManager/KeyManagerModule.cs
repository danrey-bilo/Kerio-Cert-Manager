using System.Security.Cryptography.X509Certificates;
using KerioCertManager.Modules.KeyManager.Client;
using KerioCertManager.Modules.KeyManager.Contracts;
using KerioCertManager.Modules.KeyManager.Schema;
using KerioCertManager.Modules.KeyManager.Secrets;
using KerioCertManager.Modules.KeyManager.Settings;
using KerioCertManager.Modules.KeyManager.Source;
using KerioCertManager.Modules.KeyManager.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Modules.Core.Composition;
using Modules.Core.Contracts.Audit;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Modules.Core.Features;
using Modules.Crypto;

namespace KerioCertManager.Modules.KeyManager;

/// <summary>Подключение модуля Key Manager Plus.</summary>
public sealed class KeyManagerModuleOptions
{
    /// <summary>Переключатели модуля (своих подвозможностей нет).</summary>
    public KeyManagerModuleFeatures Features { get; } = new();

    /// <summary>Корни доверия вместо системных для соединения с KMP.</summary>
    public X509Certificate2Collection TrustedRoots { get; } = [];
}

/// <summary>Переключатели модуля Key Manager Plus.</summary>
public sealed class KeyManagerModuleFeatures() : ModuleFeatures("keymanager");

/// <summary>Key Manager Plus — источник SSL-сертификатов: опрос, снимок, ключ API, выгрузка.</summary>
public static class KeyManagerModule
{
    public static IMvcBuilder AddKeyManagerModule(
        this IMvcBuilder mvc, Action<KeyManagerModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(mvc);

        var options = new KeyManagerModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<IEntityConfigurationProvider, KeyManagerTables>();
        services.AddModuleSchema(KeyManagerSchema.Declaration);
        services.AddSingleton<ISettingDeclarationProvider, KeyManagerSettingDeclarations>();
        services.AddSingleton<IAuditActionProvider, KeyManagerAuditActions>();

        // ⚠ Запечатанный секрет — вместе со своей целью ротации.
        services.AddScoped<ApiKeyStore>();
        services.AddScoped<IRewrapTarget, ApiKeyRewrap>();

        services.AddScoped<IKeyManagerConnector, KeyManagerConnector>();
        services.AddScoped<CertificateSnapshots>();
        services.AddScoped<ICertificateSource, CertificateSource>();

        services.TryAddSingleton<SourceWakeup>();

        const string модуль = "keymanager";

        // ⚠ Проверка и опрос открывают соединение к KMP по запросу человека.
        services.AddRateLimitRule(new RateLimitRule(
            модуль,
            RateLimitClasses.Sync,
            "/api/keymanager/test, /api/keymanager/poll",
            path => path.Equals("/api/keymanager/test", StringComparison.OrdinalIgnoreCase)
                || path.Equals("/api/keymanager/poll", StringComparison.OrdinalIgnoreCase)));

        services.Require<ISettingsReader>(модуль, "адрес Key Manager Plus и срок опроса некому прочитать");
        services.Require<ISecretEnvelope>(модуль, "ключ API Key Manager Plus нечем запечатать");

        return mvc.AddModule(typeof(KeyManagerModule).Assembly, options.Features);
    }
}
