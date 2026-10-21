using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiReap.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// S1 - Artifact.CurrentVersion becomes an EF concurrency token. That is model metadata only
    /// (UPDATE/DELETE statements check the version they read); the column is unchanged, so there is
    /// no schema operation either way.
    /// </summary>
    public partial class ArtifactCurrentVersionConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
