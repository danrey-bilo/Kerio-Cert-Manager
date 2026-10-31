// РАЗРУШАЮЩИХ ОПЕРАЦИЙ НЕТ: индексы журнала установок по исходу и по незавершённым установкам.
// ОБРАТИМОСТЬ: `Down` удаляет индексы.
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KerioCertManager.Modules.Kerio.Schema.Migrations
{
    /// <inheritdoc />
    public partial class DeploymentIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_kerio_deployments_outcome_started",
                table: "kerio_deployments",
                columns: new[] { "outcome", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kerio_deployments_running",
                table: "kerio_deployments",
                column: "server_id",
                filter: "outcome = 'running'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_kerio_deployments_outcome_started",
                table: "kerio_deployments");

            migrationBuilder.DropIndex(
                name: "ix_kerio_deployments_running",
                table: "kerio_deployments");
        }
    }
}
