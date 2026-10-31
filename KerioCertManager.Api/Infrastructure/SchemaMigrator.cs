using Modules.Core.Composition;
using Modules.Core.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KerioCertManager.Api.Infrastructure;

/// <summary>Схема установки: наборы миграций модулей по порядку подключения, набор проекта последним.</summary>
public static class SchemaMigrator
{
    /// <summary>Номер замка прохода миграций в пространстве проекта.</summary>
    internal const ushort AdvisoryLockNumber = 1;

    /// <summary>Выполнить наборы по порядку.</summary>
    public static async Task<IReadOnlyList<string>> MigrateAsync(
        string connection,
        IReadOnlyList<ModuleSchema> schemas,
        AdvisoryLockSpace locks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(locks);

        var сделано = new List<string>();

        // ⚠ Замок сессии на отдельном подключении: снимется сам, если процесс умрёт.
        await using var замок = new NpgsqlConnection(connection);
        await замок.OpenAsync(cancellationToken);
        await ВыполнитьAsync(замок, $"SELECT pg_advisory_lock({locks.Key(AdvisoryLockNumber)})", cancellationToken);

        try
        {
            foreach (var набор in schemas)
            {
                await using var db = набор.Create(connection);

                сделано.AddRange((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"{набор.Module}: {м}"));
                await db.Database.MigrateAsync(cancellationToken);
            }
        }
        finally
        {
            await ВыполнитьAsync(замок, $"SELECT pg_advisory_unlock({locks.Key(AdvisoryLockNumber)})", CancellationToken.None);
        }

        return сделано;
    }

    /// <summary>Что не применено — строками «набор: миграция»; пусто — схема на месте.</summary>
    public static async Task<IReadOnlyList<string>> PendingAsync(
        string connection, IReadOnlyList<ModuleSchema> schemas, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        var недостаёт = new List<string>();

        foreach (var набор in schemas)
        {
            await using var db = набор.Create(connection);

            недостаёт.AddRange((await db.Database.GetPendingMigrationsAsync(cancellationToken)).Select(м => $"{набор.Module}: {м}"));
        }

        return недостаёт;
    }

    private static async Task ВыполнитьAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
