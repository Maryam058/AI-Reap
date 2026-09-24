using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ImpactNoticesAndBusinessObjectives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ArtifactImpactNotices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceVersion = table.Column<int>(type: "int", nullable: false),
                    AffectedArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Path = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: false),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AcknowledgementNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactImpactNotices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactImpactNotices_Artifacts_AffectedArtifactId",
                        column: x => x.AffectedArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArtifactImpactNotices_Artifacts_SourceArtifactId",
                        column: x => x.SourceArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ArtifactImpactNotices_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactImpactNotices_AffectedArtifactId_AcknowledgedAt",
                table: "ArtifactImpactNotices",
                columns: new[] { "AffectedArtifactId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactImpactNotices_ProjectId_AcknowledgedAt",
                table: "ArtifactImpactNotices",
                columns: new[] { "ProjectId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactImpactNotices_SourceArtifactId",
                table: "ArtifactImpactNotices",
                column: "SourceArtifactId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtifactImpactNotices");
        }
    }
}
