using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptProcessing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "processing_job_id",
                table: "transcripts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider",
                table: "transcripts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_version",
                table: "transcripts",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "cancellation_requested_at",
                table: "processing_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payload_json",
                table: "processing_jobs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "requested_by_user_id",
                table: "processing_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_processing_job_id_language",
                table: "transcripts",
                columns: new[] { "processing_job_id", "language" },
                unique: true,
                filter: "processing_job_id IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transcripts_generated_provenance",
                table: "transcripts",
                sql: "origin <> 'Generated' OR processing_job_id IS NOT NULL AND provider IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_jobs_requested_by_user_id",
                table: "processing_jobs",
                column: "requested_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "f_k_processing_jobs_asp_net_users_requested_by_user_id",
                table: "processing_jobs",
                column: "requested_by_user_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "f_k_transcripts_processing_jobs_processing_job_id",
                table: "transcripts",
                column: "processing_job_id",
                principalTable: "processing_jobs",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_processing_jobs_asp_net_users_requested_by_user_id",
                table: "processing_jobs");

            migrationBuilder.DropForeignKey(
                name: "f_k_transcripts_processing_jobs_processing_job_id",
                table: "transcripts");

            migrationBuilder.DropIndex(
                name: "i_x_transcripts_processing_job_id_language",
                table: "transcripts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transcripts_generated_provenance",
                table: "transcripts");

            migrationBuilder.DropIndex(
                name: "i_x_processing_jobs_requested_by_user_id",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "processing_job_id",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "provider",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "provider_version",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "cancellation_requested_at",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "payload_json",
                table: "processing_jobs");

            migrationBuilder.DropColumn(
                name: "requested_by_user_id",
                table: "processing_jobs");
        }
    }
}
