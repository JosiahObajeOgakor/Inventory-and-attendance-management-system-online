using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SupplierPayments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "supplier_payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    PurchaseOrderId = table.Column<int>(type: "int", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Reference = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    PaidByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_supplier_payments_purchase_orders_PurchaseOrderId",
                        column: x => x.PurchaseOrderId,
                        principalTable: "purchase_orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_supplier_payments_suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_PurchaseOrderId",
                table: "supplier_payments",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_SupplierId_PaidAt",
                table: "supplier_payments",
                columns: new[] { "SupplierId", "PaidAt" });

            // Orders paid before payments were recorded one by one: carry what each has been paid over as a single "Earlier" payment on its
            // order date, so every supplier's page shows its full payment history from day one.
            migrationBuilder.Sql("""
                INSERT INTO supplier_payments (SupplierId, PurchaseOrderId, PaidAt, Amount, Method, Reference, PaidByUserId)
                SELECT SupplierId, Id, CAST(OrderDate AS DATETIME(6)), AmountPaid, 'Earlier', LEFT(PoNumber, 40), CreatedByUserId
                FROM purchase_orders WHERE AmountPaid > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_payments");
        }
    }
}
