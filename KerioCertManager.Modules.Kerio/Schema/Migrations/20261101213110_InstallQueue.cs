// РАЗРУШАЮЩИЕ ОПЕРАЦИИ: удаляются deploy_requested_at/by/name в kerio_servers — ждущий ручной запрос сначала
// переносится в kerio_install_requests (запрос на все сертификаты — строкой на каждое выбранное имя).
// ОБРАТИМОСТЬ: `Down` возвращает столбцы с самым ранним запросом сервера и удаляет очередь и слот установки.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class InstallQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "install_queue",
                table: "kerio_servers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.CreateTable(
                name: "kerio_install_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    common_name = table.Column<string>(type: "text", nullable: false),
                    requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    requested_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kerio_install_requests", x => x.id);
                    table.ForeignKey(
                        name: "fk_kerio_install_requests_server",
                        column: x => x.server_id,
                        principalTable: "kerio_servers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "kerio_install_slot",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kerio_install_slot", x => x.id);
                    table.CheckConstraint("ck_kerio_install_slot_single", "id = 1");
                });

            migrationBuilder.CreateIndex(
                name: "ux_kerio_install_requests_server_name",
                table: "kerio_install_requests",
                columns: new[] { "server_id", "common_name" },
                unique: true);

            migrationBuilder.Sql("INSERT INTO kerio_install_slot (id) VALUES (1);");

            migrationBuilder.Sql(
                """
                INSERT INTO kerio_install_requests (id, server_id, common_name, requested_at, requested_by)
                SELECT gen_random_uuid(), s.id, n.name, s.deploy_requested_at, s.deploy_requested_by
                FROM kerio_servers s
                CROSS JOIN LATERAL (
                    SELECT s.deploy_requested_name AS name WHERE s.deploy_requested_name IS NOT NULL
                    UNION
                    SELECT s.common_name WHERE s.deploy_requested_name IS NULL
                    UNION
                    SELECT e ->> 'commonName' FROM jsonb_array_elements(s.extras) e WHERE s.deploy_requested_name IS NULL
                ) n
                WHERE s.deploy_requested_at IS NOT NULL AND n.name IS NOT NULL
                ON CONFLICT DO NOTHING;
                """);

            migrationBuilder.DropColumn(
                name: "deploy_requested_at",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "deploy_requested_by",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "deploy_requested_name",
                table: "kerio_servers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deploy_requested_at",
                table: "kerio_servers",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "deploy_requested_by",
                table: "kerio_servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deploy_requested_name",
                table: "kerio_servers",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE kerio_servers s
                SET deploy_requested_at = r.requested_at, deploy_requested_by = r.requested_by, deploy_requested_name = r.common_name
                FROM (
                    SELECT DISTINCT ON (server_id) server_id, requested_at, requested_by, common_name
                    FROM kerio_install_requests
                    ORDER BY server_id, requested_at
                ) r
                WHERE s.id = r.server_id;
                """);

            migrationBuilder.DropTable(
                name: "kerio_install_requests");

            migrationBuilder.DropTable(
                name: "kerio_install_slot");

            migrationBuilder.DropColumn(
                name: "install_queue",
                table: "kerio_servers");
        }
    }
}
