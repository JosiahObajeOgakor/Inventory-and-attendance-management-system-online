using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TradeRegistrationSteps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_codes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Email = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Purpose = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    CodeHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_codes", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "trade_application_drafts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Email = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    EmailVerifiedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DeviceTokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    Step = table.Column<int>(type: "int", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    LastActivityAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    RemindersSent = table.Column<int>(type: "int", nullable: false),
                    LastReminderAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    WarnedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_application_drafts", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "trade_application_files",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    DraftId = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    FileName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Data = table.Column<byte[]>(type: "mediumblob", nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trade_application_files", x => x.Id);
                    table.ForeignKey(
                        name: "FK_trade_application_files_trade_application_drafts_DraftId",
                        column: x => x.DraftId,
                        principalTable: "trade_application_drafts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_email_codes_Email_Purpose_CreatedAt",
                table: "email_codes",
                columns: new[] { "Email", "Purpose", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_trade_application_drafts_DeviceTokenHash",
                table: "trade_application_drafts",
                column: "DeviceTokenHash");

            migrationBuilder.CreateIndex(
                name: "IX_trade_application_drafts_Email",
                table: "trade_application_drafts",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trade_application_drafts_LastActivityAt",
                table: "trade_application_drafts",
                column: "LastActivityAt");

            migrationBuilder.CreateIndex(
                name: "IX_trade_application_files_DraftId",
                table: "trade_application_files",
                column: "DraftId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_codes");

            migrationBuilder.DropTable(
                name: "trade_application_files");

            migrationBuilder.DropTable(
                name: "trade_application_drafts");
        }
    }
}
