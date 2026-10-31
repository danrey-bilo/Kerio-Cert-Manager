// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новые столбцы store, store_checked_at в kerio_servers и новая таблица kerio_notifications.
// ОБРАТИМОСТЬ: `Down` удаляет таблицу журнала оповещений и оба столбца снимка хранилища.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class StoreAndNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "store",
                table: "kerio_servers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "store_checked_at",
                table: "kerio_servers",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "kerio_notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    server_id = table.Column<Guid>(type: "uuid", nullable: true),
                    server_name = table.Column<string>(type: "text", nullable: true),
                    @event = table.Column<string>(name: "event", type: "text", nullable: false),
                    level = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    queued = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kerio_notifications", x => x.id);
                    table.CheckConstraint("ck_kerio_notifications_recipients", "cardinality(recipients) <= 50");
                });

            migrationBuilder.CreateIndex(
                name: "ix_kerio_notifications_at",
                table: "kerio_notifications",
                column: "at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kerio_notifications");

            migrationBuilder.DropColumn(
                name: "store",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "store_checked_at",
                table: "kerio_servers");
        }
    }
}
