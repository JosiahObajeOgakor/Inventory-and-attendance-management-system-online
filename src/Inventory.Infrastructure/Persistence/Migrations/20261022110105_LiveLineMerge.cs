using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class LiveLineMerge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Joins the live line (Sep 30) with the branch work. Everything below only adds tables, columns and indexes, except that payment_links.Status
            // is widened (the status check is dropped and re-added unchanged around that). A schema left half-done by the rolled-back deploy is put back
            // first by DatabaseProvisioner.UndoHalfAppliedLiveLineMergeAsync.
            migrationBuilder.DropCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links");

            migrationBuilder.AddColumn<int>(
                name: "BranchId",
                table: "warehouses",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountName",
                table: "supplier_payments",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AccountNumber",
                table: "supplier_payments",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "supplier_payments",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

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

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "payment_links",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(10)",
                oldMaxLength: 10);

            migrationBuilder.AddColumn<string>(
                name: "AccountNumber",
                table: "payment_links",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BankName",
                table: "payment_links",
                type: "varchar(60)",
                maxLength: 60,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "payment_links",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalId",
                table: "payment_links",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketingSource",
                table: "customers",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MarketingStatus",
                table: "customers",
                type: "varchar(12)",
                maxLength: 12,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "MarketingUpdatedAt",
                table: "customers",
                type: "datetime(6)",
                precision: 6,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreferredLanguage",
                table: "customers",
                type: "varchar(5)",
                maxLength: 5,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AiCalls",
                table: "chat_conversations",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DraftJson",
                table: "chat_conversations",
                type: "varchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Language",
                table: "chat_conversations",
                type: "varchar(5)",
                maxLength: 5,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "chat_conversations",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "app_records",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    App = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    Kind = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ParentId = table.Column<int>(type: "int", nullable: true),
                    Title = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Code = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    Date = table.Column<DateTime>(type: "date", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: true),
                    Data = table.Column<string>(type: "longtext", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedByName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_app_records_app_records_ParentId",
                        column: x => x.ParentId,
                        principalTable: "app_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "approval_requests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Action = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Summary = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    RequestedByName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    RequestedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedByName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionNote = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true),
                    FailureReason = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_requests", x => x.Id);
                    table.CheckConstraint("CK_approval_requests_status", "Status IN ('Pending','Executing','Executed','Rejected','Failed')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "approval_rules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Action = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    ThresholdAmount = table.Column<decimal>(type: "decimal(14,2)", precision: 14, scale: 2, nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approval_rules", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    BankName = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    AccountNumber = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    GlCode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_accounts", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "branches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Address = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: true),
                    State = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true),
                    Phone = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true),
                    IsHeadOffice = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_branches", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "campaign_messages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    CampaignId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Recipient = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false),
                    Channel = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Language = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false),
                    Text = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Detail = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Mode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_messages", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "campaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Type = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    Enabled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SendWhatsApp = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SendEmail = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    RunMinute = table.Column<int>(type: "int", nullable: false),
                    EveryNDays = table.Column<int>(type: "int", nullable: false),
                    StartDate = table.Column<DateTime>(type: "date", nullable: true),
                    EndDate = table.Column<DateTime>(type: "date", nullable: true),
                    CooldownDays = table.Column<int>(type: "int", nullable: false),
                    MaxRecipients = table.Column<int>(type: "int", nullable: false),
                    ThresholdDays = table.Column<int>(type: "int", nullable: false),
                    FallbackRepurchaseDays = table.Column<int>(type: "int", nullable: false),
                    LastRunOn = table.Column<DateTime>(type: "date", nullable: true),
                    LastRunAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    LastRunSent = table.Column<int>(type: "int", nullable: false),
                    LastRunNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    UpdatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    UpdatedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "chat_events",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    At = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    Channel = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    ConversationId = table.Column<int>(type: "int", nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: true),
                    Reference = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_events", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "currencies",
                columns: table => new
                {
                    Code = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Name = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    Symbol = table.Column<string>(type: "varchar(8)", maxLength: 8, nullable: false),
                    Active = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_currencies", x => x.Code);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "depreciation_runs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Assets = table.Column<int>(type: "int", nullable: false),
                    RunByUserId = table.Column<int>(type: "int", nullable: false),
                    RunAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_depreciation_runs", x => x.Id);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fx_documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Number = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    DueDate = table.Column<DateTime>(type: "date", nullable: true),
                    Description = table.Column<string>(type: "varchar(250)", maxLength: 250, nullable: false),
                    AccountCode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    ForeignAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ForeignSettled = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Settled = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_documents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fx_documents_customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fx_documents_suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "gl_accounts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    IsControl = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_accounts", x => x.Id);
                    table.CheckConstraint("CK_gl_accounts_type", "Type IN ('Asset','Liability','Equity','Income','Expense')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "gl_periods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    ClosedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    ClosedBy = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_periods", x => x.Id);
                    table.CheckConstraint("CK_gl_periods_status", "Status IN ('Open','SoftClosed','Locked')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "leave_types",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    DaysPerYear = table.Column<int>(type: "int", nullable: false),
                    Paid = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IsActive = table.Column<bool>(type: "tinyint(1)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_types", x => x.Id);
                    table.CheckConstraint("CK_leave_types_days", "DaysPerYear >= 0");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "tax_filings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Kind = table.Column<string>(type: "varchar(5)", maxLength: 5, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Authority = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false),
                    TaxDue = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Credits = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    PaidOn = table.Column<DateTime>(type: "date", nullable: false),
                    Reference = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tax_filings", x => x.Id);
                    table.CheckConstraint("CK_tax_filings_kind", "Kind IN ('VAT')");
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "campaign_recipients",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    RecordId = table.Column<int>(type: "int", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    Channel = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Address = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    Subject = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    Message = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Error = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    DedupeKey = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    SentAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaign_recipients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_campaign_recipients_app_records_RecordId",
                        column: x => x.RecordId,
                        principalTable: "app_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_reconciliations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    BankAccountId = table.Column<int>(type: "int", nullable: false),
                    StatementDate = table.Column<DateTime>(type: "date", nullable: false),
                    StatementBalance = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    LedgerBalance = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    UnclearedReceipts = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    UnclearedPayments = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    CompletedByUserId = table.Column<int>(type: "int", nullable: true),
                    CompletedByName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_reconciliations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_reconciliations_bank_accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "bank_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "budgets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    BranchId = table.Column<int>(type: "int", nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false),
                    ApprovedByName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    ApprovedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budgets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_budgets_branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fixed_assets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Code = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    Category = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    SerialNumber = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    Location = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    BranchId = table.Column<int>(type: "int", nullable: true),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    AcquiredOn = table.Column<DateTime>(type: "date", nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    ResidualValue = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    UsefulLifeMonths = table.Column<int>(type: "int", nullable: false),
                    RatePct = table.Column<decimal>(type: "decimal(6,3)", precision: 6, scale: 3, nullable: false),
                    AccumulatedDepreciation = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    DisposedOn = table.Column<DateTime>(type: "date", nullable: true),
                    DisposalProceeds = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: true),
                    DisposalGainLoss = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fixed_assets", x => x.Id);
                    table.CheckConstraint("CK_fixed_assets_cost", "Cost > 0 AND ResidualValue >= 0 AND AccumulatedDepreciation >= 0");
                    table.ForeignKey(
                        name: "FK_fixed_assets_branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_fixed_assets_suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "gl_journals",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    SourceType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    SourceId = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    SourceEvent = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    Memo = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    ReversalOfId = table.Column<long>(type: "bigint", nullable: true),
                    BranchId = table.Column<int>(type: "int", nullable: true),
                    PreparedByUserId = table.Column<int>(type: "int", nullable: true),
                    PreparedByName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    ApprovedByName = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_journals", x => x.Id);
                    table.CheckConstraint("CK_gl_journals_status", "Status IN ('Draft','Posted','Reversed')");
                    table.ForeignKey(
                        name: "FK_gl_journals_branches_BranchId",
                        column: x => x.BranchId,
                        principalTable: "branches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "exchange_rates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Currency = table.Column<string>(type: "varchar(3)", maxLength: 3, nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    Source = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exchange_rates", x => x.Id);
                    table.CheckConstraint("CK_exchange_rates_rate", "Rate > 0");
                    table.ForeignKey(
                        name: "FK_exchange_rates_currencies_Currency",
                        column: x => x.Currency,
                        principalTable: "currencies",
                        principalColumn: "Code",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "fx_settlements",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    DocumentId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    ForeignAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    BaseAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    CarryingAmount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    GainLoss = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    BankCode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fx_settlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_fx_settlements_fx_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "fx_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "leave_requests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    EmployeeId = table.Column<int>(type: "int", nullable: false),
                    LeaveTypeId = table.Column<int>(type: "int", nullable: false),
                    FromDate = table.Column<DateTime>(type: "date", nullable: false),
                    ToDate = table.Column<DateTime>(type: "date", nullable: false),
                    Days = table.Column<int>(type: "int", nullable: false),
                    Reason = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    RequestedByName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    DecidedByUserId = table.Column<int>(type: "int", nullable: true),
                    DecidedByName = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true),
                    DecidedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: true),
                    DecisionNote = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime(6)", precision: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_leave_requests", x => x.Id);
                    table.CheckConstraint("CK_leave_requests_dates", "ToDate >= FromDate AND Days > 0");
                    table.ForeignKey(
                        name: "FK_leave_requests_employees_EmployeeId",
                        column: x => x.EmployeeId,
                        principalTable: "employees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_leave_requests_leave_types_LeaveTypeId",
                        column: x => x.LeaveTypeId,
                        principalTable: "leave_types",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "budget_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    BudgetId = table.Column<int>(type: "int", nullable: false),
                    AccountCode = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_lines", x => x.Id);
                    table.CheckConstraint("CK_budget_lines_month", "Month BETWEEN 1 AND 12");
                    table.ForeignKey(
                        name: "FK_budget_lines_budgets_BudgetId",
                        column: x => x.BudgetId,
                        principalTable: "budgets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "asset_depreciation",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    AssetId = table.Column<int>(type: "int", nullable: false),
                    Year = table.Column<int>(type: "int", nullable: false),
                    Month = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    RunId = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_depreciation", x => x.Id);
                    table.ForeignKey(
                        name: "FK_asset_depreciation_fixed_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "fixed_assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "gl_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    JournalId = table.Column<long>(type: "bigint", nullable: false),
                    AccountId = table.Column<int>(type: "int", nullable: false),
                    Debit = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    Credit = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: true),
                    SupplierId = table.Column<int>(type: "int", nullable: true),
                    ProductId = table.Column<int>(type: "int", nullable: true),
                    Note = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gl_lines", x => x.Id);
                    table.CheckConstraint("CK_gl_lines_amounts", "Debit >= 0 AND Credit >= 0 AND NOT (Debit > 0 AND Credit > 0)");
                    table.ForeignKey(
                        name: "FK_gl_lines_gl_accounts_AccountId",
                        column: x => x.AccountId,
                        principalTable: "gl_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_gl_lines_gl_journals_JournalId",
                        column: x => x.JournalId,
                        principalTable: "gl_journals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "bank_statement_lines",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    BankAccountId = table.Column<int>(type: "int", nullable: false),
                    Date = table.Column<DateTime>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false),
                    Reference = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: true),
                    Amount = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: false),
                    RunningBalance = table.Column<decimal>(type: "decimal(16,2)", precision: 16, scale: 2, nullable: true),
                    Status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false),
                    GlLineId = table.Column<long>(type: "bigint", nullable: true),
                    ReconciliationId = table.Column<int>(type: "int", nullable: true),
                    ImportBatch = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    Fingerprint = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bank_statement_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_bank_accounts_BankAccountId",
                        column: x => x.BankAccountId,
                        principalTable: "bank_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_bank_statement_lines_gl_lines_GlLineId",
                        column: x => x.GlLineId,
                        principalTable: "gl_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySQL:Charset", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_warehouses_BranchId",
                table: "warehouses",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_links_ExternalId",
                table: "payment_links",
                column: "ExternalId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links",
                sql: "Status IN ('Pending','Paid','PaidUnfulfilled','Cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_app_records_App_Kind_Code",
                table: "app_records",
                columns: new[] { "App", "Kind", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_app_records_App_Kind_ParentId",
                table: "app_records",
                columns: new[] { "App", "Kind", "ParentId" });

            migrationBuilder.CreateIndex(
                name: "IX_app_records_App_Kind_Status",
                table: "app_records",
                columns: new[] { "App", "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_app_records_ParentId",
                table: "app_records",
                column: "ParentId");

            migrationBuilder.CreateIndex(
                name: "IX_approval_requests_Status_Id",
                table: "approval_requests",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_approval_rules_Action",
                table: "approval_rules",
                column: "Action",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_depreciation_AssetId_Year_Month",
                table: "asset_depreciation",
                columns: new[] { "AssetId", "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_accounts_GlCode",
                table: "bank_accounts",
                column: "GlCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_reconciliations_BankAccountId",
                table: "bank_reconciliations",
                column: "BankAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_BankAccountId_Fingerprint",
                table: "bank_statement_lines",
                columns: new[] { "BankAccountId", "Fingerprint" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_BankAccountId_Status_Date",
                table: "bank_statement_lines",
                columns: new[] { "BankAccountId", "Status", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_bank_statement_lines_GlLineId",
                table: "bank_statement_lines",
                column: "GlLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_branches_Code",
                table: "branches",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budget_lines_BudgetId_AccountCode_Month",
                table: "budget_lines",
                columns: new[] { "BudgetId", "AccountCode", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_budgets_BranchId",
                table: "budgets",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_campaign_messages_CampaignId_CreatedAt",
                table: "campaign_messages",
                columns: new[] { "CampaignId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_messages_CustomerId_CreatedAt",
                table: "campaign_messages",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_RecordId_DedupeKey",
                table: "campaign_recipients",
                columns: new[] { "RecordId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_RecordId_Status",
                table: "campaign_recipients",
                columns: new[] { "RecordId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_campaign_recipients_Status_Id",
                table: "campaign_recipients",
                columns: new[] { "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_Type",
                table: "campaigns",
                column: "Type",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_events_At_Kind",
                table: "chat_events",
                columns: new[] { "At", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_events_ConversationId",
                table: "chat_events",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_depreciation_runs_Year_Month",
                table: "depreciation_runs",
                columns: new[] { "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exchange_rates_Currency_Date",
                table: "exchange_rates",
                columns: new[] { "Currency", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fixed_assets_BranchId",
                table: "fixed_assets",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_fixed_assets_Code",
                table: "fixed_assets",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fixed_assets_SupplierId",
                table: "fixed_assets",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_fx_documents_CustomerId",
                table: "fx_documents",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_fx_documents_Number",
                table: "fx_documents",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fx_documents_SupplierId",
                table: "fx_documents",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_fx_settlements_DocumentId",
                table: "fx_settlements",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_gl_accounts_Code",
                table: "gl_accounts",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gl_journals_BranchId_Date",
                table: "gl_journals",
                columns: new[] { "BranchId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_journals_Date",
                table: "gl_journals",
                column: "Date");

            migrationBuilder.CreateIndex(
                name: "IX_gl_journals_SourceType_SourceId_SourceEvent",
                table: "gl_journals",
                columns: new[] { "SourceType", "SourceId", "SourceEvent" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gl_lines_AccountId_JournalId",
                table: "gl_lines",
                columns: new[] { "AccountId", "JournalId" });

            migrationBuilder.CreateIndex(
                name: "IX_gl_lines_CustomerId",
                table: "gl_lines",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_gl_lines_JournalId",
                table: "gl_lines",
                column: "JournalId");

            migrationBuilder.CreateIndex(
                name: "IX_gl_lines_SupplierId",
                table: "gl_lines",
                column: "SupplierId");

            migrationBuilder.CreateIndex(
                name: "IX_gl_periods_Year_Month",
                table: "gl_periods",
                columns: new[] { "Year", "Month" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_EmployeeId_FromDate",
                table: "leave_requests",
                columns: new[] { "EmployeeId", "FromDate" });

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_LeaveTypeId",
                table: "leave_requests",
                column: "LeaveTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_leave_requests_Status",
                table: "leave_requests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_leave_types_Name",
                table: "leave_types",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tax_filings_Kind_Year_Month",
                table: "tax_filings",
                columns: new[] { "Kind", "Year", "Month" });

            migrationBuilder.AddForeignKey(
                name: "FK_warehouses_branches_BranchId",
                table: "warehouses",
                column: "BranchId",
                principalTable: "branches",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_warehouses_branches_BranchId",
                table: "warehouses");

            migrationBuilder.DropTable(
                name: "approval_requests");

            migrationBuilder.DropTable(
                name: "approval_rules");

            migrationBuilder.DropTable(
                name: "asset_depreciation");

            migrationBuilder.DropTable(
                name: "bank_reconciliations");

            migrationBuilder.DropTable(
                name: "bank_statement_lines");

            migrationBuilder.DropTable(
                name: "budget_lines");

            migrationBuilder.DropTable(
                name: "campaign_messages");

            migrationBuilder.DropTable(
                name: "campaign_recipients");

            migrationBuilder.DropTable(
                name: "campaigns");

            migrationBuilder.DropTable(
                name: "chat_events");

            migrationBuilder.DropTable(
                name: "depreciation_runs");

            migrationBuilder.DropTable(
                name: "exchange_rates");

            migrationBuilder.DropTable(
                name: "fx_settlements");

            migrationBuilder.DropTable(
                name: "gl_periods");

            migrationBuilder.DropTable(
                name: "leave_requests");

            migrationBuilder.DropTable(
                name: "tax_filings");

            migrationBuilder.DropTable(
                name: "fixed_assets");

            migrationBuilder.DropTable(
                name: "bank_accounts");

            migrationBuilder.DropTable(
                name: "gl_lines");

            migrationBuilder.DropTable(
                name: "budgets");

            migrationBuilder.DropTable(
                name: "app_records");

            migrationBuilder.DropTable(
                name: "currencies");

            migrationBuilder.DropTable(
                name: "fx_documents");

            migrationBuilder.DropTable(
                name: "leave_types");

            migrationBuilder.DropTable(
                name: "gl_accounts");

            migrationBuilder.DropTable(
                name: "gl_journals");

            migrationBuilder.DropTable(
                name: "branches");

            migrationBuilder.DropIndex(
                name: "IX_warehouses_BranchId",
                table: "warehouses");

            migrationBuilder.DropIndex(
                name: "IX_payment_links_ExternalId",
                table: "payment_links");

            migrationBuilder.DropCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "BranchId",
                table: "warehouses");

            migrationBuilder.DropColumn(
                name: "AccountName",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "AccountNumber",
                table: "supplier_payments");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "supplier_payments");

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

            migrationBuilder.DropColumn(
                name: "AccountNumber",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "BankName",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "ExternalId",
                table: "payment_links");

            migrationBuilder.DropColumn(
                name: "MarketingSource",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "MarketingStatus",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "MarketingUpdatedAt",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "PreferredLanguage",
                table: "customers");

            migrationBuilder.DropColumn(
                name: "AiCalls",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "DraftJson",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "Language",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "State",
                table: "chat_conversations");

            migrationBuilder.AlterColumn<string>(
                name: "Status",
                table: "payment_links",
                type: "varchar(10)",
                maxLength: 10,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(20)",
                oldMaxLength: 20);

            migrationBuilder.AddCheckConstraint(
                name: "CK_payment_links_status",
                table: "payment_links",
                sql: "Status IN ('Pending','Paid','Cancelled')");
        }
    }
}
