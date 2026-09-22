using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "payment_links",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Reference = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    DocType = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    DocId = table.Column<int>(type: "int", nullable: false),
                    DocNumber = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    CustomerName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Url = table.Column<string>(type: "varchar(400)", maxLength: 400, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    PaidAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    PaidAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: true),
                    Channel = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    NotifiedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_links", x => x.Id);
                    table.CheckConstraint("CK_payment_links_status", "Status IN ('Pending','Paid')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_payment_links_DocType_DocId_Status",
                table: "payment_links",
                columns: new[] { "DocType", "DocId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_links_Reference",
                table: "payment_links",
                column: "Reference",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "payment_links");
        }
    }
}
