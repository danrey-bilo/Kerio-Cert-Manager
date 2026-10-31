// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новые таблицы kerio_servers и kerio_deployments.
// ОБРАТИМОСТЬ: `Down` удаляет обе таблицы вместе с журналом установок.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "kerio_deployments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_id = table.Column<Guid>(type: "uuid", nullable: false),
                    server_name = table.Column<string>(type: "text", nullable: false),
                    trigger = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    finished_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    common_name = table.Column<string>(type: "text", nullable: false),
                    serial_number = table.Column<string>(type: "text", nullable: false),
                    sha256 = table.Column<string>(type: "text", nullable: true),
                    not_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    previous_sha256 = table.Column<string>(type: "text", nullable: true),
                    kerio_certificate_id = table.Column<string>(type: "text", nullable: true),
                    previous_kerio_certificate_id = table.Column<string>(type: "text", nullable: true),
                    failed_step = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    steps = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kerio_deployments", x => x.id);
                    table.CheckConstraint("ck_kerio_deployments_outcome", "outcome IN ('running', 'succeeded', 'failed', 'rolled_back', 'dry_run')");
                });

            migrationBuilder.CreateTable(
                name: "kerio_servers",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: false),
                    username = table.Column<string>(type: "text", nullable: false),
                    password = table.Column<byte[]>(type: "bytea", nullable: true),
                    data_key = table.Column<byte[]>(type: "bytea", nullable: true),
                    tls_mode = table.Column<string>(type: "text", nullable: false),
                    tls_server_name = table.Column<string>(type: "text", nullable: true),
                    tls_pin_sha256 = table.Column<string>(type: "text", nullable: true),
                    common_name = table.Column<string>(type: "text", nullable: true),
                    auto_deploy = table.Column<bool>(type: "boolean", nullable: false),
                    verify_ports = table.Column<int[]>(type: "integer[]", nullable: false),
                    notify_emails = table.Column<string[]>(type: "text[]", nullable: false),
                    cleanup_old = table.Column<bool>(type: "boolean", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false, defaultValueSql: "now()"),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true),
                    last_check_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_check_error = table.Column<string>(type: "text", nullable: true),
                    check_failures = table.Column<int>(type: "integer", nullable: false),
                    auth_rejected = table.Column<bool>(type: "boolean", nullable: false),
                    kerio_version = table.Column<string>(type: "text", nullable: true),
                    kerio_role = table.Column<string>(type: "text", nullable: true),
                    kerio_certificate_count = table.Column<int>(type: "integer", nullable: true),
                    served_sha256 = table.Column<string>(type: "text", nullable: true),
                    served_subject = table.Column<string>(type: "text", nullable: true),
                    served_issuer = table.Column<string>(type: "text", nullable: true),
                    served_serial = table.Column<string>(type: "text", nullable: true),
                    served_not_before = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    served_not_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    installed_serial = table.Column<string>(type: "text", nullable: true),
                    installed_sha256 = table.Column<string>(type: "text", nullable: true),
                    installed_not_before = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    installed_not_after = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    installed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    installed_kerio_id = table.Column<string>(type: "text", nullable: true),
                    last_deploy_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    last_deploy_error = table.Column<string>(type: "text", nullable: true),
                    failed_serial = table.Column<string>(type: "text", nullable: true),
                    deploy_failures = table.Column<int>(type: "integer", nullable: false),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    dry_run_serial = table.Column<string>(type: "text", nullable: true),
                    deploy_requested_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    deploy_requested_by = table.Column<Guid>(type: "uuid", nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    lease_owner = table.Column<Guid>(type: "uuid", nullable: true),
                    alerts = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kerio_servers", x => x.id);
                    table.CheckConstraint("ck_kerio_servers_base_url", "base_url LIKE 'https://%'");
                    table.CheckConstraint("ck_kerio_servers_name", "char_length(name) BETWEEN 1 AND 100");
                    table.CheckConstraint("ck_kerio_servers_notify", "cardinality(notify_emails) <= 20");
                    table.CheckConstraint("ck_kerio_servers_pin", "tls_mode <> 'pin' OR tls_pin_sha256 ~ '^[0-9A-F]{64}$'");
                    table.CheckConstraint("ck_kerio_servers_ports", "cardinality(verify_ports) <= 10");
                    table.CheckConstraint("ck_kerio_servers_secret", "(password IS NULL) = (data_key IS NULL)");
                    table.CheckConstraint("ck_kerio_servers_tls_mode", "tls_mode IN ('ca', 'pin')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_kerio_deployments_server_started",
                table: "kerio_deployments",
                columns: new[] { "server_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kerio_deployments_started",
                table: "kerio_deployments",
                column: "started_at");

            migrationBuilder.CreateIndex(
                name: "ux_kerio_servers_name",
                table: "kerio_servers",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "kerio_deployments");

            migrationBuilder.DropTable(
                name: "kerio_servers");
        }
    }
}
