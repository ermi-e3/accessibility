using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineSnapshotMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "title",
                table: "offline_packages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "display_name",
                table: "offline_package_assets",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "title",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "display_name",
                table: "offline_package_assets");
        }
    }
}
