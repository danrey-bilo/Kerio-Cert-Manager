// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новые столбцы extras и served_by_name в kerio_servers.
// ОБРАТИМОСТЬ: `Down` удаляет оба столбца — список сертификатов своих имён и снимок поданного по SNI.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class ExtraCertificates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "extras",
                table: "kerio_servers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'[]'::jsonb");

            migrationBuilder.AddColumn<string>(
                name: "served_by_name",
                table: "kerio_servers",
                type: "jsonb",
                nullable: false,
                defaultValueSql: "'{}'::jsonb");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "extras",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "served_by_name",
                table: "kerio_servers");
        }
    }
}
