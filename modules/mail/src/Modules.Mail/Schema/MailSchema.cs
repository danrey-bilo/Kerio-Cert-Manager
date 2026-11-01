using Modules.Mail.Storage;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Mail.Schema;

/// <summary>Контекст схемы модуля — его миграции, снимок и таблица истории (решение 0114).</summary>
internal sealed class MailSchema(DbContextOptions<MailSchema> options) : ModuleSchemaContext(options, [new MailEntities()])
{
    /// <summary>Имя модуля — оно же в имени таблицы истории.</summary>
    public const string Module = "mail";

    /// <summary>Объявление набора для проекта.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(MailSchema), connection => Create(connection));

    /// <summary>Контекст схемы по строке подключения.</summary>
    public static MailSchema Create(string connection) => new(
        new DbContextOptionsBuilder<MailSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(MailSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст схемы для инструментов EF: <c>dotnet ef migrations add</c> не подключается к базе.</summary>
internal sealed class MailSchemaFactory : IDesignTimeDbContextFactory<MailSchema>
{
    /// <inheritdoc />
    public MailSchema CreateDbContext(string[] args) => MailSchema.Create("Host=design-time;Database=design-time");
}
