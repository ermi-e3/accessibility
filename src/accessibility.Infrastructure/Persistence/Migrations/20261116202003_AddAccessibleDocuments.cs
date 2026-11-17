using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccessibleDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "issue_summary_json",
                table: "documents",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "normalized_at",
                table: "documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "normalizer_version",
                table: "documents",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "validated_at",
                table: "document_outputs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_profile",
                table: "document_outputs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validation_summary_json",
                table: "document_outputs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validator_name",
                table: "document_outputs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "validator_version",
                table: "document_outputs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "relationship_id",
                table: "document_images",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "reviewed_at",
                table: "document_images",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "reviewed_by_user_id",
                table: "document_images",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "source_location",
                table: "document_images",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "suggested_alt_text",
                table: "document_images",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "suggested_decorative",
                table: "document_images",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(
                """
                UPDATE document_images
                SET review_status = 'Draft',
                    alt_text = NULL,
                    is_decorative = FALSE
                WHERE review_status = 'Approved';

                UPDATE document_outputs
                SET status = 'Stale'
                WHERE status = 'Validated';
                """);

            migrationBuilder.CreateIndex(
                name: "i_x_document_images_document_id_relationship_id",
                table: "document_images",
                columns: new[] { "document_id", "relationship_id" });

            migrationBuilder.CreateIndex(
                name: "i_x_document_images_reviewed_by_user_id",
                table: "document_images",
                column: "reviewed_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_images_approved_alternative",
                table: "document_images",
                sql: "review_status <> 'Approved' OR (reviewed_by_user_id IS NOT NULL AND reviewed_at IS NOT NULL AND ((is_decorative AND alt_text IS NULL) OR (NOT is_decorative AND alt_text IS NOT NULL AND length(btrim(alt_text)) > 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_document_outputs_validation_provenance",
                table: "document_outputs",
                sql: "status NOT IN ('Validated', 'ValidationFailed', 'Failed') OR (validation_report_file_id IS NOT NULL AND validator_name IS NOT NULL AND validator_version IS NOT NULL AND validation_profile IS NOT NULL AND validation_summary_json IS NOT NULL AND validated_at IS NOT NULL)");

            migrationBuilder.AddForeignKey(
                name: "f_k_document_images_asp_net_users_reviewed_by_user_id",
                table: "document_images",
                column: "reviewed_by_user_id",
                principalTable: "asp_net_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_document_outputs_validation_provenance",
                table: "document_outputs");

            migrationBuilder.DropForeignKey(
                name: "f_k_document_images_asp_net_users_reviewed_by_user_id",
                table: "document_images");

            migrationBuilder.DropIndex(
                name: "i_x_document_images_document_id_relationship_id",
                table: "document_images");

            migrationBuilder.DropIndex(
                name: "i_x_document_images_reviewed_by_user_id",
                table: "document_images");

            migrationBuilder.DropCheckConstraint(
                name: "ck_document_images_approved_alternative",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "issue_summary_json",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "normalized_at",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "normalizer_version",
                table: "documents");

            migrationBuilder.DropColumn(
                name: "validated_at",
                table: "document_outputs");

            migrationBuilder.DropColumn(
                name: "validation_profile",
                table: "document_outputs");

            migrationBuilder.DropColumn(
                name: "validation_summary_json",
                table: "document_outputs");

            migrationBuilder.DropColumn(
                name: "validator_name",
                table: "document_outputs");

            migrationBuilder.DropColumn(
                name: "validator_version",
                table: "document_outputs");

            migrationBuilder.DropColumn(
                name: "relationship_id",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "reviewed_at",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "reviewed_by_user_id",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "source_location",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "suggested_alt_text",
                table: "document_images");

            migrationBuilder.DropColumn(
                name: "suggested_decorative",
                table: "document_images");
        }
    }
}
