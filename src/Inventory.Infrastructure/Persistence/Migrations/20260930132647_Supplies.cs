using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Supplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Reference = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    SupplyDate = table.Column<DateTime>(type: "date", nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    PaymentStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    PaymentMethod = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "varchar(400)", maxLength: 400, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplies_suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "supply_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SupplyId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitCost = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supply_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supply_items_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_supply_items_supplies_SupplyId",
                        column: x => x.SupplyId,
                        principalTable: "supplies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_supplies_Reference",
                table: "supplies",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_supplies_SupplierId_SupplyDate",
                table: "supplies",
                columns: new[] { "SupplierId", "SupplyDate" });

            migrationBuilder.CreateIndex(
                name: "IX_supplies_SupplyDate",
                table: "supplies",
                column: "SupplyDate");

            migrationBuilder.CreateIndex(
                name: "IX_supply_items_ProductId",
                table: "supply_items",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_supply_items_SupplyId",
                table: "supply_items",
                column: "SupplyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supply_items");

            migrationBuilder.DropTable(
                name: "supplies");
        }
    }
}
