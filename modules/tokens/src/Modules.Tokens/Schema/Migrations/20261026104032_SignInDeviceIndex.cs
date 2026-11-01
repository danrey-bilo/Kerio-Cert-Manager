// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется индекс проверки «знакомо ли устройство» при входе.
// ОБРАТИМОСТЬ: откатывается `Down` — индекс снимается, данные не затрагиваются.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Tokens.Schema.Migrations
{
    /// <inheritdoc />
    public partial class SignInDeviceIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_auth_tokens_user_kind_name",
                table: "auth_tokens",
                columns: new[] { "user_id", "kind", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_auth_tokens_user_kind_name",
                table: "auth_tokens");
        }
    }
}
