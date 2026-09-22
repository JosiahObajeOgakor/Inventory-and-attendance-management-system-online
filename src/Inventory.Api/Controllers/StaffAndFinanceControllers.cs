using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Finance;
using Inventory.Application.Products;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Application.Staff;
using Inventory.Application.Trade;
using Inventory.Infrastructure.Documents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Controllers;

// ------------------------------------------------------------------ quotations & waybills (Staff)
[ApiController, Route("api/quotations")]
public class QuotationsController(ICurrentUser cu, QuotationService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<QuotationRowDto>> List([FromQuery] PageRequest page, CancellationToken ct) => svc.ListAsync(page, ct);

    [HttpGet("{id:int}"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<QuotationDetailDto>> Get(int id, CancellationToken ct) => await svc.GetAsync(id, ct) is { } q ? q : NotFound();

    [HttpPost, Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Create(QuoteRequest req, CancellationToken ct) => Ok(await svc.CreateAsync(req, Me, ct));

    [HttpPost("{id:int}/convert"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Convert(int id, ConvertQuoteRequest req, CancellationToken ct) => Ok(await svc.ConvertAsync(id, req, Me, ct));

    [HttpDelete("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, Me, ct); return NoContent(); }
}

[ApiController, Route("api/waybills")]
public class WaybillsController(ICurrentUser cu, WaybillService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<WaybillRowDto>> List([FromQuery] PageRequest page, CancellationToken ct) => svc.ListAsync(page, ct);

    [HttpGet("invoices"), Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<PendingInvoiceDto>> Invoices([FromQuery] PageRequest page, [FromQuery] bool pendingOnly, CancellationToken ct) => svc.InvoicesAsync(page, pendingOnly, ct);

    [HttpGet("prefill/{invoiceId:int}"), Authorize(Policy = Policies.Staff)]
    public Task<WaybillPrefillDto> Prefill(int invoiceId, CancellationToken ct) => svc.PrefillAsync(invoiceId, ct);

    [HttpPost, Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Create(WaybillInput input, CancellationToken ct) => Ok(new { id = await svc.CreateAsync(input, Me, ct) });
}

// ------------------------------------------------------------------ attendance
[ApiController, Route("api/attendance")]
public class AttendanceController(ICurrentUser cu, AttendanceService svc) : AppController(cu)
{
    [HttpGet("today"), Authorize(Policy = Policies.Staff)]
    public Task<AttendanceToday> Today(CancellationToken ct) => svc.TodayAsync(Me.Id, ct);

    [HttpPost("check-in"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> CheckIn(CancellationToken ct) => Ok(new { at = await svc.CheckInAsync(Me, ct) });

    [HttpPost("check-out"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> CheckOut(CancellationToken ct) { await svc.CheckOutAsync(Me, ct); return NoContent(); }

    [HttpPost("decline"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Decline(CancellationToken ct) { await svc.DeclineAsync(Me, ct); return NoContent(); }

    [HttpGet("events"), Authorize(Policy = Policies.Admin)]
    public Task<PagedResult<AttendanceEventDto>> Events([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] PageRequest page, CancellationToken ct) => svc.EventsAsync(from, to, page, ct);

    [HttpGet("daily"), Authorize(Policy = Policies.Admin)]
    public Task<List<AttendanceDayDto>> Daily([FromQuery] DateOnly from, [FromQuery] DateOnly to, CancellationToken ct) => svc.DailySummaryAsync(from, to, ct);
}

// ------------------------------------------------------------------ employees, payroll, loans (Admin)
public sealed record GenerateMonthRequest(int Year, int Month);
public sealed record LoanRequest(decimal Amount, string? Note);
public sealed record ActiveRequest(bool Active);

[ApiController, Route("api/employees"), Authorize(Policy = Policies.Admin)]
public class EmployeesController(ICurrentUser cu, PayrollService svc) : AppController(cu)
{
    [HttpGet]
    public Task<List<EmployeeDto>> List(CancellationToken ct) => svc.EmployeesAsync(ct);

    [HttpPost]
    public async Task<IActionResult> Create(EmployeeInput i, CancellationToken ct) => Ok(new { id = await svc.CreateEmployeeAsync(i, Me, ct) });

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, EmployeeInput i, CancellationToken ct) { await svc.UpdateEmployeeAsync(id, i, Me, ct); return NoContent(); }

    [HttpPut("{id:int}/active")]
    public async Task<IActionResult> SetActive(int id, ActiveRequest r, CancellationToken ct) { await svc.SetActiveAsync(id, r.Active, Me, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteEmployeeAsync(id, Me, ct); return NoContent(); }

    [HttpGet("{id:int}/payroll")]
    public Task<List<PayrollRowDto>> History(int id, CancellationToken ct) => svc.HistoryAsync(id, ct);

    [HttpGet("{id:int}/loans")]
    public Task<List<LoanDto>> Loans(int id, CancellationToken ct) => svc.LoansAsync(id, ct);

    [HttpPost("{id:int}/loans")]
    public async Task<IActionResult> AddLoan(int id, LoanRequest r, CancellationToken ct) => Ok(new { id = await svc.AddLoanAsync(id, r.Amount, r.Note, Me, ct) });
}

[ApiController, Route("api/payroll"), Authorize(Policy = Policies.Admin)]
public class PayrollController(ICurrentUser cu, PayrollService svc) : AppController(cu)
{
    [HttpGet("{year:int}/{month:int}")]
    public Task<List<PayrollRowDto>> Month(int year, int month, CancellationToken ct) => svc.MonthAsync(year, month, ct);

    [HttpPost("generate")]
    public async Task<IActionResult> Generate(GenerateMonthRequest r, CancellationToken ct) => Ok(new { added = await svc.GenerateMonthAsync(r.Year, r.Month, Me, ct) });

    [HttpPut("rows/{id:int}")]
    public async Task<IActionResult> Edit(int id, PayrollEditInput i, CancellationToken ct) { await svc.EditRowAsync(id, i, Me, ct); return NoContent(); }

    [HttpDelete("rows/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteRowAsync(id, Me, ct); return NoContent(); }

    [HttpPost("rows/{id:int}/pay")]
    public async Task<IActionResult> Pay(int id, CancellationToken ct) => Ok(await svc.MarkPaidAsync(id, Me, ct));

    [HttpPost("{year:int}/{month:int}/pay-all")]
    public async Task<IActionResult> PayAll(int year, int month, CancellationToken ct) => Ok(new { paid = await svc.MarkAllPaidAsync(year, month, Me, ct) });

    [HttpPost("loans/{loanId:int}/repay")]
    public async Task<IActionResult> Repay(int loanId, LoanRequest r, CancellationToken ct) { await svc.RepayLoanAsync(loanId, r.Amount, r.Note, Me, ct); return NoContent(); }

    [HttpDelete("loans/{loanId:int}")]
    public async Task<IActionResult> DeleteLoan(int loanId, CancellationToken ct) { await svc.DeleteLoanAsync(loanId, Me, ct); return NoContent(); }
}

// ------------------------------------------------------------------ serial numbers
public sealed record ReceiveSerialsRequest(int WarehouseId, string? Serials, string? Notes);
public sealed record TakeBackRequest(string Serial, int WarehouseId, string? Reason);
public sealed record WriteOffRequest(string Serial, string Reason);

[ApiController, Route("api/serials")]
public class SerialsController(ICurrentUser cu, SerialService svc) : AppController(cu)
{
    [HttpGet("product/{productId:int}"), Authorize(Policy = Policies.Staff)]
    public Task<List<SerialDto>> List(int productId, [FromQuery] string? status, [FromQuery] int? warehouseId, [FromQuery] string? search, CancellationToken ct) => svc.ListAsync(productId, status, warehouseId, search, ct);

    [HttpPost("product/{productId:int}/receive"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Receive(int productId, ReceiveSerialsRequest r, CancellationToken ct)
    {
        var list = SerialService.Split(r.Serials);
        await svc.ReceiveAsync(productId, r.WarehouseId, list, r.Notes, Me, ct);
        return Ok(new { received = list.Count });
    }

    [HttpPost("product/{productId:int}/take-back"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> TakeBack(int productId, TakeBackRequest r, CancellationToken ct) { await svc.TakeBackAsync(productId, r.Serial.Trim(), r.WarehouseId, r.Reason, Me, ct); return NoContent(); }

    [HttpPost("product/{productId:int}/write-off"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> WriteOff(int productId, WriteOffRequest r, CancellationToken ct) { await svc.WriteOffAsync(productId, r.Serial.Trim(), r.Reason, Me, ct); return NoContent(); }

    [HttpGet("discrepancies"), Authorize(Policy = Policies.Admin)]
    public Task<List<SerialDiscrepancyDto>> Discrepancies(CancellationToken ct) => svc.DiscrepanciesAsync(ct);
}

// ------------------------------------------------------------------ expenses & rebates (Admin)
public sealed record RateRequest(decimal Pct);

[ApiController, Route("api/expenses"), Authorize(Policy = Policies.Admin)]
public class ExpensesController(ICurrentUser cu, ExpenseService svc) : AppController(cu)
{
    [HttpGet("categories")]
    public string[] Categories() => ExpenseCategories.All;

    [HttpGet]
    public Task<ExpenseListDto> List([FromQuery] DateOnly from, [FromQuery] DateOnly to, [FromQuery] string? category, [FromQuery] PageRequest page, CancellationToken ct) => svc.ListAsync(from, to, category, page, ct);

    [HttpPost]
    public async Task<IActionResult> Create(ExpenseInput i, CancellationToken ct) => Ok(new { id = await svc.CreateAsync(i, Me, ct) });

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, ExpenseInput i, CancellationToken ct) { await svc.UpdateAsync(id, i, Me, ct); return NoContent(); }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteAsync(id, Me, ct); return NoContent(); }
}

[ApiController, Route("api/rebates"), Authorize(Policy = Policies.Admin)]
public class RebatesController(ICurrentUser cu, RebateService svc) : AppController(cu)
{
    [HttpGet]
    public Task<RebateSummaryDto> Summary([FromQuery] string? search, CancellationToken ct) => svc.SummaryAsync(search, ct);

    [HttpGet("customer/{customerId:int}")]
    public Task<List<RebateEntryDto>> Entries(int customerId, CancellationToken ct) => svc.EntriesAsync(customerId, ct);

    [HttpPost("customer/{customerId:int}/redeem")]
    public async Task<IActionResult> Redeem(int customerId, CancellationToken ct) => Ok(await svc.RedeemAsync(customerId, Me, ct));

    [HttpPut("default-rate")]
    public async Task<IActionResult> SetRate(RateRequest r, CancellationToken ct) { await svc.SetDefaultRateAsync(r.Pct, Me, ct); return NoContent(); }
}

// ------------------------------------------------------------------ price book (Candid)
public sealed record ApplyPricesRequest(List<PriceUpdateInput> Updates, string? Note);
public sealed record AddPriceProductsRequest(List<NewPriceProduct> Products, string? Note);

[ApiController, Route("api/price-book")]
public class PriceBookController(ICurrentUser cu, PriceBookService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<PriceBookDto> Current([FromQuery] PageRequest page, [FromQuery] int? categoryId, CancellationToken ct) => svc.CurrentAsync(page, categoryId, ct);

    [HttpGet("history"), Authorize(Policy = Policies.Admin)]
    public Task<PagedResult<PriceHistoryDto>> History([FromQuery] PageRequest page, [FromQuery] int? productId, CancellationToken ct) => svc.HistoryAsync(page, productId, ct);

    [HttpPost("apply"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Apply(ApplyPricesRequest r, CancellationToken ct) => Ok(new { changed = await svc.ApplyAsync(r.Updates, r.Note, Me, ct) });

    [HttpPost("products"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> AddProducts(AddPriceProductsRequest r, CancellationToken ct) => Ok(new { ids = await svc.AddProductsAsync(r.Products, r.Note, Me, ct) });
}

// ------------------------------------------------------------------ customer metrics & exports
[ApiController, Route("api/customers")]
public class CustomerMetricsController(ICurrentUser cu, CustomerMetricsService svc) : AppController(cu)
{
    [HttpGet("{id:int}/metrics"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<CustomerMetricsDto>> Get(int id, CancellationToken ct) => await svc.GetAsync(id, ct) is { } m ? m : NotFound();
}

[ApiController, Route("api/exports")]
public class ExportsController(ICurrentUser cu, ExcelExporter x, IClock clock, ICompanyContext company) : AppController(cu)
{
    private IActionResult Xlsx(byte[] bytes, string name)
    {
        Response.Headers.CacheControl = "no-store";
        return File(bytes, ExcelExporter.ContentType, $"{company.Key}-{name}-{clock.BusinessToday:yyyy-MM-dd}.xlsx");
    }

    private (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to)
    {
        var t = to ?? clock.BusinessToday;
        var f = from ?? new DateOnly(t.Year, t.Month, 1);
        if (f > t) throw new BusinessRuleException("The start date is after the end date.");
        if (t.DayNumber - f.DayNumber > 1830) throw new BusinessRuleException("Choose a period of five years or less.");
        return (f, t);
    }

    [HttpGet("inventory"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Inventory(CancellationToken ct) => Xlsx(await x.InventoryAsync(IsAdmin, ct), "inventory");

    [HttpGet("invoices"), Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Invoices([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) { var r = Range(from, to); return Xlsx(await x.InvoicesAsync(r.From, r.To, ct), "invoices"); }

    [HttpGet("customers"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Customers(CancellationToken ct) => Xlsx(await x.CustomersAsync(ct), "customers");

    [HttpGet("expenses"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Expenses([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) { var r = Range(from, to); return Xlsx(await x.ExpensesAsync(r.From, r.To, ct), "expenses"); }

    [HttpGet("ledger"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Ledger([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) { var r = Range(from, to); return Xlsx(await x.LedgerAsync(r.From, r.To, ct), "ledger"); }

    [HttpGet("report"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Report([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) { var r = Range(from, to); return Xlsx(await x.ReportAsync(r.From, r.To, ct), "report"); }

    [HttpGet("full"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Full([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) { var r = Range(from, to); return Xlsx(await x.FullAsync(r.From, r.To, ct), "full-export"); }
}
