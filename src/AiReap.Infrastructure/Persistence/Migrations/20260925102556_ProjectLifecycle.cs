using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ProjectLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReviewerRole",
                table: "ArtifactReviews",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProjectStatusAtStart",
                table: "AgentRuns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ArtifactWorkRecords",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ArtifactId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VersionNumber = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<int>(type: "int", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    RecordedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RecordedByRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RecordedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ArtifactWorkRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ArtifactWorkRecords_Artifacts_ArtifactId",
                        column: x => x.ArtifactId,
                        principalTable: "Artifacts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProjectStageTransitions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    State = table.Column<int>(type: "int", nullable: false),
                    FromStatus = table.Column<int>(type: "int", nullable: true),
                    ToStatus = table.Column<int>(type: "int", nullable: false),
                    RequestedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    RequestedByRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RequestedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RequestNote = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DecidedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    DecidedByRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DecisionReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    IsOverride = table.Column<bool>(type: "bit", nullable: false),
                    OverrideReason = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    EvidenceSnapshotJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectStageTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectStageTransitions_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectNotifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipientUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Message = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AcknowledgedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectNotifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectNotifications_ProjectStageTransitions_TransitionId",
                        column: x => x.TransitionId,
                        principalTable: "ProjectStageTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProjectNotifications_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProjectStageReviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TransitionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ReviewerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReviewerRole = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Decision = table.Column<int>(type: "int", nullable: false),
                    Comment = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ReviewedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectStageReviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectStageReviews_ProjectStageTransitions_TransitionId",
                        column: x => x.TransitionId,
                        principalTable: "ProjectStageTransitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ArtifactWorkRecords_ArtifactId_RecordedAt",
                table: "ArtifactWorkRecords",
                columns: new[] { "ArtifactId", "RecordedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotifications_ProjectId",
                table: "ProjectNotifications",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotifications_RecipientUserId_AcknowledgedAt",
                table: "ProjectNotifications",
                columns: new[] { "RecipientUserId", "AcknowledgedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectNotifications_TransitionId_RecipientUserId_Kind",
                table: "ProjectNotifications",
                columns: new[] { "TransitionId", "RecipientUserId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStageReviews_TransitionId",
                table: "ProjectStageReviews",
                column: "TransitionId");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStageTransitions_ProjectId_Pending",
                table: "ProjectStageTransitions",
                column: "ProjectId",
                unique: true,
                filter: "[State] = 0");

            migrationBuilder.CreateIndex(
                name: "IX_ProjectStageTransitions_ProjectId_Sequence",
                table: "ProjectStageTransitions",
                columns: new[] { "ProjectId", "Sequence" },
                unique: true);

            // Give every existing project a lifecycle history: one Baseline (Kind 0), Completed
            // (State 1) row at its current status. Project.Status itself is left untouched - no
            // project is moved backward; evidence gaps are reported, not corrected.
            migrationBuilder.Sql(@"
INSERT INTO ProjectStageTransitions
    (Id, ProjectId, Sequence, Kind, State, FromStatus, ToStatus, RequestedByUserId, RequestedByRole, RequestedAt,
     RequestNote, DecidedByUserId, DecidedByRole, DecidedAt, IsOverride, EvidenceSnapshotJson)
SELECT NEWID(), p.Id, 0, 0, 1, NULL, p.Status, 'system', NULL, SYSUTCDATETIME(),
     N'Lifecycle tracking introduced; the project was already at this stage.', NULL, NULL, SYSUTCDATETIME(), 0, N'[]'
FROM Projects p;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ArtifactWorkRecords");

            migrationBuilder.DropTable(
                name: "ProjectNotifications");

            migrationBuilder.DropTable(
                name: "ProjectStageReviews");

            migrationBuilder.DropTable(
                name: "ProjectStageTransitions");

            migrationBuilder.DropColumn(
                name: "ReviewerRole",
                table: "ArtifactReviews");

            migrationBuilder.DropColumn(
                name: "ProjectStatusAtStart",
                table: "AgentRuns");
        }
    }
}
