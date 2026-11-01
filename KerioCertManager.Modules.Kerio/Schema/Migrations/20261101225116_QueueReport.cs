// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: новый пустой столбец queue_report_since в kerio_servers.
// ОБРАТИМОСТЬ: `Down` удаляет столбец — сводка идущей очереди не уйдёт, следующие удачи пишутся по одной.
using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class QueueReport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "queue_report_since",
                table: "kerio_servers",
                type: "timestamptz",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "queue_report_since",
                table: "kerio_servers");
        }
    }
}
