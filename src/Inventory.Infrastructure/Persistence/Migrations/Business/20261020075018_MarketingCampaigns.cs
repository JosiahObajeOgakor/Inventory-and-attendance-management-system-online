using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations.Business
{
    /// <inheritdoc />
    public partial class MarketingCampaigns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MarketingSource",
                table: "customers",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketingStatus",
                table: "customers",
                type: "varchar(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketingUpdatedAt",
                table: "customers",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "customers",
                type: "varchar(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "campaign_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    CampaignId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Recipient = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false),
                    Channel = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Language = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false),
                    Text = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Detail = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Mode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_messages", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SendWhatsApp = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SendEmail = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RunMinute = table.Column<int>(type: "int", nullable: false),
                    EveryNDays = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: true),
                    EndDate = table.Column<DateTime>(type: "date", nullable: true),
                    CooldownDays = table.Column<int>(type: "int", nullable: false),
                    MaxRecipients = table.Column<int>(type: "int", nullable: false),
                    ThresholdDays = table.Column<int>(type: "int", nullable: false),
                    FallbackRepurchaseDays = table.Column<int>(type: "int", nullable: false),
                    LastRunOn = table.Column<DateTime>(type: "date", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastRunSent = table.Column<int>(type: "int", nullable: false),
                    LastRunNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    UpdatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_campaign_messages_CampaignId_CreatedAt",
                table: "campaign_messages",
                columns: new[] { "CampaignId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_messages_CustomerId_CreatedAt",
                table: "campaign_messages",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_Type",
                table: "campaigns",
                column: "Type",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "campaign_messages");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropColumn(
                name: "MarketingSource",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "MarketingStatus",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "MarketingUpdatedAt",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "customers");
        }
    }
}
