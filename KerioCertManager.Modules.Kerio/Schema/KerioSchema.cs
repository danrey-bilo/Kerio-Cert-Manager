using KerioCertManager.Modules.Kerio.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Modules.Core.Data;

namespace KerioCertManager.Modules.Kerio.Schema;

/// <summary>Набор миграций таблиц серверов Kerio и журнала установок.</summary>
internal sealed class KerioSchema(DbContextOptions<KerioSchema> options)
    : ModuleSchemaContext(options, [new KerioTables()])
{
    /// <summary>Имя набора — оно же в имени таблицы истории.</summary>
    public const string Module = "kerio";

    public static ModuleSchema Declaration { get; } = new(Module, typeof(KerioSchema), connection => Create(connection));

    public static KerioSchema Create(string connection) => new(
        new DbContextOptionsBuilder<KerioSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(KerioSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Для <c>dotnet ef migrations add</c>.</summary>
internal sealed class KerioSchemaFactory : IDesignTimeDbContextFactory<KerioSchema>
{
    /// <inheritdoc />
    public KerioSchema CreateDbContext(string[] args) => KerioSchema.Create("Host=design-time;Database=design-time");
}
