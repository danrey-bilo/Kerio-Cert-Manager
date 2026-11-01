using System.Security.Cryptography;
using System.Text;

namespace Modules.Audit.Contracts;

/// <summary>Цепочка хешей поверх журнала: канонический вид строки, его SQL и его же арифметика.</summary>
public static class AuditChain
{
    /// <summary>Таблица голов цепочек: по строке на месяц.</summary>
    public const string HeadTable = "audit_chain";

    /// <summary>Функция обрамления одного поля.</summary>
    public const string FieldFunction = "audit_chain_field";

    /// <summary>Функция-триггер, ведущая цепочку.</summary>
    public const string ChainFunction = "audit_log_chain";

    /// <summary>Байт-заполнитель на месте <c>NULL</c>: дефис.</summary>
    public const byte NullMarker = 0x2D;

    /// <summary>Как каждое поле превращается в текст — <b>одним списком на всю систему</b>.</summary>
    private static readonly string[] Rendering =
    [
        "{0}chain_seq::text",
        "{0}id::text",
        """to_char({0}ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')""",
        "{0}actor_id::text",
        "{0}actor_type",
        "{0}actor_ip::text",
        "{0}user_agent",
        "{0}permission",
        "{0}action",
        "{0}resource_type",
        "{0}resource_id::text",
        "{0}result",
        "{0}reason",
        "{0}changes::text",
        "{0}request_id",
        "{0}session_id::text",
        """to_char({0}occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')""",
    ];

    /// <summary>Сколько полей входит в отпечаток строки.</summary>
    public static int FieldCount => Rendering.Length;

