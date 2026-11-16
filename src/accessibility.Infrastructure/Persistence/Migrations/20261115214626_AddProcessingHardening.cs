using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingHardening : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "revision_of_transcript_id",
                table: "transcripts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "version",
                table: "processing_job_attempts",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "processing_idempotency_requests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    client_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    request_fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    processing_job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("p_k_processing_idempotency_requests", x => x.id);
                    table.ForeignKey(
                        name: "f_k_processing_idempotency_requests_asp_net_users_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "asp_net_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "f_k_processing_idempotency_requests_processing_jobs_processing_j~",
                        column: x => x.processing_job_id,
                        principalTable: "processing_jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_revision_of_transcript_id",
                table: "transcripts",
                column: "revision_of_transcript_id");

            migrationBuilder.CreateIndex(
                name: "i_x_processing_idempotency_requests_owner_user_id_client_key",
                table: "processing_idempotency_requests",
                columns: new[] { "owner_user_id", "client_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "i_x_processing_idempotency_requests_processing_job_id",
                table: "processing_idempotency_requests",
                column: "processing_job_id");

            migrationBuilder.AddForeignKey(
                name: "f_k_transcripts_transcripts_revision_of_transcript_id",
                table: "transcripts",
                column: "revision_of_transcript_id",
                principalTable: "transcripts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_transcripts_transcripts_revision_of_transcript_id",
                table: "transcripts");

            migrationBuilder.DropTable(
                name: "processing_idempotency_requests");

            migrationBuilder.DropIndex(
                name: "i_x_transcripts_revision_of_transcript_id",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "revision_of_transcript_id",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "version",
                table: "processing_job_attempts");
        }
    }
}
