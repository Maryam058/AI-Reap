using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectMembers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProjectMembers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProjectId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    AddedByUserId = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProjectMembers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProjectMembers_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_ProjectId_UserId",
                table: "ProjectMembers",
                columns: new[] { "ProjectId", "UserId" },
                unique: true);

            // Backfill: every existing user becomes a member of every existing project, so
            // membership enforcement (introduced alongside this table) doesn't lock anyone out
            // of data they already had access to. Only NEW projects going forward get properly
            // scoped membership (the creator, plus whoever is explicitly added via
            // POST /api/projects/{id}/members). Attributed to each project's own creator, since
            // that's the closest thing to "who already effectively granted this access."
            migrationBuilder.Sql(@"
                INSERT INTO ProjectMembers (Id, ProjectId, UserId, AddedByUserId, AddedAt)
                SELECT NEWID(), p.Id, u.Id, p.CreatedByUserId, GETUTCDATE()
                FROM Projects p
                CROSS JOIN AspNetUsers u
                WHERE NOT EXISTS (
                    SELECT 1 FROM ProjectMembers m WHERE m.ProjectId = p.Id AND m.UserId = u.Id
                );
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProjectMembers");
        }
    }
}
