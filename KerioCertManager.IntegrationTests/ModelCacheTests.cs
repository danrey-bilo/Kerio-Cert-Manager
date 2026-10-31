using KerioCertManager.Api.Infrastructure;
using Modules.Core.Contracts.Data;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace KerioCertManager.IntegrationTests;

/// <summary>Кэш модели EF не делится между разными наборами конфигураций.</summary>
public sealed class ModelCacheTests
{
    /// <summary>Модель строится без базы.</summary>
    private const string Строка = "Host=нет;Database=нет;Username=нет;Password=нет";

    /// <summary>Одна таблица, которой нет ни в одном модуле.</summary>
    private sealed class Отличие : IEntityConfigurationProvider
    {
        public void Configure(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.Entity<Метка>().ToTable("проба_кэша_модели").HasKey(строка => строка.Id);
        }
    }

    /// <summary>Другая конфигурация того же счёта.</summary>
    private sealed class ДругоеОтличие : IEntityConfigurationProvider
    {
        public void Configure(ModelBuilder modelBuilder)
        {
            ArgumentNullException.ThrowIfNull(modelBuilder);

            modelBuilder.Entity<ВтораяМетка>().ToTable("проба_кэша_вторая").HasKey(строка => строка.Id);
        }
    }

    /// <summary>Сущность-метка.</summary>
    public sealed class Метка
    {
        public Guid Id { get; set; }
    }

    /// <summary>Метка второго набора.</summary>
    public sealed class ВтораяМетка
    {
        public Guid Id { get; set; }
    }

    [Fact]
    public void Модель_собирается_по_своему_набору_конфигураций_а_не_по_чужому()
    {
        using var мигратор = new ServiceCollection()
            .AddKerioCertManagerDatabase(Строка)
            .BuildServiceProvider();

        var пустая = мигратор.GetRequiredService<AppDbContext>().Model;

        using var приложение = new ServiceCollection()
            .AddKerioCertManagerDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, Отличие>()
            .BuildServiceProvider();

        var своя = приложение.GetRequiredService<AppDbContext>().Model;

        Assert.Empty(пустая.GetEntityTypes());
        Assert.NotEmpty(своя.GetEntityTypes());

        Assert.Contains(своя.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
    }

    // ⚠ Ключ кэша — состав конфигураций, а не их число.
    [Fact]
    public void Наборы_одного_размера_но_разного_состава_не_делят_модель()
    {
        using var первый = new ServiceCollection()
            .AddKerioCertManagerDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, Отличие>()
            .BuildServiceProvider();

        using var второй = new ServiceCollection()
            .AddKerioCertManagerDatabase(Строка)
            .AddSingleton<IEntityConfigurationProvider, ДругоеОтличие>()
            .BuildServiceProvider();

        var модельПервого = первый.GetRequiredService<AppDbContext>().Model;
        var модельВторого = второй.GetRequiredService<AppDbContext>().Model;

        Assert.Contains(модельПервого.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
        Assert.Contains(модельВторого.GetEntityTypes(), тип => тип.ClrType == typeof(ВтораяМетка));
        Assert.DoesNotContain(модельВторого.GetEntityTypes(), тип => тип.ClrType == typeof(Метка));
    }
}
