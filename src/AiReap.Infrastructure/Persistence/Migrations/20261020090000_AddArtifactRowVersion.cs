using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// S1 - Artifacts.RowVersion, a SQL Server rowversion maintained by the database, as a second
    /// concurrency token beside the application's CurrentVersion. Adding a rowversion column fills it
    /// for every existing row, so it is NOT NULL with no default value: SQL Server does not accept a
    /// default on a rowversion column, and the value is never supplied by the application.
    /// </summary>
    public partial class AddArtifactRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                table: "Artifacts",
                type: "rowversion",
                rowVersion: true,
                nullable: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                table: "Artifacts");
        }
    }
}
