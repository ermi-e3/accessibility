using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_processing_jobs_requested_by_user_id",
                table: "processing_jobs");

            migrationBuilder.DropIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "i_x_caption_artifacts_transcript_id_format_generator_version",
                table: "caption_artifacts");

            migrationBuilder.AddColumn<string>(
                name: "client_request_key",
                table: "processing_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "execution_lease_id",
                table: "processing_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "request_fingerprint",
                table: "processing_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "processing_job_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "source_media_asset_id",
                table: "media_assets",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_media_version",
                table: "media_assets",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "processing_job_attempts",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    processing_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    error_message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_processing_job_attempts", x => x.id);
                    table.CheckConstraint("ck_processing_job_attempts_number", "attempt_number > 0");
                    table.ForeignKey(
                        name: "f_k_processing_job_attempts_processing_jobs_processing_job_id",
                        column: x => x.processing_job_id,
                        principalTable: "processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_requested_by_user_id_client_request_key",
                table: "processing_jobs",
                columns: new[] { "requested_by_user_id", "client_request_key" },
                unique: true,
                filter: "client_request_key IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_status_lease_expires_at",
                table: "processing_jobs",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (PARTITION BY lesson_id, role ORDER BY updated_at DESC, id DESC) AS row_number
                    FROM media_assets
                    WHERE is_active = TRUE AND role IN ('OfflineAudio', 'Poster')
                )
                UPDATE media_assets
                SET is_active = FALSE, status = 'Stale'
                WHERE id IN (SELECT id FROM ranked WHERE row_number > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets",
                columns: new[] { "lesson_id", "role" },
                unique: true,
                filter: "is_active = TRUE AND role IN ('SignLanguage', 'AudioDescription', 'OfflineAudio', 'Poster')");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_processing_job_id",
                table: "media_assets",
                column: "processing_job_id");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_source_media_asset_id",
                table: "media_assets",
                column: "source_media_asset_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_media_assets_source_provenance",
                table: "media_assets",
                sql: "(source_media_asset_id IS NULL AND source_media_version IS NULL) OR (source_media_asset_id IS NOT NULL AND source_media_version > 0)");

            migrationBuilder.CreateIndex(
                name: "i_x_caption_artifacts_transcript_id_format_generator_version_ch~",
                table: "caption_artifacts",
                columns: new[] { "transcript_id", "format", "generator_version", "checksum" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_processing_job_attempts_processing_job_id_attempt_number",
                table: "processing_job_attempts",
                columns: new[] { "processing_job_id", "attempt_number" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "f_k_media_assets_media_assets_source_media_asset_id",
                table: "media_assets",
                column: "source_media_asset_id",
                principalTable: "media_assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "f_k_media_assets_processing_jobs_processing_job_id",
                table: "media_assets",
                column: "processing_job_id",
                principalTable: "processing_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_media_assets_media_assets_source_media_asset_id",
                table: "media_assets");

            migrationBuilder.DropForeignKey(
                name: "f_k_media_assets_processing_jobs_processing_job_id",
                table: "media_assets");

            migrationBuilder.DropTable(
                name: "processing_job_attempts");

            migrationBuilder.DropIndex(
                name: "i_x_processing_jobs_requested_by_user_id_client_request_key",
                table: "processing_jobs");

            migrationBuilder.DropIndex(
                name: "i_x_processing_jobs_status_lease_expires_at",
                table: "processing_jobs");

            migrationBuilder.DropIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "i_x_media_assets_processing_job_id",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "i_x_media_assets_source_media_asset_id",
                table: "media_assets");

            migrationBuilder.DropCheckConstraint(
                name: "ck_media_assets_source_provenance",
                table: "media_assets");

            migrationBuilder.DropIndex(
                name: "i_x_caption_artifacts_transcript_id_format_generator_version_ch~",
                table: "caption_artifacts");

            migrationBuilder.DropColumn(
                name: "client_request_key",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "execution_lease_id",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "request_fingerprint",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "processing_job_id",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "source_media_asset_id",
                table: "media_assets");

            migrationBuilder.DropColumn(
                name: "source_media_version",
                table: "media_assets");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_requested_by_user_id",
                table: "processing_jobs",
                column: "requested_by_user_id");

            migrationBuilder.CreateIndex(
                name: "i_x_media_assets_lesson_id_role",
                table: "media_assets",
                columns: new[] { "lesson_id", "role" },
                unique: true,
                filter: "is_active = TRUE AND role IN ('SignLanguage', 'AudioDescription')");

            migrationBuilder.Sql("""
                WITH ranked AS (
                    SELECT id,
                           ROW_NUMBER() OVER (
                               PARTITION BY transcript_id, format, generator_version
                               ORDER BY CASE status WHEN 'Ready' THEN 0 WHEN 'Pending' THEN 1 ELSE 2 END,
                                        updated_at DESC,
                                        id DESC) AS row_number
                    FROM caption_artifacts
                )
                DELETE FROM caption_artifacts
                WHERE id IN (SELECT id FROM ranked WHERE row_number > 1);
                """);

            migrationBuilder.CreateIndex(
                name: "i_x_caption_artifacts_transcript_id_format_generator_version",
                table: "caption_artifacts",
                columns: new[] { "transcript_id", "format", "generator_version" },
                unique: true);
        }
    }
}
