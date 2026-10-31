using Modules.Core.Composition;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace KerioCertManager.Api.Infrastructure;

/// <summary>Подключение базы, применение наборов миграций и проверка схемы при старте.</summary>
public static class DatabaseStartup
{
    /// <summary>Строка подключения хоста — для контекстов схемы наборов миграций.</summary>
    internal sealed record SchemaConnection(string Value)
    {
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append("строка скрыта");

            return true;
        }
    }

    /// <summary>Зарегистрировать общий контекст.</summary>
    public static IServiceCollection AddKerioCertManagerDatabase(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContextFactory<AppDbContext>(
            (provider, options) => Configure(options, connectionString, provider));

        services.AddDbContext<AppDbContext>(
            (provider, options) => Configure(options, connectionString, provider),
            optionsLifetime: ServiceLifetime.Singleton);

        services.AddSingleton(new SchemaConnection(connectionString));

        return services;
    }

    private static void Configure(
        DbContextOptionsBuilder options, string connectionString, IServiceProvider provider)
    {
        // ⚠ Перехватчики — синглтоны из контейнера: о фиксации чужой транзакции модулю иначе не узнать.
        options.AddInterceptors(provider.GetServices<IInterceptor>());

        // ⚠ Свой ключ кэша: модель собирается из конфигураций модулей, а ключ по умолчанию видит только тип.
        options.ReplaceService<IModelCacheKeyFactory, AppModelCacheKeyFactory>();

        options
            .UseNpgsql(connectionString, npgsql => npgsql
                .EnableRetryOnFailure(maxRetryCount: 3, TimeSpan.FromSeconds(2), null));

        // ⚠ Сборки миграций у общего контекста нет: схему несут наборы модулей и SchemaMigrator.
    }

    /// <summary>Применить миграции — только из команды <c>migrate</c>, не при старте сервера.</summary>
    public static Task<IReadOnlyList<string>> MigrateAsync(this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        return SchemaMigrator.MigrateAsync(
            app.Services.GetRequiredService<SchemaConnection>().Value,
            Наборы(app),
            app.Services.GetRequiredService<AdvisoryLockSpace>(),
            cancellationToken);
    }

    /// <summary>Наборы миграций в порядке регистрации: модули по порядку подключения, проект последним.</summary>
    internal static IReadOnlyList<ModuleSchema> Наборы(WebApplication app) =>
        [.. app.Services.GetServices<ModuleSchema>()];

    /// <summary>Какие миграции ещё не применены.</summary>
    public static Task<IReadOnlyList<string>> PendingMigrationsAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        return SchemaMigrator.PendingAsync(
            app.Services.GetRequiredService<SchemaConnection>().Value, Наборы(app), cancellationToken);
    }

    /// <summary>Не стартовать, если схема отстала от кода.</summary>
    public static async Task RequireCurrentSchemaAsync(
        this WebApplication app, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var pending = await app.PendingMigrationsAsync(cancellationToken);

        if (pending.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Схема базы отстала от кода: не применено миграций — {pending.Count} "
            + $"({string.Join(", ", pending)}). Приложение не стартует, пока схема не "
            + "накатана: делать это самому означало бы менять базу раньше, чем оператор "
            + "успеет снять копию. Порядок: сначала копия базы, затем "
            + "docker compose run --rm migrate");
    }
}
