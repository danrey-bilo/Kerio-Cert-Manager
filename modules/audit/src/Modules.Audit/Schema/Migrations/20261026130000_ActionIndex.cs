// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется индекс журнала по событию, строки не меняются.
// ОБРАТИМОСТЬ: откатывается `Down` — индекс снимается со всех партиций, данные не затрагиваются.
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Audit.Schema.Migrations
{
    /// <summary>
    /// Индекс журнала по событию — под фильтр <c>action</c> страницы и выгрузки (решение 0118).
    /// </summary>
    /// <remarks>
    /// ⚠ ⚠ Ревизия 2026-10-26: фильтр по событию шёл обратным проходом `ix_audit_log_ts_id` по всем
    /// партициям с проверкой каждой строки, пока не наберётся страница; для редкого события это весь
    /// журнал за срок хранения. Индекс на родителе: PostgreSQL заводит его на каждой партиции, и на
    /// будущих тоже. ⚠ Тело руками, как у базовой: модель EF журнала не знает партиционирования, и
    /// снимок модели не меняется.
    /// </remarks>
    [DbContext(typeof(AuditLogSchema))]
    [Migration("20261026130000_ActionIndex")]
    public partial class ActionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS ix_audit_log_action_ts ON audit_log (action, ts DESC, id DESC)");

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_audit_log_action_ts");
    }
}
