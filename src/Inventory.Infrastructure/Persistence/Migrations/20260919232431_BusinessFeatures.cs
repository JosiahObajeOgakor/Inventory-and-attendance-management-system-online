using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BusinessFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "attendance_events",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<int>(type: "int", nullable: false),
                    FullName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    EventType = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    HappenedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    WorkDate = table.Column<DateTime>(type: "date", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attendance_events", x => x.Id);
                    table.CheckConstraint("CK_attendance_type", "EventType IN ('In','Out','Declined')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "company_assets",
                columns: table => new
                {
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ContentType = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Data = table.Column<byte[]>(type: "longblob", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_assets", x => x.Kind);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "company_profile",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    LegalName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Address = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: false),
                    Phone = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Email = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    TaxId = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    DefaultVatRate = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    DefaultRebateRatePct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_profile", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    FullName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false),
                    Position = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    Phone = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    StartedOn = table.Column<DateTime>(type: "date", nullable: false),
                    MonthlySalary = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employees", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "price_changes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    OldDistributor = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    OldWholesaler = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    OldRetail = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    NewDistributor = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    NewWholesaler = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    NewRetail = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ChangedByUserId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_changes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_price_changes_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "product_serials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    SerialNumber = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    BatchId = table.Column<int>(type: "int", nullable: true),
                    WarehouseId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ReceivedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    SoldAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    InvoiceId = table.Column<int>(type: "int", nullable: true),
                    Notes = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_serials", x => x.Id);
                    table.CheckConstraint("CK_serial_status", "Status IN ('In Stock','Sold','Returned','Written Off')");
                    table.ForeignKey(
                        name: "FK_product_serials_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "quotations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    QuotationNumber = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    QuotationDate = table.Column<DateTime>(type: "date", nullable: false),
                    Subtotal = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    DiscountPct = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    VatRate = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    VatAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    TotalAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    PriceTier = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ConvertedInvoiceId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quotations_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotations_invoices_ConvertedInvoiceId",
                        column: x => x.ConvertedInvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "waybills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WaybillNumber = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    InvoiceId = table.Column<int>(type: "int", nullable: false),
                    IssueDate = table.Column<DateTime>(type: "date", nullable: false),
                    DriverName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    DriverPhone = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    VehiclePlate = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true),
                    DestinationAddress = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true),
                    Notes = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_waybills", x => x.Id);
                    table.ForeignKey(
                        name: "FK_waybills_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "company_banks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    CompanyProfileId = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    BankName = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    AccountName = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    AccountNumber = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_company_banks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_company_banks_company_profile_CompanyProfileId",
                        column: x => x.CompanyProfileId,
                        principalTable: "company_profile",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_loans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    LoanDate = table.Column<DateTime>(type: "date", nullable: false),
                    Principal = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Closed = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_employee_loans_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "employee_monthly",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    PeriodYear = table.Column<int>(type: "int", nullable: false),
                    PeriodMonth = table.Column<int>(type: "int", nullable: false),
                    Position = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    SalaryAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    LoanDeduction = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Paid = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_employee_monthly", x => x.Id);
                    table.ForeignKey(
                        name: "FK_employee_monthly_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "quotation_items",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    QuotationId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Quantity = table.Column<int>(type: "int", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "decimal(12,2)", precision: 12, scale: 2, nullable: false),
                    LineTotal = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quotation_items", x => x.Id);
                    table.ForeignKey(
                        name: "FK_quotation_items_products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_quotation_items_quotations_QuotationId",
                        column: x => x.QuotationId,
                        principalTable: "quotations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "loan_repayments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    LoanId = table.Column<int>(type: "int", nullable: false),
                    PaidDate = table.Column<DateTime>(type: "date", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Note = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_loan_repayments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_loan_repayments_employee_loans_LoanId",
                        column: x => x.LoanId,
                        principalTable: "employee_loans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_UserId_WorkDate",
                table: "attendance_events",
                columns: new[] { "UserId", "WorkDate" });

            migrationBuilder.CreateIndex(
                name: "IX_attendance_events_WorkDate",
                table: "attendance_events",
                column: "WorkDate");

            migrationBuilder.CreateIndex(
                name: "IX_company_banks_CompanyProfileId",
                table: "company_banks",
                column: "CompanyProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_employee_loans_EmployeeId",
                table: "employee_loans",
                column: "EmployeeId");

            migrationBuilder.CreateIndex(
                name: "IX_employee_monthly_EmployeeId_PeriodYear_PeriodMonth",
                table: "employee_monthly",
                columns: new[] { "EmployeeId", "PeriodYear", "PeriodMonth" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_loan_repayments_LoanId",
                table: "loan_repayments",
                column: "LoanId");

            migrationBuilder.CreateIndex(
                name: "IX_price_changes_ProductId_ChangedAt",
                table: "price_changes",
                columns: new[] { "ProductId", "ChangedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_product_serials_ProductId_SerialNumber",
                table: "product_serials",
                columns: new[] { "ProductId", "SerialNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_serials_ProductId_Status",
                table: "product_serials",
                columns: new[] { "ProductId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_quotation_items_ProductId",
                table: "quotation_items",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_quotation_items_QuotationId",
                table: "quotation_items",
                column: "QuotationId");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_ConvertedInvoiceId",
                table: "quotations",
                column: "ConvertedInvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_CustomerId",
                table: "quotations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_quotations_QuotationNumber",
                table: "quotations",
                column: "QuotationNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_waybills_InvoiceId",
                table: "waybills",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_waybills_WaybillNumber",
                table: "waybills",
                column: "WaybillNumber",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attendance_events");

            migrationBuilder.DropTable(
                name: "company_assets");

            migrationBuilder.DropTable(
                name: "company_banks");

            migrationBuilder.DropTable(
                name: "employee_monthly");

            migrationBuilder.DropTable(
                name: "loan_repayments");

            migrationBuilder.DropTable(
                name: "price_changes");

            migrationBuilder.DropTable(
                name: "product_serials");

            migrationBuilder.DropTable(
                name: "quotation_items");

            migrationBuilder.DropTable(
                name: "waybills");

            migrationBuilder.DropTable(
                name: "company_profile");

            migrationBuilder.DropTable(
                name: "employee_loans");

            migrationBuilder.DropTable(
                name: "quotations");

            migrationBuilder.DropTable(
                name: "employees");
        }
    }
}
