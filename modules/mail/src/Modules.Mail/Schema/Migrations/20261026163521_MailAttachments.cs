// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: добавляется обнуляемый столбец запечатанных вложений письма `mail_outbox.attachments` (решение 0120).
// ОБРАТИМОСТЬ: откатывается `Down` — столбец снимается, теряются вложения писем, ещё стоящих в очереди, и прежний код
// отправил бы такие письма без файлов: перед откатом дождаться, пока письма с вложениями уйдут, или отменить их.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Modules.Mail.Schema.Migrations
{
    /// <inheritdoc />
    public partial class MailAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "attachments",
                table: "mail_outbox",
                type: "bytea",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "attachments",
                table: "mail_outbox");
        }
    }
}
