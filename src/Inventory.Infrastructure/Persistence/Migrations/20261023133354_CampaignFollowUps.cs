using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CampaignFollowUps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsService",
                table: "campaign_recipients",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            // Customers added before consent was tracked carry an empty status, which neither the campaigns nor the opt-in question recognised.
            migrationBuilder.Sql("UPDATE customers SET MarketingStatus = 'Unknown' WHERE MarketingStatus IS NULL OR MarketingStatus = '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsService",
                table: "campaign_recipients");
        }
    }
}
