// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется обнуляемый столбец запечатанной HTML-части письма `mail_outbox.html_body` (решение 0121).
// ОБРАТИМОСТЬ: откатывается `Down` — столбец снимается, теряются HTML-части писем, ещё стоящих в очереди; прежний код
// отправит такие письма одной текстовой частью, таблицы в ней — строкой на ряд, так что ни одно письмо не теряется.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Mail.Schema.Migrations
{
    /// <inheritdoc />
    public partial class MailHtml : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "html_body",
                table: "mail_outbox",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "html_body",
                table: "mail_outbox");
        }
    }
}
