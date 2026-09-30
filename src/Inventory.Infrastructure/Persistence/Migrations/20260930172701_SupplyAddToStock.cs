using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplyAddToStock : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ProductId",
                table: "supply_items",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AddedToStock",
                table: "supplies",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "WarehouseId",
                table: "supplies",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ProductId",
                table: "supplier_products",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplier_products_ProductId",
                table: "supplier_products",
                column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_supplier_products_products_ProductId",
                table: "supplier_products",
                column: "ProductId",
                principalTable: "products",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supplier_products_products_ProductId",
                table: "supplier_products");

            migrationBuilder.DropIndex(
                name: "IX_supplier_products_ProductId",
                table: "supplier_products");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "AddedToStock",
                table: "supplies");

            migrationBuilder.DropColumn(
                name: "WarehouseId",
                table: "supplies");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "supplier_products");
        }
    }
}
