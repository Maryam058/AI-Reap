using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AgentConcurrencyGuards : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_RequirementSourceId",
                table: "AgentRuns");

            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "AgentStageRuns",
                type: "rowversion",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_RequirementSourceId",
                table: "AgentRuns",
                column: "RequirementSourceId",
                unique: true,
                filter: "[Status] IN (0, 1, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AgentRuns_RequirementSourceId",
                table: "AgentRuns");

            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "AgentStageRuns");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRuns_RequirementSourceId",
                table: "AgentRuns",
                column: "RequirementSourceId");
        }
    }
}
