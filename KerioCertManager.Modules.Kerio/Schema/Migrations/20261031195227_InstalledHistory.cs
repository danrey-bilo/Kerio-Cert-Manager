// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новый столбец previous_serials в kerio_servers с пустым массивом по умолчанию.
// ОБРАТИМОСТЬ: `Down` удаляет столбец — история поставленных сервисом выпусков сертификата по умолчанию.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class InstalledHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string[]>(
                name: "previous_serials",
                table: "kerio_servers",
                type: "text[]",
                nullable: false,
                defaultValueSql: "'{}'::text[]");

            migrationBuilder.AddCheckConstraint(
                name: "ck_kerio_servers_previous",
                table: "kerio_servers",
                sql: "cardinality(previous_serials) <= 10");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_kerio_servers_previous",
                table: "kerio_servers");

            migrationBuilder.DropColumn(
                name: "previous_serials",
                table: "kerio_servers");
        }
    }
}
