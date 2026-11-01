// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новый пустой столбец deploy_requested_name в kerio_servers.
// ОБРАТИМОСТЬ: `Down` удаляет столбец — ждущий ручной запрос на один сертификат станет запросом на все.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class DeployRequestName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "deploy_requested_name",
                table: "kerio_servers",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "deploy_requested_name",
                table: "kerio_servers");
        }
    }
}
