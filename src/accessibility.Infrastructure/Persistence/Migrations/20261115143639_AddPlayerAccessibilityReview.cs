using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlayerAccessibilityReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "accessibility_mode",
                table: "media_assets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_human_reviewed",
                table: "media_assets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                "UPDATE media_assets SET is_active = FALSE, status = 'Stale' " +
                "WHERE role IN ('SignLanguage', 'AudioDescription');");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets",
                columns: new[] { "lesson_id", "role" },
                unique: true,
                filter: "is_active = TRUE AND role IN ('SignLanguage', 'AudioDescription')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_accessibility_review",
                table: "media_assets",
                sql: "role NOT IN ('SignLanguage', 'AudioDescription') OR is_active = FALSE OR is_human_reviewed = TRUE AND ((role = 'SignLanguage' AND accessibility_mode = 'human-recorded') OR (role = 'AudioDescription' AND accessibility_mode = 'full-mix'))");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_accessibility_review",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "accessibility_mode",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "is_human_reviewed",
                table: "media_assets");
        }
    }
}
