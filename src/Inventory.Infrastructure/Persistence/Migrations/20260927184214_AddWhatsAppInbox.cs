using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWhatsAppInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inbound_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Channel = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Sender = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ExternalId = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Text = table.Column<string>(type: "varchar(4096)", maxLength: 4096, nullable: true),
                    ButtonId = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LockedUntil = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ReplyJson = table.Column<string>(type: "text", nullable: true),
                    LastError = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbound_messages", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_inbound_messages_Channel_ExternalId",
                table: "inbound_messages",
                columns: new[] { "Channel", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inbound_messages_Sender_Status",
                table: "inbound_messages",
                columns: new[] { "Sender", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_inbound_messages_Status_NextAttemptAt",
                table: "inbound_messages",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inbound_messages");
        }
    }
}
