// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется столбец уровня нарастающей блокировки входа со значением 0.
// ОБРАТИМОСТЬ: откатывается `Down` — столбец снимается, теряется только уровень блокировок подряд (решение 0119).
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Identity.Schema.Migrations
{
    /// <inheritdoc />
    public partial class LockoutLevel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "lockout_level",
                table: "users",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "lockout_level",
                table: "users");
        }
    }
}
