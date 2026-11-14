using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLessonIngestConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_dimensions",
                table: "media_assets",
                sql: "(width IS NULL OR width > 0) AND (height IS NULL OR height > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_sign_language",
                table: "media_assets",
                sql: "role <> 'SignLanguage' OR language IS NOT NULL AND language = 'eth'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_sync_offset",
                table: "media_assets",
                sql: "sync_offset_milliseconds >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_dimensions",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_sign_language",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_sync_offset",
                table: "media_assets");
        }
    }
}
