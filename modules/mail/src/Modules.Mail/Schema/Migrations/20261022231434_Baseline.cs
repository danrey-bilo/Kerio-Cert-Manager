// РАЗРУШАЮЩИЕ ОПЕРАЦИИ — ТОЛЬКО В `Down`: он сносит таблицы модуля вместе с данными (`mail_outbox`, `system_secrets`).
// ОБРАТИМОСТЬ: на боевой базе — только восстановлением из копии; `Down` годится лишь для пересборки
// пустого окружения. На поднятой установке базовая не выполняется, а отмечается (решение 0114).
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Mail.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mail_outbox",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    to_address = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<byte[]>(type: "bytea", nullable: false),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mail_outbox", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "system_secrets",
                columns: table => new
                {
                    key = table.Column<string>(type: "text", nullable: false),
                    @sealed = table.Column<byte[]>(name: "sealed", type: "bytea", nullable: false),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_system_secrets", x => x.key);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mail_outbox_pending",
                table: "mail_outbox",
                column: "next_attempt_at",
                filter: "sent_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_mail_outbox_rate",
                table: "mail_outbox",
                columns: new[] { "to_address", "kind", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mail_outbox");

            migrationBuilder.DropTable(
                name: "system_secrets");
        }
    }
}
