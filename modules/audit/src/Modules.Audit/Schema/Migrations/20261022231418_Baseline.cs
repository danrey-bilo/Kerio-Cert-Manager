// РАЗРУШАЮЩИЕ ОПЕРАЦИИ — ТОЛЬКО В `Down`: он сносит журнал целиком, со всеми партициями и записями.
// ОБРАТИМОСТЬ: на боевой базе — только восстановлением из копии; `Down` годится лишь для пересборки
// пустого окружения: неизменяемый журнал повторным `Up` не воскресить.
using Modules.Audit.Contracts;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Audit.Schema.Migrations
{
    /// <summary>
    /// Базовая миграция журнала: партиционированная таблица, цепочка хешей, функции обслуживания, окно
    /// времени (решение 0114).
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ <b>Тело написано руками, сгенерированное удалено</b> — как у прежней миграции хоста
    /// <c>AuditLog</c>: EF не умеет партиционирования и не знает ни цепочки, ни триггеров, ни функций.
    /// Снимок модели не тронут, следующая генерация повторного создания таблицы не даст.
    ///
    /// ⚠ Шаги и порядок — те же, что у прежних миграций хоста: <c>AuditLog</c>, <c>AuditHashChain</c>,
    /// <c>AuditPartitionFunctions</c>, частичные индексы <c>Стадия234_Индексы</c>,
    /// <c>AuditTimeWindow</c>. SQL — из типов модуля (<see cref="AuditSchema"/>, <see cref="AuditChain"/>,
    /// <see cref="AuditPrivileges"/>): правка их меняет и эту миграцию на новых установках, и это та же
    /// цена, что была у хоста. Что схема совпадает с прежней, держит интеграционный сторож.
    ///
    /// ⚠ На существующей установке не выполняется, а отмечается: журнал создан прежними миграциями.
    /// </remarks>
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AuditSchema.CreateTable());

            foreach (var sql in AuditSchema.CreateIndexes())
            {
                migrationBuilder.Sql(sql);
            }

            foreach (var sql in AuditSchema.CreateGuards())
            {
                migrationBuilder.Sql(sql);
            }

            // Запасная партиция — первой: строка вне всех диапазонов иначе роняет вставку, а вставка в
            // журнал идёт в транзакции бизнес-операции.
            foreach (var sql in AuditSchema.CreateDefaultPartition())
            {
                migrationBuilder.Sql(sql);
            }

            // Партиции — от даты применения и по часам базы, как у фоновой задачи.
            migrationBuilder.Sql(CreateInitialPartitions);

            migrationBuilder.Sql(AuditChain.CreateHeadTable());

            foreach (var sql in AuditChain.AddColumns())
            {
                migrationBuilder.Sql(sql);
            }

            foreach (var sql in AuditChain.CreateFunctions())
            {
                migrationBuilder.Sql(sql);
            }

            migrationBuilder.Sql(AuditChain.CreateTrigger());

            foreach (var sql in AuditPrivileges.CreateFunctions())
            {
                migrationBuilder.Sql(sql);
            }

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_ip_ts",
                table: "audit_log",
                columns: new[] { "actor_ip", "ts" },
                descending: new[] { false, true },
                filter: "actor_ip IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_request_ts",
                table: "audit_log",
                columns: new[] { "request_id", "ts" },
                descending: new[] { false, true },
                filter: "request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_session_ts",
                table: "audit_log",
                columns: new[] { "session_id", "ts" },
                descending: new[] { false, true },
                filter: "session_id IS NOT NULL");

            foreach (var sql in AuditChain.CreateTimeWindow())
            {
                migrationBuilder.Sql(sql);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var sql in AuditChain.DropTimeWindow())
            {
                migrationBuilder.Sql(sql);
            }

            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AuditPrivileges.EnsurePartition}(date)");
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AuditPrivileges.DropPartition}(text)");
            migrationBuilder.Sql($"DROP TABLE IF EXISTS {AuditSchema.Table} CASCADE");
            migrationBuilder.Sql($"DROP TABLE IF EXISTS {AuditChain.HeadTable}");
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AuditChain.ChainFunction}()");
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AuditChain.FieldFunction}(text)");
            migrationBuilder.Sql($"DROP FUNCTION IF EXISTS {AuditSchema.GuardFunction}()");
        }

        /// <summary>
        /// Текущий месяц и два вперёд — копия блока прежней миграции хоста <c>AuditLog</c>.
        /// </summary>
        /// <remarks>
        /// ⚠ Копия, а не общий код: прежний набор хоста заморожен и своё тело не меняет. Что оба блока
        /// дают одни партиции, держит сторож сравнения схем. Границы якорятся к UTC явно: приведение
        /// даты к <c>timestamptz</c> взяло бы пояс сессии и разошлось бы с фоновой задачей.
        /// </remarks>
        private const string CreateInitialPartitions =
            """
            DO $$
            DECLARE
                month_start date := date_trunc('month', (now() AT TIME ZONE 'UTC'))::date;
                month_next  date;
                partition   text;
                i           int;
            BEGIN
                FOR i IN 0..2 LOOP
                    month_start := (date_trunc('month', (now() AT TIME ZONE 'UTC')) + (i || ' month')::interval)::date;
                    month_next  := (month_start + interval '1 month')::date;
                    partition   := 'audit_log_' || to_char(month_start, 'YYYY_MM');

                    EXECUTE format(
                        'CREATE TABLE IF NOT EXISTS %I PARTITION OF audit_log FOR VALUES FROM (%L) TO (%L)',
                        partition,
                        month_start::text || ' 00:00:00+00',
                        month_next::text || ' 00:00:00+00');

                    -- Триггер TRUNCATE уровня оператора на партиции не клонируется с родителя.
                    EXECUTE format(
                        'DROP TRIGGER IF EXISTS %I ON %I', partition || '_no_truncate', partition);

                    EXECUTE format(
                        'CREATE TRIGGER %I BEFORE TRUNCATE ON %I FOR EACH STATEMENT EXECUTE FUNCTION audit_log_immutable()',
                        partition || '_no_truncate', partition);
                END LOOP;
            END
            $$;
            """;
    }
}
