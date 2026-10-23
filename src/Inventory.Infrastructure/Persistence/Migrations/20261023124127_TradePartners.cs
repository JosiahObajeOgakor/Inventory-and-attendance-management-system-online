using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TradePartners : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "trade_partners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    BusinessName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    ContactName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Phone = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Email = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Address = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    CacNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    TaxId = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    Description = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: false),
                    RequestedTier = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ApprovedTier = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    Status = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    Channel = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    Verification = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    VerifiedName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    VerificationNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    UserId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecidedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    DecisionNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_partners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trade_partners_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "trade_partner_documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    TradePartnerId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    FileName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Data = table.Column<byte[]>(type: "mediumblob", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_partner_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trade_partner_documents_trade_partners_TradePartnerId",
                        column: x => x.TradePartnerId,
                        principalTable: "trade_partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "wallet_entries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    TradePartnerId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Reference = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Method = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    PaymentLinkId = table.Column<int>(type: "int", nullable: true),
                    InvoiceId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ConfirmedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ConfirmedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wallet_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wallet_entries_trade_partners_TradePartnerId",
                        column: x => x.TradePartnerId,
                        principalTable: "trade_partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_trade_partner_documents_TradePartnerId",
                table: "trade_partner_documents",
                column: "TradePartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_trade_partners_CustomerId",
                table: "trade_partners",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_trade_partners_Phone",
                table: "trade_partners",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trade_partners_Status_Id",
                table: "trade_partners",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_trade_partners_UserId",
                table: "trade_partners",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_Reference",
                table: "wallet_entries",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wallet_entries_TradePartnerId_Status",
                table: "wallet_entries",
                columns: new[] { "TradePartnerId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "trade_partner_documents");

            migrationBuilder.DropTable(
                name: "wallet_entries");

            migrationBuilder.DropTable(
                name: "trade_partners");
        }
    }
}
