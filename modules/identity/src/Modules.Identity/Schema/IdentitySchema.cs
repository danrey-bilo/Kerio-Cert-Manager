using Modules.Identity.Storage;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Identity.Schema;

/// <summary>Контекст схемы модуля — его миграции, снимок и таблица истории (решение 0114).</summary>
internal sealed class IdentitySchema(DbContextOptions<IdentitySchema> options) : ModuleSchemaContext(options, [new IdentityEntities()])
{
    /// <summary>Имя модуля — оно же в имени таблицы истории.</summary>
    public const string Module = "identity";

    /// <summary>Объявление набора для проекта.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(IdentitySchema), connection => Create(connection));

    /// <summary>Контекст схемы по строке подключения.</summary>
    public static IdentitySchema Create(string connection) => new(
        new DbContextOptionsBuilder<IdentitySchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(IdentitySchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст схемы для инструментов EF: <c>dotnet ef migrations add</c> не подключается к базе.</summary>
internal sealed class IdentitySchemaFactory : IDesignTimeDbContextFactory<IdentitySchema>
{
    /// <inheritdoc />
    public IdentitySchema CreateDbContext(string[] args) => IdentitySchema.Create("Host=design-time;Database=design-time");
}
