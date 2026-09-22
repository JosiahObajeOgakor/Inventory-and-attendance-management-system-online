using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MovementBatchId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BatchId",
                table: "stock_movements",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_stock_movements_BatchId",
                table: "stock_movements",
                column: "BatchId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stock_movements_BatchId",
                table: "stock_movements");

            migrationBuilder.DropColumn(
                name: "BatchId",
                table: "stock_movements");
        }
    }
}
