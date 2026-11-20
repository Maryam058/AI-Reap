using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDecisionEvidenceSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DecisionEvidenceSnapshotJson",
                table: "ProjectStageTransitions",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DecisionEvidenceSnapshotJson",
                table: "ProjectStageTransitions");
        }
    }
}
