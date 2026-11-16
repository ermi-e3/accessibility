using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineLearning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "attempts",
                table: "offline_packages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                table: "offline_packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "external_job_id",
                table: "offline_packages",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "started_at",
                table: "offline_packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "offline_package_assets",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    offline_package_id = table.Column<Guid>(type: "uuid", nullable: false),
                    stored_file_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    language = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: true),
                    is_required = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_offline_package_assets", x => x.id);
                    table.ForeignKey(
                        name: "f_k_offline_package_assets_offline_packages_offline_package_id",
                        column: x => x.offline_package_id,
                        principalTable: "offline_packages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "f_k_offline_package_assets_stored_files_stored_file_id",
                        column: x => x.stored_file_id,
                        principalTable: "stored_files",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                UPDATE offline_packages
                SET status = 'Queued', external_job_id = NULL, attempts = 0
                WHERE status = 'Building' AND started_at IS NULL;

                UPDATE offline_packages
                SET status = 'Stale'
                WHERE status = 'Ready';
                """);

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY transcript_id, format
                               ORDER BY updated_at DESC, id DESC) AS row_number
                    FROM caption_artifacts
                    WHERE status = 'Ready'
                )
                UPDATE caption_artifacts
                SET status = 'Stale'
                WHERE id IN (SELECT id FROM ranked WHERE row_number > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "i_x_caption_artifacts_transcript_id_format_status",
                table: "caption_artifacts",
                columns: new[] { "transcript_id", "format", "status" },
                unique: true,
                filter: "status = 'Ready'");

            migrationBuilder.CreateIndex(
                name: "i_x_offline_package_assets_offline_package_id_path",
                table: "offline_package_assets",
                columns: new[] { "offline_package_id", "path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_offline_package_assets_offline_package_id_sequence",
                table: "offline_package_assets",
                columns: new[] { "offline_package_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_offline_package_assets_stored_file_id",
                table: "offline_package_assets",
                column: "stored_file_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "offline_package_assets");

            migrationBuilder.DropIndex(
                name: "i_x_caption_artifacts_transcript_id_format_status",
                table: "caption_artifacts");

            migrationBuilder.DropColumn(
                name: "attempts",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "external_job_id",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "started_at",
                table: "offline_packages");
        }
    }
}
