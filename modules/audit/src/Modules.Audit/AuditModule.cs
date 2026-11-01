using Modules.Audit.Catalog;
using Modules.Audit.Contracts;
using Modules.Audit.Detection;
using Modules.Audit.Integrity;
using Modules.Audit.Partitioning;
using Modules.Audit.Permissions;
using Modules.Audit.Reading;
using Modules.Audit.Retention;
using Modules.Audit.Settings;
using Modules.Audit.Storage;
using Modules.Audit.Writing;
using Modules.Core.Contracts.Audit;
using Modules.Core.Errors;
using Modules.Core.Contracts.Data;
using Modules.Core.Contracts.Settings;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Modules.Core.Features;
using Modules.Core.Composition;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Modules.Audit;

/// <summary>Настройки подключения модуля, которые задаёт хост.</summary>
public sealed class AuditModuleOptions
{
    /// <summary>Падать ли на событии, которого нет в справочнике.</summary>
    public bool StrictActionCatalog { get; set; }

    /// <summary>Обслуживать ли партиции и срок хранения в этом процессе.</summary>
    public bool RunMaintenance { get; set; }

    /// <summary>Отдельное подключение под удаление старых партиций.</summary>
    public string? MaintenanceConnectionString { get; set; }

    /// <summary>Нижняя граница срока хранения журнала, месяцев.</summary>
    public int MinimumRetentionMonths { get; set; } = 1;

    /// <summary>Сколько ждать между назначением удаления партиции и самим удалением.</summary>
    public TimeSpan RetentionDropDelay { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Окно журнала по умолчанию: без явного <c>since</c> страница и выгрузка смотрят столько назад.</summary>
    public TimeSpan? DefaultWindow { get; set; } = TimeSpan.FromDays(365);

    /// <summary>Переключатели подвозможностей.</summary>
    public AuditModuleFeatures Features { get; } = new();
}

/// <summary>Переключатели модуля журнала (решения 0111, 0112).</summary>
public sealed class AuditModuleFeatures() : ModuleFeatures("audit")
{
    /// <summary>Выгрузка журнала файлом.</summary>
    [Feature("audit.export", Description = "Выгрузка журнала файлом CSV или JSON с потолком строк")]
    public bool Export { get; set; } = true;

    /// <summary>Детектор массового показа секретов.</summary>
    [Feature("audit.mass_reveal", Description = "Счёт показов секретов одним человеком и тревога о массовом выносе")]
    public bool MassRevealDetection { get; set; } = true;

    /// <summary>Удаление журнала по сроку хранения.</summary>
    [Feature("audit.retention", Description = "Удаление старых партиций журнала по сроку хранения: назначение, отсрочка, отмена")]
    public bool Retention { get; set; } = true;

    /// <summary>Чтение журнала в приложении.</summary>
    [Feature("audit.read_api", Description = "Чтение журнала в приложении: страница с фильтрами и справочник событий")]
    public bool ReadApi { get; set; } = true;

