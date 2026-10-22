using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentProvidersAndDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "quotations",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "quotations",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryZone",
                table: "quotations",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AdminNotifiedAt",
                table: "payment_links",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "payment_links",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "paystack");

            migrationBuilder.AddColumn<string>(
                name: "ProviderReference",
                table: "payment_links",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RiderNotifiedAt",
                table: "payment_links",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "invoices",
                type: "varchar(250)",
                maxLength: 250,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFee",
                table: "invoices",
                type: "decimal(12,2)",
                precision: 12,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryZone",
                table: "invoices",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_links_Provider_ProviderReference",
                table: "payment_links",
                columns: new[] { "Provider", "ProviderReference" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_payment_links_Provider_ProviderReference",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "DeliveryZone",
                table: "quotations");

            migrationBuilder.DropColumn(
                name: "AdminNotifiedAt",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "ProviderReference",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "RiderNotifiedAt",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryFee",
                table: "invoices");

            migrationBuilder.DropColumn(
                name: "DeliveryZone",
                table: "invoices");
        }
    }
}
