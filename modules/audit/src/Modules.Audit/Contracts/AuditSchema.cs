namespace Modules.Audit.Contracts;

/// <summary>SQL журнала: имена партиций, их создание и защита от изменения.</summary>
public static class AuditSchema
{
    /// <summary>Имя таблицы.</summary>
    public const string Table = "audit_log";

    /// <summary>Запасная партиция.</summary>
    public const string DefaultPartition = "audit_log_default";

    /// <summary>Имя функции, роняющей любую попытку изменить журнал.</summary>
    public const string GuardFunction = "audit_log_immutable";

    /// <summary>Имя помесячной партиции.</summary>
    public static string PartitionName(DateOnly month) => $"{Table}_{month.Year:D4}_{month.Month:D2}";

    /// <summary>Разобрать имя партиции обратно в месяц.</summary>
    public static bool TryParsePartition(string name, out DateOnly month)
    {
        month = default;

        if (!name.StartsWith($"{Table}_", StringComparison.Ordinal))
        {
            return false;
        }

        var tail = name[(Table.Length + 1)..];

        return DateOnly.TryParseExact(
            $"{tail}_01", "yyyy_MM_dd", null, System.Globalization.DateTimeStyles.None, out month);
    }

    /// <summary>Первое число месяца, к которому относится дата.</summary>
    public static DateOnly MonthStart(DateOnly date) => new(date.Year, date.Month, 1);

    /// <summary>Создание таблицы: партиционирование, ключ, колонки.</summary>
    public static string CreateTable() =>
        $"""
        CREATE TABLE {Table} (
            id            bigserial   NOT NULL,
            ts            timestamptz NOT NULL DEFAULT now(),
            actor_id      uuid        NULL,
            actor_type    text        NOT NULL DEFAULT 'user',
            actor_ip      inet        NULL,
            user_agent    text        NULL,
            permission    text        NULL,
            action        text        NOT NULL,
            resource_type text        NULL,
            resource_id   uuid        NULL,
            result        text        NOT NULL DEFAULT 'allow',
            reason        text        NULL,
            changes       jsonb       NULL,
            request_id    text        NULL,
            session_id    uuid        NULL,
            occurred_at   timestamptz NULL,
            CONSTRAINT pk_{Table} PRIMARY KEY (id, ts),
            CONSTRAINT ck_{Table}_actor_type CHECK (actor_type IN ('user', 'api_token', 'system')),
            CONSTRAINT ck_{Table}_result CHECK (result IN ('allow', 'deny'))
        ) PARTITION BY RANGE (ts)
        """;

    /// <summary>Индексы под порядок выдачи и под фильтры.</summary>
    public static IReadOnlyList<string> CreateIndexes() =>
    [
        $"CREATE INDEX ix_{Table}_ts_id ON {Table} (ts DESC, id DESC)",
        $"CREATE INDEX ix_{Table}_actor_ts ON {Table} (actor_id, ts DESC, id DESC)",
        $"CREATE INDEX ix_{Table}_resource_ts ON {Table} (resource_type, resource_id, ts DESC)",
        $"CREATE INDEX ix_{Table}_result_ts ON {Table} (result, ts DESC)",
    ];

    /// <summary>Неизменяемость на уровне базы: функция и два триггера.</summary>
    public static IReadOnlyList<string> CreateGuards() =>
    [
        $"""
        CREATE OR REPLACE FUNCTION {GuardFunction}() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN
            RAISE EXCEPTION 'audit_log is append-only: % is not allowed', TG_OP
                USING ERRCODE = 'restrict_violation';
        END;
        $$
        """,
        $"""
        CREATE TRIGGER {Table}_append_only
            BEFORE UPDATE OR DELETE ON {Table}
            FOR EACH ROW EXECUTE FUNCTION {GuardFunction}()
        """,
        $"""
        CREATE TRIGGER {Table}_no_truncate
            BEFORE TRUNCATE ON {Table}
            FOR EACH STATEMENT EXECUTE FUNCTION {GuardFunction}()
        """,
    ];

    /// <summary>Запасная партиция и её защита.</summary>
    public static IReadOnlyList<string> CreateDefaultPartition() =>
    [
        $"CREATE TABLE {DefaultPartition} PARTITION OF {Table} DEFAULT",
        TruncateGuard(DefaultPartition),
    ];

    /// <summary>Создание помесячной партиции вместе с её защитой — одним списком.</summary>
    public static IReadOnlyList<string> CreatePartition(DateOnly month)
    {
        var start = MonthStart(month);
        var next = start.AddMonths(1);
        var name = PartitionName(start);

        return
        [
            $"""
            CREATE TABLE IF NOT EXISTS {name} PARTITION OF {Table}
                FOR VALUES FROM ('{start:yyyy-MM-dd} 00:00:00+00') TO ('{next:yyyy-MM-dd} 00:00:00+00')
            """,
            TruncateGuard(name),
        ];
    }

    /// <summary>Триггер <c>TRUNCATE</c> для одной партиции — см.</summary>
    public static string TruncateGuard(string partition) =>
        $"""
        DROP TRIGGER IF EXISTS {partition}_no_truncate ON {partition};
        CREATE TRIGGER {partition}_no_truncate
            BEFORE TRUNCATE ON {partition}
            FOR EACH STATEMENT EXECUTE FUNCTION {GuardFunction}()
        """;
}
