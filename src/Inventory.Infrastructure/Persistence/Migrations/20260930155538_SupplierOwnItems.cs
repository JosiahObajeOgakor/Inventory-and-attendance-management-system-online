using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierOwnItems : Migration
    {
        /// <inheritdoc />
        // Supply lines stop pointing at stock products and point at each supplier's own items instead. Dropping supply_items.ProductId is
        // flagged as data loss but loses nothing: the supplies feature has never been released, so no supply record exists anywhere yet.
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supply_items_products_ProductId",
                table: "supply_items");

            migrationBuilder.DropIndex(
                name: "IX_supply_items_ProductId",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "ProductId",
                table: "supply_items");

            migrationBuilder.AddColumn<string>(
                name: "Name",
                table: "supply_items",
                type: "varchar(150)",
                maxLength: 150,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Size",
                table: "supply_items",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SupplierProductId",
                table: "supply_items",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "supply_items",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "supplier_products",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Size = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    Unit = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_products", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplier_products_suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_supply_items_SupplierProductId",
                table: "supply_items",
                column: "SupplierProductId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_products_SupplierId_Name",
                table: "supplier_products",
                columns: new[] { "SupplierId", "Name" });

            migrationBuilder.AddForeignKey(
                name: "FK_supply_items_supplier_products_SupplierProductId",
                table: "supply_items",
                column: "SupplierProductId",
                principalTable: "supplier_products",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_supply_items_supplier_products_SupplierProductId",
                table: "supply_items");

            migrationBuilder.DropTable(
                name: "supplier_products");

            migrationBuilder.DropIndex(
                name: "IX_supply_items_SupplierProductId",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "Name",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "SupplierProductId",
                table: "supply_items");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "supply_items");

            migrationBuilder.AddColumn<int>(
                name: "ProductId",
                table: "supply_items",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_supply_items_ProductId",
                table: "supply_items",
                column: "ProductId");

            migrationBuilder.AddForeignKey(
                name: "FK_supply_items_products_ProductId",
                table: "supply_items",
                column: "ProductId",
                principalTable: "products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
