using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ArtifactApprovalVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Priority",
                table: "ArtifactVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "ArtifactVersions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                table: "ArtifactVersions",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ApprovedVersion",
                table: "Artifacts",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VersionNumber",
                table: "ArtifactReviews",
                type: "int",
                nullable: true);

            // Backfill existing data (no rows are removed or rewritten beyond these new columns):
            // artifacts that are Approved(3)/Implemented(5)/Verified(6) today were approved as of
            // their current version, and each artifact's latest version row gets the title,
            // priority and status it currently has.
            migrationBuilder.Sql(
                "UPDATE [Artifacts] SET [ApprovedVersion] = [CurrentVersion] WHERE [Status] IN (3, 5, 6);");
            migrationBuilder.Sql(
                "UPDATE v SET v.[Title] = a.[Title], v.[Priority] = a.[Priority], v.[Status] = a.[Status] " +
                "FROM [ArtifactVersions] v INNER JOIN [Artifacts] a ON a.[Id] = v.[ArtifactId] AND v.[VersionNumber] = a.[CurrentVersion];");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Priority",
                table: "ArtifactVersions");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "ArtifactVersions");

            migrationBuilder.DropColumn(
                name: "Title",
                table: "ArtifactVersions");

            migrationBuilder.DropColumn(
                name: "ApprovedVersion",
                table: "Artifacts");

            migrationBuilder.DropColumn(
                name: "VersionNumber",
                table: "ArtifactReviews");
        }
    }
}
