using System.Globalization;
using Modules.Core.Errors;
using Npgsql;

namespace Modules.Hosting;

/// <summary>Замок, которым установка держит инвариант «экземпляр ровно один» (решение 0002).</summary>
public sealed class SingleInstance : IAsyncDisposable
{
    /// <summary>Первая половина ключа по умолчанию: «это замок этого приложения, а не чей-то ещё».</summary>
    public const int DefaultLockClass = 0x4D4F4453;

    /// <summary>Известные роли и вторая половина ключа для каждой.</summary>
    private static readonly Dictionary<string, int> Роли = new(StringComparer.Ordinal)
    {
        ["api"] = 1,
        ["worker"] = 2,
    };

    private readonly NpgsqlConnection _connection;
    private readonly ILogger _log;

    /// <summary>Первая половина ключа замка — та, с которой замок взят.</summary>
    private readonly int _класс;

    /// <summary>Соединением пользуются по одному.</summary>
    private readonly SemaphoreSlim _один = new(1, 1);

    private bool _отпущен;

    private SingleInstance(NpgsqlConnection connection, string role, int objectId, int classId, ILogger log)
    {
        _connection = connection;
        _класс = classId;
        _log = log;
        Role = role;
        ObjectId = objectId;
    }

    /// <summary>Роль, чей замок держим.</summary>
    public string Role { get; }

    /// <summary>Вторая половина ключа — та, что различает роли.</summary>
    public int ObjectId { get; }

    /// <summary>Взять замок или отказаться стартовать.</summary>
    public static async Task<SingleInstance> AcquireAsync(
        string connectionString, string role, ILogger log, CancellationToken cancellationToken = default,
        int lockClass = DefaultLockClass, string? help = null)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(log);

        if (!Роли.TryGetValue(role, out var objectId))
        {
            throw new InvalidOperationException(
                $"Неизвестная роль процесса «{role}». Известны: {string.Join(", ", Роли.Keys)}. "
                + "Роль задаёт хост; опечатка в ней подняла бы процесс без замка единственного "
                + "экземпляра, а это ровно то, от чего он защищает." + Подсказка(help));
        }

        var connection = new NpgsqlConnection(Своё(connectionString, role));

        try
        {
            await connection.OpenAsync(cancellationToken);

            if (!await TryLockAsync(connection, lockClass, objectId, cancellationToken))
            {
                var кто = await ДержательAsync(connection, lockClass, objectId, cancellationToken);

                throw new InvalidOperationException(
                    $"Замок роли «{role}» уже занят{кто}: в этой базе работает другой экземпляр. "
                    + "Экземпляр каждой роли ровно один, несколько реплик не поддерживаются. "
                    + "Остановите прежний процесс или проверьте, не осталось ли его после неудачного "
                    + "обновления." + Подсказка(help));
            }
        }
        catch
        {
            await connection.DisposeAsync();

            throw;
        }

        log.LogInformation("single_instance_acquired {Role} {ObjectId}", role, objectId);

