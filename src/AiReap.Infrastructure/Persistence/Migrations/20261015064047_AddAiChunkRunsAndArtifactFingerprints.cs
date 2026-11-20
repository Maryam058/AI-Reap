using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiChunkRunsAndArtifactFingerprints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiChunkRuns",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operation = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    PromptTemplateVersion = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    SystemPrompt = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SourceHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ContextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PlanHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    FixedContext = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PlanJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PartCount = table.Column<int>(type: "int", nullable: false),
                    LeaseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    LeaseExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ResultJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    StartedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiChunkRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiChunkRuns_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AiChunkRuns_RequirementSources_RequirementSourceId",
                        column: x => x.RequirementSourceId,
                        principalTable: "RequirementSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ArtifactFingerprints",
                columns: table => new
                {
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequirementSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactType = table.Column<int>(type: "int", nullable: false),
                    Fingerprint = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactFingerprints", x => x.ArtifactId);
                    table.ForeignKey(
                        name: "FK_ArtifactFingerprints_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiChunkRunParts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiChunkRunId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PartNumber = table.Column<int>(type: "int", nullable: false),
                    ChunkHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SectionPath = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    SourceIdsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PromptText = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    RawResponse = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ParsedJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    LastError = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiChunkRunParts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiChunkRunParts_AiChunkRuns_AiChunkRunId",
                        column: x => x.AiChunkRunId,
                        principalTable: "AiChunkRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiChunkRunParts_AiChunkRunId_PartNumber",
                table: "AiChunkRunParts",
                columns: new[] { "AiChunkRunId", "PartNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AiChunkRuns_ProjectId",
                table: "AiChunkRuns",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_AiChunkRuns_RequirementSourceId_Operation",
                table: "AiChunkRuns",
                columns: new[] { "RequirementSourceId", "Operation" },
                unique: true,
                filter: "[Status] IN (0, 1)");

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactFingerprints_RequirementSourceId_ArtifactType_Fingerprint",
                table: "ArtifactFingerprints",
                columns: new[] { "RequirementSourceId", "ArtifactType", "Fingerprint" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiChunkRunParts");

            migrationBuilder.DropTable(
                name: "ArtifactFingerprints");

            migrationBuilder.DropTable(
                name: "AiChunkRuns");
        }
    }
}
