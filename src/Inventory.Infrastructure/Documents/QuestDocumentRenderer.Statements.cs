using Inventory.Application.Documents;
using Inventory.Application.Ledger;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Inventory.Infrastructure.Documents;

/// <summary>The financial statements on the business's letterhead: profit and loss, balance sheet and trial balance, one page each.</summary>
public sealed partial class QuestDocumentRenderer
{
    public byte[] Statements(StatementsDoc d) => StatementsDocument(d with { Brand = Clean(d.Brand) }).GeneratePdf();

    private static IDocument StatementsDocument(StatementsDoc d) => Compose(doc =>
    {
        var accent = Accent(d.Brand.CompanyKey);
        var scope = string.IsNullOrWhiteSpace(d.Branch) ? "Whole business" : d.Branch!;
        var footer = $"{d.Brand.Name}   ·   {scope}   ·   from the general ledger, generated {d.GeneratedAt:dd MMM yyyy HH:mm}";
        if (d.Which is "pl" or "all") doc.Page(page =>
        {
            Frame(page, d.Brand, footer);
            page.Content().Column(col =>
            {
                var p = d.ProfitAndLoss;
                Letterhead(col, d.Brand, accent, "PROFIT AND LOSS", [("From", D(p.From)), ("To", D(p.To)), ("Covers", scope)]);
                Section(col, accent, "INCOME", p.Income, "Total income", p.TotalIncome);
                Section(col, accent, "EXPENSES", p.Expenses, "Total expenses", p.TotalExpenses);
                Grand(col, accent, p.NetProfit < 0 ? "NET LOSS" : "NET PROFIT", p.NetProfit, p.NetProfit < 0);
                if (p.TotalIncome != 0)
                    col.Item().PaddingTop(4).AlignRight().Text($"Net margin {p.NetProfit / p.TotalIncome:P1} of income").FontSize(8).FontColor(Muted);
            });
        });
        if (d.Which is "bs" or "all") doc.Page(page =>
        {
            Frame(page, d.Brand, footer);
            page.Content().Column(col =>
            {
                var b = d.BalanceSheet;
                Letterhead(col, d.Brand, accent, "BALANCE SHEET", [("As at", D(b.AsOf)), ("Covers", scope), ("Check", b.Balanced ? "Balanced" : "DOES NOT BALANCE")]);
                Section(col, accent, "ASSETS", b.Assets, "Total assets", b.TotalAssets);
                Section(col, accent, "LIABILITIES", b.Liabilities, "Total liabilities", b.TotalLiabilities);
                Section(col, accent, "EQUITY", [.. b.Equity, new StatementLine("", "Earnings to date", b.EarningsToDate)], "Total equity", b.TotalEquity);
                Grand(col, accent, "LIABILITIES + EQUITY", b.TotalLiabilities + b.TotalEquity, !b.Balanced);
            });
        });
        if (d.Which is "tb" or "all") doc.Page(page =>
        {
            Frame(page, d.Brand, footer);
            page.Content().Column(col =>
            {
                var t = d.TrialBalance;
                Letterhead(col, d.Brand, accent, "TRIAL BALANCE", [("As at", D(t.AsOf)), ("Covers", scope), ("Check", t.Balanced ? "Balanced" : "OUT OF BALANCE")]);
                col.Item().Table(tb =>
                {
                    tb.ColumnsDefinition(c => { c.ConstantColumn(44); c.RelativeColumn(); c.ConstantColumn(62); c.ConstantColumn(84); c.ConstantColumn(84); });
                    foreach (var h in new[] { "Code", "Account", "Type", "Debit", "Credit" })
                    {
                        var hc = tb.Cell().Element(HeadCell(accent));
                        (h is "Debit" or "Credit" ? hc.AlignRight() : hc).Text(h).Bold().FontSize(8.5f);
                    }
                    foreach (var r in t.Rows)
                    {
                        tb.Cell().Element(BodyCell).Text(r.Code).FontColor(Muted);
                        tb.Cell().Element(BodyCell).Text(r.Name);
                        tb.Cell().Element(BodyCell).Text(r.Type).FontColor(Muted);
                        tb.Cell().Element(BodyCell).AlignRight().Text(r.Debit == 0 ? "" : N2(r.Debit));
                        tb.Cell().Element(BodyCell).AlignRight().Text(r.Credit == 0 ? "" : N2(r.Credit));
                    }
                    tb.Cell().ColumnSpan(3).Element(FootCell).Text("Totals").Bold();
                    tb.Cell().Element(FootCell).AlignRight().Text(N2(t.TotalDebit)).Bold();
                    tb.Cell().Element(FootCell).AlignRight().Text(N2(t.TotalCredit)).Bold();
                });
            });
        });
    });

    /// <summary>A heading, one row per account, and the section total.</summary>
    private static void Section(ColumnDescriptor col, string accent, string title, IReadOnlyList<StatementLine> lines, string totalLabel, decimal total)
    {
        col.Item().PaddingTop(8).Text(title).Bold().FontSize(9).FontColor(accent);
        col.Item().PaddingTop(2).Table(t =>
        {
            t.ColumnsDefinition(c => { c.ConstantColumn(44); c.RelativeColumn(); c.ConstantColumn(110); });
            foreach (var l in lines)
            {
                t.Cell().Element(BodyCell).Text(l.Code).FontColor(Muted);
                t.Cell().Element(BodyCell).Text(l.Name);
                t.Cell().Element(BodyCell).AlignRight().Text(Money(l.Amount));
            }
            if (lines.Count == 0) t.Cell().ColumnSpan(3).Element(BodyCell).Text("None in this period").FontColor(Muted).Italic();
            t.Cell().ColumnSpan(2).Element(FootCell).Text(totalLabel).Bold();
            t.Cell().Element(FootCell).AlignRight().Text(Money(total)).Bold();
        });
    }

    private static void Grand(ColumnDescriptor col, string accent, string label, decimal amount, bool bad) =>
        col.Item().PaddingTop(10).Background(Colors.Grey.Lighten4).BorderTop(2).BorderColor(accent).Padding(6).Row(r =>
        {
            r.RelativeItem().Text(label).Bold().FontSize(11);
            r.ConstantItem(150).AlignRight().Text(Money(amount)).Bold().FontSize(11).FontColor(bad ? Danger : Ink);
        });
}
