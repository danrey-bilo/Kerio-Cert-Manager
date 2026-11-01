using Modules.Tokens.Storage;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Modules.Tokens.Schema;

/// <summary>Контекст схемы модуля — его миграции, снимок и таблица истории (решение 0114).</summary>
internal sealed class TokensSchema(DbContextOptions<TokensSchema> options) : ModuleSchemaContext(options, [new AuthTokenEntities()])
{
    /// <summary>Имя модуля — оно же в имени таблицы истории.</summary>
    public const string Module = "tokens";

    /// <summary>Объявление набора для проекта.</summary>
    public static ModuleSchema Declaration { get; } = new(Module, typeof(TokensSchema), connection => Create(connection));

    /// <summary>Контекст схемы по строке подключения.</summary>
    public static TokensSchema Create(string connection) => new(
        new DbContextOptionsBuilder<TokensSchema>()
            .UseNpgsql(connection, npgsql => npgsql
                .MigrationsAssembly(typeof(TokensSchema).Assembly)
                .MigrationsHistoryTable(ModuleSchema.HistoryTableOf(Module), ModuleSchema.HistorySchema))
            .Options);
}

/// <summary>Контекст схемы для инструментов EF: <c>dotnet ef migrations add</c> не подключается к базе.</summary>
internal sealed class TokensSchemaFactory : IDesignTimeDbContextFactory<TokensSchema>
{
    /// <inheritdoc />
    public TokensSchema CreateDbContext(string[] args) => TokensSchema.Create("Host=design-time;Database=design-time");
}
