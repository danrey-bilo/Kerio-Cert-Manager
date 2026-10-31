// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новые таблицы keymanager_state и keymanager_certificates.
// ОБРАТИМОСТЬ: `Down` удаляет обе таблицы; ключ API придётся ввести заново.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "keymanager_certificates",
                columns: table => new
                {
                    common_name = table.Column<string>(type: "text", nullable: false),
                    serial_number = table.Column<string>(type: "text", nullable: false),
                    expires_on = table.Column<DateOnly>(type: "date", nullable: true),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_keymanager_certificates", x => new { x.common_name, x.serial_number });
                });

            migrationBuilder.CreateTable(
                name: "keymanager_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    api_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_poll_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_poll_error = table.Column<string>(type: "text", nullable: true),
                    last_poll_count = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_keymanager_state", x => x.id);
                    table.CheckConstraint("ck_keymanager_state_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "keymanager_certificates");

            migrationBuilder.DropTable(
                name: "keymanager_state");
        }
    }
}
