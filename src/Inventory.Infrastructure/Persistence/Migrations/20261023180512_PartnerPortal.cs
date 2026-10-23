using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PartnerPortal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "partner_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    TradePartnerId = table.Column<int>(type: "int", nullable: false),
                    FromPartner = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Text = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    By = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    At = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ReadAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_partner_messages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_partner_messages_trade_partners_TradePartnerId",
                        column: x => x.TradePartnerId,
                        principalTable: "trade_partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "trusted_devices",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Email = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    FirstIp = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true),
                    LastIp = table.Column<string>(type: "varchar(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LastUsedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trusted_devices", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_partner_messages_TradePartnerId_Id",
                table: "partner_messages",
                columns: new[] { "TradePartnerId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_trusted_devices_Email",
                table: "trusted_devices",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_trusted_devices_TokenHash",
                table: "trusted_devices",
                column: "TokenHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "partner_messages");

            migrationBuilder.DropTable(
                name: "trusted_devices");
        }
    }
}
