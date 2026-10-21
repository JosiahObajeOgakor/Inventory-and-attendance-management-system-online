using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations.Business
{
    /// <inheritdoc />
    public partial class PaymentAccountRecording : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "payments",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountNumber",
                table: "payments",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "payments",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerName",
                table: "payments",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "supplier_payments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    PurchaseOrderId = table.Column<int>(type: "int", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    PaymentDate = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    BankName = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    AccountName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true),
                    AccountNumber = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    PaidByUserId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_supplier_payments", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_PurchaseOrderId",
                table: "supplier_payments",
                column: "PurchaseOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_supplier_payments_SupplierId",
                table: "supplier_payments",
                column: "SupplierId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "AccountName",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "AccountNumber",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "PayerName",
                table: "payments");
        }
    }
}
