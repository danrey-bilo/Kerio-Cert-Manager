// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: два новых столбца в kerio_servers — когда выбран сертификат по умолчанию и счётчик сбоев TLS.
// ОБРАТИМОСТЬ: `Down` удаляет столбцы.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class ServerCounters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "default_selected_at",
                table: "kerio_servers",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "tls_failures",
                table: "kerio_servers",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "default_selected_at",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "tls_failures",
                table: "kerio_servers");
        }
    }
}
