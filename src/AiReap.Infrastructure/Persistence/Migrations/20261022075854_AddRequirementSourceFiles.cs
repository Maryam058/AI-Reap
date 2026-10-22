using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// RequirementSourceFiles: the original PDF/DOCX kept for an uploaded requirement source (at most one per
    /// source; the bytes are in private file storage, not here). Additive only: no existing table or row is
    /// changed, and a source without a row (pasted, TXT, or uploaded earlier) keeps working from its text.
    /// See docs/SOURCE_DOCUMENT_RETENTION.md.
    /// </summary>
    public partial class AddRequirementSourceFiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequirementSourceFiles",
                columns: table => new
                {
                    RequirementSourceId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    StorageKey = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Format = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Length = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    PageLineStartsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RequirementSourceFiles", x => x.RequirementSourceId);
                    table.ForeignKey(
                        name: "FK_RequirementSourceFiles_RequirementSources_RequirementSourceId",
                        column: x => x.RequirementSourceId,
                        principalTable: "RequirementSources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RequirementSourceFiles_StorageKey",
                table: "RequirementSourceFiles",
                column: "StorageKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequirementSourceFiles");
        }
    }
}
