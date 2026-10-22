using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeleteSalesSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links");

            migrationBuilder.AddCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links",
                sql: "Status IN ('Pending','Paid','Cancelled')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links");

            migrationBuilder.AddCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links",
                sql: "Status IN ('Pending','Paid')");
        }
    }
}
