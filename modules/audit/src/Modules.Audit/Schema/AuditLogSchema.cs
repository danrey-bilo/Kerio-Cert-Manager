using Modules.Audit.Storage;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Audit.Schema;

/// <summary>Контекст схемы модуля — его миграции, снимок и таблица истории (решение 0114).</summary>
internal sealed class AuditLogSchema(DbContextOptions<AuditLogSchema> options) : ModuleSchemaContext(options, [new AuditLogEntryConfiguration()])
{
    /// <summary>Имя модуля — оно же в имени таблицы истории.</summary>
    public const string Module = "audit";

    /// <summary>Объявление набора для проекта.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(AuditLogSchema), connection => Create(connection));

    /// <summary>Контекст схемы по строке подключения.</summary>
    public static AuditLogSchema Create(string connection) => new(
        new DbContextOptionsBuilder<AuditLogSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(AuditLogSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст схемы для инструментов EF: <c>dotnet ef migrations add</c> не подключается к базе.</summary>
internal sealed class AuditLogSchemaFactory : IDesignTimeDbContextFactory<AuditLogSchema>
{
    /// <inheritdoc />
    public AuditLogSchema CreateDbContext(string[] args) => AuditLogSchema.Create("Host=design-time;Database=design-time");
}
