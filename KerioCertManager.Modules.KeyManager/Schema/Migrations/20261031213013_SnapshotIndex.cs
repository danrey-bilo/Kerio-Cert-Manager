// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: индекс снимка Key Manager Plus по last_seen_at.
// ОБРАТИМОСТЬ: `Down` удаляет индексы.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.KeyManager.Schema.Migrations
{
    /// <inheritdoc />
    public partial class SnapshotIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_keymanager_certificates_last_seen",
                table: "keymanager_certificates",
                column: "last_seen_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_keymanager_certificates_last_seen",
                table: "keymanager_certificates");
        }
    }
}
