// РАЗРУШАЮЩИЕ ОПЕРАЦИИ — ТОЛЬКО В `Down`: он сносит таблицы модуля вместе с данными (`auth_tokens`, `auth_token_secrets`).
// ОБРАТИМОСТЬ: на боевой базе — только восстановлением из копии; `Down` годится лишь для пересборки
// пустого окружения. На поднятой установке базовая не выполняется, а отмечается (решение 0114).
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tokens.Schema.Migrations
{
    /// <inheritdoc />
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auth_tokens",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    address = table.Column<IPAddress>(type: "inet", nullable: true),
                    device_ref = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    scopes = table.Column<string>(type: "jsonb", nullable: true),
                    step_up_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_tokens", x => x.id);
                    table.CheckConstraint("ck_auth_tokens_kind", "kind IN ('session', 'device', 'api_key', 'download_ticket')");
                    table.CheckConstraint("ck_auth_tokens_scopes", "(kind = 'api_key' AND scopes IS NOT NULL AND jsonb_array_length(scopes) > 0)\nOR (kind = 'download_ticket' AND (scopes IS NULL OR jsonb_array_length(scopes) = 0))\nOR (kind NOT IN ('api_key', 'download_ticket') AND scopes IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "auth_token_secrets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    token_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    selector = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_auth_token_secrets", x => x.id);
                    table.CheckConstraint("ck_auth_token_secrets_kind", "kind IN ('access', 'refresh', 'key')");
                    table.ForeignKey(
                        name: "fk_auth_token_secrets_token_id_auth_tokens",
                        column: x => x.token_id,
                        principalTable: "auth_tokens",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_auth_token_secrets_expires",
                table: "auth_token_secrets",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_token_secrets_token",
                table: "auth_token_secrets",
                column: "token_id");

            migrationBuilder.CreateIndex(
                name: "ux_auth_token_secrets_hash",
                table: "auth_token_secrets",
                column: "hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_auth_token_secrets_selector",
                table: "auth_token_secrets",
                column: "selector",
                unique: true,
                filter: "selector IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_device",
                table: "auth_tokens",
                column: "device_ref",
                filter: "device_ref IS NOT NULL AND revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_expires",
                table: "auth_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_revoked",
                table: "auth_tokens",
                column: "revoked_at");

            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_user_kind",
                table: "auth_tokens",
                columns: new[] { "user_id", "kind" },
                filter: "revoked_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_auth_tokens_user_name",
                table: "auth_tokens",
                columns: new[] { "user_id", "name" },
                unique: true,
                filter: "kind = 'api_key' AND revoked_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auth_token_secrets");

            migrationBuilder.DropTable(
                name: "auth_tokens");
        }
    }
}