    /// <summary>Первое звено цепочки месяца.</summary>
    public static byte[] Genesis(string partition) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(partition));

    /// <summary>Отпечаток строки: предыдущий хеш и поля в каноническом виде.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> previous, IReadOnlyList<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        if (fields.Count != Rendering.Length)
        {
            throw new ArgumentException(
                $"Ожидалось {Rendering.Length} полей, пришло {fields.Count}", nameof(fields));
        }

        var bytes = new List<byte>(previous.Length + 256);
        bytes.AddRange(previous);

        foreach (var field in fields)
        {
            if (field is null)
            {
                bytes.Add(NullMarker);
                continue;
            }

            var value = Encoding.UTF8.GetBytes(field);

            bytes.AddRange(Encoding.UTF8.GetBytes($"{value.Length}:"));
            bytes.AddRange(value);
        }

        return SHA256.HashData([.. bytes]);
    }

    /// <summary>Колонки цепочки на самом журнале.</summary>
    public static IReadOnlyList<string> AddColumns() =>
    [
        $"ALTER TABLE {AuditSchema.Table} ADD COLUMN chain_seq bigint NULL",
        $"ALTER TABLE {AuditSchema.Table} ADD COLUMN prev_hash bytea NULL",
        $"ALTER TABLE {AuditSchema.Table} ADD COLUMN hash bytea NULL",
    ];

    /// <summary>Функция окна времени строки.</summary>
    public const string TimeWindowFunction = "audit_log_time_window";

    /// <summary>Время строки — в окне вокруг часов базы: задним числом в журнал не пишут.</summary>
    public static IReadOnlyList<string> CreateTimeWindow() =>
    [
        $"""
        CREATE OR REPLACE FUNCTION {TimeWindowFunction}() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $window$
        BEGIN
            IF session_user <> current_user
               AND (NEW.ts < now() - interval '1 hour' OR NEW.ts > clock_timestamp() + interval '1 hour')
            THEN
                RAISE EXCEPTION 'audit row time is outside the allowed window'
                    USING ERRCODE = 'check_violation';
            END IF;

            RETURN NEW;
        END
        $window$
        """,
        $"""
        CREATE TRIGGER {AuditSchema.Table}_time_window
            BEFORE INSERT ON {AuditSchema.Table}
            FOR EACH ROW EXECUTE FUNCTION {TimeWindowFunction}()
        """,
    ];

    /// <summary>Снять окно времени — для отката миграции.</summary>
    public static IReadOnlyList<string> DropTimeWindow() =>
    [
        $"DROP TRIGGER IF EXISTS {AuditSchema.Table}_time_window ON {AuditSchema.Table}",
        $"DROP FUNCTION IF EXISTS {TimeWindowFunction}()",
    ];

    /// <summary>Таблица голов.</summary>
    public static string CreateHeadTable() =>
        $"""
        CREATE TABLE {HeadTable} (
            partition  text        NOT NULL PRIMARY KEY,
            last_seq   bigint      NOT NULL,
            last_hash  bytea       NOT NULL,
            updated_at timestamptz NOT NULL DEFAULT now()
        )
        """;

    /// <summary>Функции: обрамление поля и сам триггер.</summary>
    public static IReadOnlyList<string> CreateFunctions() =>
    [
        $"""
        CREATE OR REPLACE FUNCTION {FieldFunction}(v text) RETURNS bytea
        LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $field$
            SELECT CASE
                WHEN v IS NULL THEN '\x2d'::bytea
                ELSE convert_to(octet_length(convert_to(v, 'UTF8'))::text || ':', 'UTF8')
                     || convert_to(v, 'UTF8')
            END
        $field$
        """,
        $"""
        CREATE OR REPLACE FUNCTION {ChainFunction}() RETURNS trigger
        LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, public AS $chain$
        DECLARE
            part text;
            prev bytea;
            seq  bigint;
        BEGIN
            part := '{AuditSchema.Table}_' || to_char(NEW.ts AT TIME ZONE 'UTC', 'YYYY_MM');

            -- Голова берётся ПОД ЗАМОК и держится до конца транзакции вставляющего: только
            -- так две одновременные записи получают разные звенья, а не одно и то же.
            -- Здесь же цена конструкции — запись в журнал одного месяца последовательна.
            LOOP
                SELECT c.last_seq, c.last_hash INTO seq, prev
                FROM public.{HeadTable} c WHERE c.partition = part FOR UPDATE;

                EXIT WHEN FOUND;

                -- Первой строке месяца головы ещё нет. Заводим — и повторяем, если нас
                -- опередили: соперник мог не зафиксироваться, и молча взять его значение
                -- нельзя.
                BEGIN
                    INSERT INTO public.{HeadTable} (partition, last_seq, last_hash)
                    VALUES (part, 0, sha256(convert_to(part, 'UTF8')));

                    seq  := 0;
                    prev := sha256(convert_to(part, 'UTF8'));

                    EXIT;
                EXCEPTION WHEN unique_violation THEN
                    NULL;
                END;
            END LOOP;

            NEW.chain_seq := seq + 1;
            NEW.prev_hash := prev;
            NEW.hash := sha256(prev || {TriggerBytes()});

            UPDATE public.{HeadTable}
            SET last_seq = NEW.chain_seq, last_hash = NEW.hash, updated_at = now()
            WHERE partition = part;

            RETURN NEW;
        END;
        $chain$
        """,
    ];

    /// <summary>Сам триггер на родителе — на партиции его клонирует PostgreSQL.</summary>
    public static string CreateTrigger() =>
        $"""
        CREATE TRIGGER {AuditSchema.Table}_chain
            BEFORE INSERT ON {AuditSchema.Table}
            FOR EACH ROW EXECUTE FUNCTION {ChainFunction}()
        """;

    /// <summary>Выборка строк месяца в каноническом виде — ею проверяют цепь и ею же выгружают.</summary>
    public static string SelectCanonical(string from, string to) =>
        $"""
        SELECT chain_seq AS link_seq, prev_hash AS link_prev, hash AS link_hash, {Projection()}
        FROM {AuditSchema.Table}
        WHERE ts >= {from} AND ts < {to}
        ORDER BY link_seq NULLS FIRST, {AuditSchema.Table}.id
        """;

    /// <summary>Месяцы, в которых есть строки, — по ним и идёт проверка.</summary>
    public static string SelectMonths() =>
        $"""
        SELECT DISTINCT date_trunc('month', ts AT TIME ZONE 'UTC')::date
        FROM {AuditSchema.Table}
        ORDER BY 1
        """;

    /// <summary>Поля строки в каноническом виде — для выборки.</summary>
    private static string Projection() =>
        string.Join(", ", Rendering.Select(field => string.Format(null, field, string.Empty)));

    /// <summary>Те же поля, обрамлённые длиной, — для триггера.</summary>
    private static string TriggerBytes() =>
        string.Join(
            " || ",
            Rendering.Select(field => $"{FieldFunction}({string.Format(null, field, "NEW.")})"));
}