        return new SingleInstance(connection, role, objectId, lockClass, log);
    }

    /// <summary>Держим ли замок прямо сейчас.</summary>
    public async Task<bool> HeldAsync(CancellationToken cancellationToken = default)
    {
        await _один.WaitAsync(cancellationToken);

        try
        {
            try
            {
                if (_connection.State == System.Data.ConnectionState.Open
                    && await НаМестеAsync(cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception error) when (Терпимо(error))
            {
                if (_log.IsEnabled(LogLevel.Debug))
                {
                    _log.LogDebug("single_instance_check_failed {Role} {Error}", Role, SafeError.Describe(error));
                }
            }

            return await ВернутьAsync(cancellationToken);
        }
        finally
        {
            _один.Release();
        }
    }

    /// <summary>Держит ли замок наша собственная сессия — вопрос к базе, а не к полю.</summary>
    private async Task<bool> НаМестеAsync(CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM pg_locks
                WHERE locktype = 'advisory'
                  AND classid = @classId AND objid = @objectId
                  AND pid = pg_backend_pid() AND granted)
            """,
            _connection);

        command.Parameters.AddWithValue("classId", _класс);
        command.Parameters.AddWithValue("objectId", ObjectId);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Пересобрать соединение и взять замок заново.</summary>
    private async Task<bool> ВернутьAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_connection.State != System.Data.ConnectionState.Closed)
            {
                await _connection.CloseAsync();
            }

            await _connection.OpenAsync(cancellationToken);

            var вернули = await TryLockAsync(_connection, _класс, ObjectId, cancellationToken);

            _log.Log(
                вернули ? LogLevel.Warning : LogLevel.Error,
                "single_instance_lock_lost {Role} {Reacquired}",
                Role,
                вернули);

            return вернули;
        }
        catch (Exception error) when (Терпимо(error))
        {
            _log.LogError("single_instance_recovery_failed {Role} {Error}", Role, SafeError.Describe(error));

            return false;
        }
    }

    /// <summary>Ошибки, означающие «база сейчас недоступна», а не «в коде дефект».</summary>
    private static bool Терпимо(Exception error) =>
        error is NpgsqlException or TimeoutException or InvalidOperationException or IOException;

    /// <summary>Отпустить замок.</summary>
    public async ValueTask DisposeAsync()
    {
        await _один.WaitAsync();

        // ⚠ Второй вызов ничего не делает.
        if (_отпущен)
        {
            _один.Release();

            return;
        }

        _отпущен = true;

        try
        {
            if (_connection.State == System.Data.ConnectionState.Open)
            {
                await using var command = new NpgsqlCommand(
                    "SELECT pg_advisory_unlock(@classId, @objectId)", _connection);

                command.Parameters.AddWithValue("classId", _класс);
                command.Parameters.AddWithValue("objectId", ObjectId);

                await command.ExecuteScalarAsync();
            }
        }
        catch (Exception error) when (Терпимо(error))
        {
            if (_log.IsEnabled(LogLevel.Debug))
            {
                _log.LogDebug("single_instance_unlock_failed {Role} {Error}", Role, SafeError.Describe(error));
            }
        }
        finally
        {
            await _connection.DisposeAsync();

            _один.Release();
        }
    }

    private static async Task<bool> TryLockAsync(
        NpgsqlConnection connection, int classId, int objectId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT pg_try_advisory_lock(@classId, @objectId)", connection);

        command.Parameters.AddWithValue("classId", classId);
        command.Parameters.AddWithValue("objectId", objectId);

        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>Чем занят держатель — чтобы отказ называл виновника, а не только факт.</summary>
    private static async Task<string> ДержательAsync(
        NpgsqlConnection connection, int classId, int objectId, CancellationToken cancellationToken)
    {
        try
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT a.pid, a.backend_start
                FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid
                WHERE l.locktype = 'advisory'
                  AND l.classid = @classId AND l.objid = @objectId AND l.granted
                LIMIT 1
                """,
                connection);

            command.Parameters.AddWithValue("classId", classId);
            command.Parameters.AddWithValue("objectId", objectId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            if (!await reader.ReadAsync(cancellationToken))
            {
                return string.Empty;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $" (процесс базы {reader.GetInt32(0)}, подключился {reader.GetDateTime(1):u})");
        }
        catch (Exception error) when (Терпимо(error))
        {
            return string.Empty;
        }
    }

    /// <summary>Своя строка подключения: без пула и с внятным именем в <c>pg_stat_activity</c>.</summary>
    private static string Своё(string connectionString, string role) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = ИмяВБазе(role),
        }.ConnectionString;

    /// <summary>Подсказка хоста — отдельным предложением в конце отказа.</summary>
    private static string Подсказка(string? help) =>
        string.IsNullOrWhiteSpace(help) ? string.Empty : " " + help.Trim();

    /// <summary>Как соединение замка называет себя в <c>pg_stat_activity</c>.</summary>
    public static string ИмяВБазе(string role) => $"single-instance:{role}";
}
