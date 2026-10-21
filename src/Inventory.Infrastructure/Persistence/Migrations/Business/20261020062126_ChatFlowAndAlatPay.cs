using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Infrastructure.Persistence.Migrations.Business
{
    /// <summary>
    /// WhatsApp sales chat on Twilio + AlatPay: payment links can now be a bank account shown in the chat (provider, processor id, account, bank, expiry),
    /// may end "PaidUnfulfilled", and each chat conversation keeps its state, language, draft order and AI-call count.
    /// Existing rows keep working: every existing link is a Paystack link, every existing conversation is idle English.
    /// </summary>
    public partial class ChatFlowAndAlatPay : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_payment_links_status", table: "payment_links");

            migrationBuilder.AlterColumn<string>(name: "Status", table: "payment_links", type: "varchar(20)", maxLength: 20, nullable: false,
                oldClrType: typeof(string), oldType: "varchar(10)", oldMaxLength: 10);

            migrationBuilder.AddColumn<string>(name: "AccountNumber", table: "payment_links", type: "varchar(30)", maxLength: 30, nullable: true);
            migrationBuilder.AddColumn<string>(name: "BankName", table: "payment_links", type: "varchar(60)", maxLength: 60, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "ExpiresAt", table: "payment_links", type: "datetime(6)", nullable: true);
            migrationBuilder.AddColumn<string>(name: "ExternalId", table: "payment_links", type: "varchar(80)", maxLength: 80, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Provider", table: "payment_links", type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "Paystack");

            migrationBuilder.AddCheckConstraint(name: "CK_payment_links_status", table: "payment_links", sql: "Status IN ('Pending','Paid','PaidUnfulfilled')");
            migrationBuilder.CreateIndex(name: "IX_payment_links_ExternalId", table: "payment_links", column: "ExternalId");

            migrationBuilder.AddColumn<int>(name: "AiCalls", table: "chat_conversations", type: "int", nullable: false, defaultValue: 0);
            migrationBuilder.AddColumn<string>(name: "DraftJson", table: "chat_conversations", type: "varchar(4000)", maxLength: 4000, nullable: true);
            migrationBuilder.AddColumn<string>(name: "Language", table: "chat_conversations", type: "varchar(5)", maxLength: 5, nullable: false, defaultValue: "en");
            migrationBuilder.AddColumn<string>(name: "State", table: "chat_conversations", type: "varchar(20)", maxLength: 20, nullable: false, defaultValue: "Idle");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_payment_links_status", table: "payment_links");
            migrationBuilder.DropIndex(name: "IX_payment_links_ExternalId", table: "payment_links");

            migrationBuilder.DropColumn(name: "AccountNumber", table: "payment_links");
            migrationBuilder.DropColumn(name: "BankName", table: "payment_links");
            migrationBuilder.DropColumn(name: "ExpiresAt", table: "payment_links");
            migrationBuilder.DropColumn(name: "ExternalId", table: "payment_links");
            migrationBuilder.DropColumn(name: "Provider", table: "payment_links");

            migrationBuilder.AlterColumn<string>(name: "Status", table: "payment_links", type: "varchar(10)", maxLength: 10, nullable: false,
                oldClrType: typeof(string), oldType: "varchar(20)", oldMaxLength: 20);
            migrationBuilder.AddCheckConstraint(name: "CK_payment_links_status", table: "payment_links", sql: "Status IN ('Pending','Paid')");

            migrationBuilder.DropColumn(name: "AiCalls", table: "chat_conversations");
            migrationBuilder.DropColumn(name: "DraftJson", table: "chat_conversations");
            migrationBuilder.DropColumn(name: "Language", table: "chat_conversations");
            migrationBuilder.DropColumn(name: "State", table: "chat_conversations");
        }
    }
}
