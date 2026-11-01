using Modules.Core.Contracts.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Modules.Core.Data;

/// <summary>Единственный контекст базы данных на всю систему.</summary>
public sealed class AppDbContext(
    DbContextOptions<AppDbContext> options,
    IEnumerable<IEntityConfigurationProvider> configurations)
    : DbContext(options)
{
    /// <summary>Чем этот контекст отличается от другого того же типа: набор конфигураций модулей.</summary>
    internal string ConfigurationKey { get; } = string.Join(
        '\n',
        configurations.Select(провайдер => провайдер.GetType().FullName).Order(StringComparer.Ordinal));

    /// <summary>Общие правила типов для всех модулей: время — в UTC.</summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ModelConventions.Apply(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        foreach (var configuration in configurations)
        {
            configuration.Configure(modelBuilder);
        }
    }
}

/// <summary>Правила типов — одни у контекста работы и у контекстов схемы модулей.</summary>
internal static class ModelConventions
{
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<ВремяВUtc>();
    }
}

/// <summary>Время — в UTC при записи; при чтении база и так отдаёт UTC.</summary>
internal sealed class ВремяВUtc() : ValueConverter<DateTimeOffset, DateTimeOffset>(
    значение => значение.ToUniversalTime(),
    значение => значение);
