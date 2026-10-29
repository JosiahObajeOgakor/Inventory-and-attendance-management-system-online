using ClosedXML.Excel;
using Inventory.Application.Documents;
using Inventory.Application.Ledger;

namespace Inventory.Infrastructure.Documents;

/// <summary>The financial statements as a workbook: one sheet each, with the company, period and totals, and numbers Excel can add up.</summary>
public sealed partial class ExcelExporter
{
    public static byte[] Statements(StatementsDoc d)
    {
        using var wb = new XLWorkbook();
        var scope = string.IsNullOrWhiteSpace(d.Branch) ? "Whole business" : d.Branch!;
        if (d.Which is "pl" or "all")
        {
            var p = d.ProfitAndLoss;
            var (ws, r) = Titled(wb, "Profit and loss", d.Brand.Name, $"Profit and loss, {p.From:dd MMM yyyy} to {p.To:dd MMM yyyy} · {scope}");
            r = Block(ws, r, "Income", p.Income, "Total income", p.TotalIncome);
            r = Block(ws, r + 1, "Expenses", p.Expenses, "Total expenses", p.TotalExpenses);
            Total(ws, r + 1, p.NetProfit < 0 ? "Net loss" : "Net profit", p.NetProfit, grand: true);
            Finish(ws);
        }
        if (d.Which is "bs" or "all")
        {
            var b = d.BalanceSheet;
            var (ws, r) = Titled(wb, "Balance sheet", d.Brand.Name, $"Balance sheet as at {b.AsOf:dd MMM yyyy} · {scope}{(b.Balanced ? "" : " · DOES NOT BALANCE")}");
            r = Block(ws, r, "Assets", b.Assets, "Total assets", b.TotalAssets);
            r = Block(ws, r + 1, "Liabilities", b.Liabilities, "Total liabilities", b.TotalLiabilities);
            r = Block(ws, r + 1, "Equity", [.. b.Equity, new StatementLine("", "Earnings to date", b.EarningsToDate)], "Total equity", b.TotalEquity);
            Total(ws, r + 1, "Liabilities + equity", b.TotalLiabilities + b.TotalEquity, grand: true);
            Finish(ws);
        }
        if (d.Which is "tb" or "all")
        {
            var t = d.TrialBalance;
            var (ws, r) = Titled(wb, "Trial balance", d.Brand.Name, $"Trial balance as at {t.AsOf:dd MMM yyyy} · {scope}{(t.Balanced ? "" : " · OUT OF BALANCE")}");
            string[] heads = ["Code", "Account", "Type", "Debit", "Credit", "Balance"];
            for (var i = 0; i < heads.Length; i++) ws.Cell(r, i + 1).Value = heads[i];
            Head(ws.Range(r, 1, r, heads.Length));
            foreach (var row in t.Rows)
            {
                r++;
                ws.Cell(r, 1).Value = Safe(row.Code); ws.Cell(r, 2).Value = Safe(row.Name); ws.Cell(r, 3).Value = row.Type;
                ws.Cell(r, 4).Value = row.Debit; ws.Cell(r, 5).Value = row.Credit; ws.Cell(r, 6).Value = row.Balance;
            }
            r++;
            ws.Cell(r, 2).Value = "Totals"; ws.Cell(r, 4).Value = t.TotalDebit; ws.Cell(r, 5).Value = t.TotalCredit;
            ws.Range(r, 1, r, 6).Style.Font.Bold = true;
            ws.Range(r, 1, r, 6).Style.Border.TopBorder = XLBorderStyleValues.Thin;
            ws.Range(5, 4, r, 6).Style.NumberFormat.Format = Money;
            Finish(ws);
        }
        return Save(wb);
    }

    private static (IXLWorksheet Sheet, int NextRow) Titled(XLWorkbook wb, string name, string company, string subtitle)
    {
        var ws = wb.Worksheets.Add(name);
        ws.Cell(1, 1).Value = company; ws.Cell(1, 1).Style.Font.Bold = true; ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = subtitle; ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#56636C");
        return (ws, 4);
    }

    private static void Head(IXLRange range)
    {
        range.Style.Font.Bold = true;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#1f3d34");
        range.Style.Font.FontColor = XLColor.White;
    }

    /// <summary>A section: heading row, one row per account, total row. Returns the row after it.</summary>
    private static int Block(IXLWorksheet ws, int r, string title, IReadOnlyList<StatementLine> lines, string totalLabel, decimal total)
    {
        ws.Cell(r, 1).Value = title; ws.Cell(r, 2).Value = "Account"; ws.Cell(r, 3).Value = "Amount (₦)";
        Head(ws.Range(r, 1, r, 3));
        foreach (var l in lines)
        {
            r++;
            ws.Cell(r, 1).Value = Safe(l.Code); ws.Cell(r, 2).Value = Safe(l.Name);
            ws.Cell(r, 3).Value = l.Amount; ws.Cell(r, 3).Style.NumberFormat.Format = Money;
        }
        Total(ws, r + 1, totalLabel, total);
        return r + 2;
    }

    private static void Total(IXLWorksheet ws, int r, string label, decimal amount, bool grand = false)
    {
        ws.Cell(r, 2).Value = label; ws.Cell(r, 3).Value = amount; ws.Cell(r, 3).Style.NumberFormat.Format = Money;
        var row = ws.Range(r, 1, r, 3);
        row.Style.Font.Bold = true;
        row.Style.Border.TopBorder = grand ? XLBorderStyleValues.Double : XLBorderStyleValues.Thin;
        if (grand) row.Style.Font.FontSize = 12;
    }
}
