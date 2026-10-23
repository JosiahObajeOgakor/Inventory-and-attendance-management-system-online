using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MarketingApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "PendingAt",
                table: "campaigns",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingBy",
                table: "campaigns",
                type: "longtext",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingChange",
                table: "campaigns",
                type: "longtext",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingAt",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "PendingBy",
                table: "campaigns");

            migrationBuilder.DropColumn(
                name: "PendingChange",
                table: "campaigns");
        }
    }
}