    /// <inheritdoc />
    protected override IEnumerable<FeatureOwnership> Owned() =>
    [
        new("audit.export",
            Permissions: [Permissions.AuditPermissions.Export],
            Settings: [Settings.AuditSettings.ExportRowLimit.Name],
            Actions: [Contracts.AuditActions.Export.Name]),
        new("audit.mass_reveal",
            Settings: [Settings.AuditSettings.MassRevealThreshold.Name, Settings.AuditSettings.MassRevealWindowSeconds.Name],
            Actions: [Contracts.AuditActions.MassReveal.Name]),
        new("audit.retention",
            Settings: [Settings.AuditSettings.RetentionMonths.Name],
            Actions:
            [
                Contracts.AuditActions.Retention.Name, Contracts.AuditActions.RetentionScheduled.Name,
                Contracts.AuditActions.RetentionCancelled.Name,
            ]),
    ];
}

/// <summary>Подключение модуля журнала.</summary>
public static class AuditModule
{
    /// <summary>Подключить журнал.</summary>
    public static IMvcBuilder AddAuditModule(this IMvcBuilder mvc, Action<AuditModuleOptions>? configure = null)
    {
        var options = new AuditModuleOptions();
        configure?.Invoke(options);

        var services = mvc.Services;
        services.AddSingleton(options);

        services.AddSingleton(options.Features.Filter(new AuditOwnActions()));
        services.AddSingleton(options.Features.Filter(new AuditPermissions()));
        services.AddSingleton(options.Features.Filter(new AuditSettingDeclarations()));

        services.AddSingleton<IEntityConfigurationProvider, AuditLogEntryConfiguration>();

        // ⚠ Свой набор миграций (решение 0114): проект применяет его своей командой миграций.
        services.AddModuleSchema(Schema.AuditLogSchema.Declaration);

        services.AddSingleton<AuditActionCatalog>();

        // ⚠ И сразу собираем его при старте.
        services.AddHostedService<AuditCatalogService>();

        services.AddSingleton<ChangesRedactor>();

        services.AddScoped<ScopedAuditContextAccessor>();
        services.AddScoped<IAuditContextAccessor>(sp => sp.GetRequiredService<ScopedAuditContextAccessor>());
        services.AddScoped<IAuditWriter, AuditWriter>();

        services.AddScoped<AuditReader>();
        services.AddScoped<ResourceNames>();

        services.AddSingleton<SuperuserReadRepeats>();
        services.AddScoped<IAuthorizationAuditor, AuthorizationAuditor>();

        services.AddScoped<ISecurityEventWriter, SecurityEventWriter>();

        if (!options.Features.MassRevealDetection)
        {
            services.AddScoped<ISecretAccessMonitor, NoSecretAccessMonitor>();
        }
        else
        {
            services.AddScoped<ISecretAccessMonitor>(sp => new MassRevealDetector(
                Redis(sp),
                sp.GetRequiredService<IDbContextFactory<AppDbContext>>(),
                sp.GetRequiredService<ISettingsReader>(),
                sp.GetRequiredService<ChangesRedactor>(),
                sp.GetRequiredService<IAuditContextAccessor>(),
                sp.GetServices<IAuditAlertSink>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<MassRevealDetector>>()));
        }

        if (options.Features.Retention)
        {
            services.AddScoped<AuditRetention>();
            services.AddHostedService<RetentionService>();
        }

        services.AddScoped<ChainVerifier>();
        services.AddScoped<ChainAnchor>();

        // ⚠ Приёмник якоря по умолчанию — лог (решение 0113).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditAnchorSink, LogAnchorSink>());
        services.AddHostedService<ChainAnchorService>();

        services.AddScoped<PartitionMaintenance>();
        services.AddHostedService<PartitionMaintenanceService>();
        services.AddScoped<PartitionsHealthCheck>();
        services.AddHealthChecks().AddCheck<PartitionsHealthCheck>("audit_partitions", tags: ["ready"]);

        services.TryAddSingletonTimeProvider();

        services.Require<ISettingsReader>("audit", "срок хранения и пороги журнала некому прочитать");

        // ⚠ Выгрузка — тяжёлая работа на один запрос (ревизия 2026-10-26, решение 0118).
        if (options.Features.Export)
        {
            services.AddRateLimitRule(new RateLimitRule("audit", RateLimitClasses.BulkTransfer,
                "/api/audit/export", path => path.StartsWithSegments("/api/audit/export", StringComparison.OrdinalIgnoreCase)));
        }

        // ⚠ Без подключения части контроллеры модуля не найдутся, и получится 404 БЕЗ единой ошибки при старте.
        return mvc.AddModule(typeof(AuditModule).Assembly, options.Features);
    }

    /// <summary>Пересчитать цепочку журнала — точка входа для команды хоста.</summary>
    public static async Task<ChainCheckResult> CheckChainAsync(
        IServiceProvider services,
        DateOnly? month = null,
        TextWriter? export = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        await using var scope = services.CreateAsyncScope();

        var report = await scope.ServiceProvider
            .GetRequiredService<ChainVerifier>().VerifyAsync(month, export, cancellationToken);

        return new ChainCheckResult(
        [
            .. report.Months.Select(
                item => new ChainCheckLine(item.Partition, item.Chained, item.Unchained, item.Broken)),
        ]);
    }

    /// <summary>Часы берутся из контейнера, чтобы тест мог их подменить.</summary>
    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    /// <summary>Подключение к кэшу, если оно есть и отзывается.</summary>
    private static IConnectionMultiplexer? Redis(IServiceProvider services)
    {
        try
        {
            return services.GetService<IConnectionMultiplexer>();
        }
        catch (Exception error)
        {
            services.GetRequiredService<ILogger<MassRevealDetector>>().LogWarning(
                "mass_reveal_detector_unavailable {Error}", SafeError.Describe(error));

            return null;
        }
    }
}
