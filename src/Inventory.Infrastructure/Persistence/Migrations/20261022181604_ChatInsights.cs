using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ChatInsights : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "chat_conversations",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MessageCount",
                table: "chat_conversations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Network",
                table: "chat_conversations",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Region",
                table: "chat_conversations",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_LastMessageAt",
                table: "chat_conversations",
                column: "LastMessageAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_conversations_LastMessageAt",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "MessageCount",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "Network",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "Region",
                table: "chat_conversations");
        }
    }
}
