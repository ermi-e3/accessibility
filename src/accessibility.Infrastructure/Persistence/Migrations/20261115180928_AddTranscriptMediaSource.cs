using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTranscriptMediaSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "source_media_asset_id",
                table: "transcripts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "source_media_version",
                table: "transcripts",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_transcripts_source_media_asset_id",
                table: "transcripts",
                column: "source_media_asset_id");

            migrationBuilder.AddCheckConstraint(
                name: "ck_transcripts_source_media",
                table: "transcripts",
                sql: "(source_media_asset_id IS NULL AND source_media_version IS NULL) OR (source_media_asset_id IS NOT NULL AND source_media_version > 0)");

            migrationBuilder.AddForeignKey(
                name: "f_k_transcripts_media_assets_source_media_asset_id",
                table: "transcripts",
                column: "source_media_asset_id",
                principalTable: "media_assets",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "f_k_transcripts_media_assets_source_media_asset_id",
                table: "transcripts");

            migrationBuilder.DropIndex(
                name: "i_x_transcripts_source_media_asset_id",
                table: "transcripts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_transcripts_source_media",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "source_media_asset_id",
                table: "transcripts");

            migrationBuilder.DropColumn(
                name: "source_media_version",
                table: "transcripts");
        }
    }
}
