namespace Modules.Audit.Contracts;

/// <summary>Права на таблицы журнала: что можно рабочей роли, что обслуживанию, что никому.</summary>
public static class AuditPrivileges
{
    /// <summary>Функция создания помесячной партиции.</summary>
    public const string EnsurePartition = "audit_ensure_partition";

    /// <summary>Функция удаления просроченной партиции.</summary>
    public const string DropPartition = "audit_drop_partition";

    /// <summary>Две функции обслуживания партиций.</summary>
    public static IReadOnlyList<string> CreateFunctions() =>
    [
        $"""
        CREATE OR REPLACE FUNCTION {EnsurePartition}(p_month date) RETURNS text
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $ensure$
        DECLARE
            start_at date := date_trunc('month', p_month)::date;
            stop_at  date := (date_trunc('month', p_month) + interval '1 month')::date;
            name     text := '{AuditSchema.Table}_' || to_char(start_at, 'YYYY_MM');
        BEGIN
            -- Границы с явным +00: ключ партиционирования это timestamptz, и голая дата
            -- истолковалась бы в поясе сессии, выполняющей DDL. Партиции, созданные на
            -- разных машинах, разъехались бы на несколько часов.
            -- ⚠ Схема названа ЯВНО. `search_path` у функции начинается с `pg_catalog` —
            -- так положено всякой SECURITY DEFINER-функции, иначе подсунутый в чужой схеме
            -- одноимённый `format` или `to_char` выполнился бы от имени владельца. Но
            -- СОЗДАЁТСЯ объект всегда в первой схеме списка, и без `public.` партиция
            -- заводилась бы в `pg_catalog`: «permission denied to create
            -- pg_catalog.audit_log_2026_08». Поймано первым же прогоном.
            EXECUTE format(
                'CREATE TABLE IF NOT EXISTS public.%I PARTITION OF public.{AuditSchema.Table} '
                || 'FOR VALUES FROM (%L) TO (%L)',
                name, start_at::text || ' 00:00:00+00', stop_at::text || ' 00:00:00+00');

            -- ⚠ Триггер TRUNCATE — уровня оператора, и с родителя он НЕ клонируется.
            -- Родительский останавливает TRUNCATE audit_log, а TRUNCATE партиции прошёл бы
            -- мимо, при том что имена партиций предсказуемы. Со снятием прежнего, потому
            -- что CREATE TRIGGER IF NOT EXISTS PostgreSQL не умеет.
            EXECUTE format(
                'DROP TRIGGER IF EXISTS %I ON public.%I', name || '_no_truncate', name);
            EXECUTE format(
                'CREATE TRIGGER %I BEFORE TRUNCATE ON public.%I FOR EACH STATEMENT '
                || 'EXECUTE FUNCTION public.{AuditSchema.GuardFunction}()',
                name || '_no_truncate', name);

            RETURN name;
        END;
        $ensure$
        """,
        $"""
        CREATE OR REPLACE FUNCTION {DropPartition}(p_name text) RETURNS boolean
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $drop$
        BEGIN
            IF p_name !~ '^{AuditSchema.Table}_[0-9][0-9][0-9][0-9]_[0-9][0-9]$' THEN
                RAISE EXCEPTION '{DropPartition}: % не похоже на имя помесячной партиции', p_name
                    USING ERRCODE = 'invalid_parameter_value';
            END IF;

            IF NOT EXISTS (
                SELECT 1
                FROM pg_inherits i
                JOIN pg_class c ON c.oid = i.inhrelid
                WHERE i.inhparent = to_regclass('public.{AuditSchema.Table}') AND c.relname = p_name)
            THEN
                RETURN false;
            END IF;

            EXECUTE format('DROP TABLE public.%I', p_name);

            RETURN true;
        END;
        $drop$
        """,
    ];

    /// <summary>Права на таблицы журнала.</summary>
    public static IReadOnlyList<string> Grants(string app, string maintenance) =>
    [
        $"REVOKE ALL ON {AuditSchema.Table} FROM {app}",
        RevokeOnPartitions(app, maintenance),
        $"GRANT SELECT, INSERT ON {AuditSchema.Table} TO {app}",
        $"GRANT SELECT ON {AuditSchema.DefaultPartition} TO {app}",
        $"GRANT USAGE, SELECT ON SEQUENCE {AuditSchema.Table}_id_seq TO {app}",

        // ⚠ Голова цепочки — единственное место, ПРАВКА которого позволяет переписать историю и сойтись.
        $"REVOKE ALL ON {AuditChain.HeadTable} FROM {app}",
        $"REVOKE ALL ON {AuditChain.HeadTable} FROM {maintenance}",
        $"GRANT SELECT ON {AuditChain.HeadTable} TO {app}",
        $"GRANT SELECT ON {AuditChain.HeadTable} TO {maintenance}",

        $"REVOKE ALL ON {AuditSchema.Table} FROM {maintenance}",
        $"GRANT SELECT, INSERT ON {AuditSchema.Table} TO {maintenance}",
        $"GRANT SELECT ON {AuditSchema.DefaultPartition} TO {maintenance}",
        $"GRANT USAGE, SELECT ON SEQUENCE {AuditSchema.Table}_id_seq TO {maintenance}",

        // ⚠ Сначала отобрать у PUBLIC: функции создаются доступными всем, и без этой строки рабочая роль звала бы обе, то есть получила бы.
        $"REVOKE ALL ON FUNCTION {EnsurePartition}(date) FROM PUBLIC",
        $"REVOKE ALL ON FUNCTION {DropPartition}(text) FROM PUBLIC",
        $"GRANT EXECUTE ON FUNCTION {EnsurePartition}(date) TO {maintenance}",
        $"GRANT EXECUTE ON FUNCTION {DropPartition}(text) TO {maintenance}",
    ];

    /// <summary>Изъять все права обеих ролей на каждой уже заведённой партиции журнала.</summary>
    public static string RevokeOnPartitions(string app, string maintenance) =>
        $"""
        DO $revoke$
        DECLARE
            part regclass;
        BEGIN
            FOR part IN
                SELECT i.inhrelid::regclass FROM pg_inherits i WHERE i.inhparent = to_regclass('public.{AuditSchema.Table}')
            LOOP
                EXECUTE format('REVOKE ALL ON %s FROM %I, %I', part, '{app}', '{maintenance}');
            END LOOP;
        END
        $revoke$
        """;

    /// <summary>Завести партицию месяца — вызов функции, а не DDL.</summary>
    public static string EnsurePartitionCall(DateOnly month) =>
        $"SELECT {EnsurePartition}('{AuditSchema.MonthStart(month):yyyy-MM-dd}'::date)";

    /// <summary>Удалить просроченную партицию — вызов функции, а не DDL.</summary>
    public static string DropPartitionCall(string partition) =>
        $"SELECT {DropPartition}('{partition.Replace("'", "''", StringComparison.Ordinal)}')";
}
