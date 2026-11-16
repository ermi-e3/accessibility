using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Accessibility.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOfflineReliability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "error_code",
                table: "offline_packages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "execution_lease_id",
                table: "offline_packages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "lease_expires_at",
                table: "offline_packages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "i_x_offline_packages_status_lease_expires_at",
                table: "offline_packages",
                columns: new[] { "status", "lease_expires_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "i_x_offline_packages_status_lease_expires_at",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "error_code",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "execution_lease_id",
                table: "offline_packages");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "offline_packages");
        }
    }
}
