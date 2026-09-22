using Inventory.Api.Infrastructure;
using Inventory.Application.Abstractions;
using Inventory.Application.Purchasing;
using Inventory.Application.Queries;
using Inventory.Application.Sales;
using Inventory.Domain;
using Inventory.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Api.Controllers;

public sealed record VoidRequest(string Reason);
public sealed record PaymentRequest(decimal Amount, string? Method);
public sealed record ReceiveRequest(int WarehouseId);
public sealed record SalePreviewResult(decimal Subtotal, decimal DiscountAmount, decimal VatAmount, decimal Total, decimal PreviousBalance,
    decimal AppliedToInvoice, decimal AppliedToPreviousBalance, decimal Outstanding, string Status);

[ApiController, Route("api/sales")]
public class SalesController(ICurrentUser cu, SalesService sales, SalesQueries q, InvoiceVoidService voids, IBusinessDbContext db) : AppController(cu)
{
    private const string IdempotencyHeader = "Idempotency-Key";

    [HttpGet, Authorize(Policy = Policies.Staff)]
    public Task<PagedResult<InvoiceRowDto>> List([FromQuery] PageRequest page, CancellationToken ct) => q.InvoicesAsync(page, IsAdmin, ct);

    [HttpGet("{id:int}"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<InvoiceDetailDto>> Get(int id, CancellationToken ct) => await q.InvoiceAsync(id, IsAdmin, ct) is { } d ? d : NotFound();

    /// <summary>Receipt scan: plain invoice number, or the "CS|I|number|total" QR payload.</summary>
    [HttpGet("by-number/{code}"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<InvoiceDetailDto>> ByNumber(string code, CancellationToken ct)
    {
        var scan = Barcodes.ReadScan(code);
        if (scan is null || scan.Kind == "Serial") return NotFound();
        return await q.InvoiceByNumberAsync(scan.Code, IsAdmin, ct) is { } d ? d : NotFound();
    }

    /// <summary>Totals and the payment split without saving anything — the sale screen calls this as the user types.</summary>
    [HttpPost("preview"), Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<SalePreviewResult>> Preview(SaleRequest req, CancellationToken ct)
    {
        var totals = DocumentCalculator.Sale(req.Lines.Select(l => new SaleLineInput(l.ProductId, l.Quantity, l.UnitPrice)), req.DiscountPct, req.VatRate);
        var prev = await db.Customers.Where(c => c.Id == req.CustomerId).Select(c => (decimal?)c.Balance).SingleOrDefaultAsync(ct) ?? 0m;
        var split = PaymentWaterfall.ForNewDocument(prev, totals.Total, req.PaidNow);
        return new SalePreviewResult(totals.Subtotal, totals.DiscountAmount, totals.VatAmount, totals.Total, prev, split.AppliedToNew, split.Overflow, split.Outstanding, split.Status);
    }

    /// <summary>Send a unique <c>Idempotency-Key</c> header per submit: a retry or double click returns the same invoice.</summary>
    [HttpPost, Authorize(Policy = Policies.Staff)]
    public async Task<ActionResult<SaleResult>> Create(SaleRequest req, CancellationToken ct)
    {
        var key = Request.Headers[IdempotencyHeader].FirstOrDefault();
        if (key is { Length: > 100 }) return BadRequest(new ProblemDetails { Status = 400, Title = "Idempotency-Key is too long." });
        var result = await sales.SaveAsync(req, Me, key, ct);
        return CreatedAtAction(nameof(Get), new { id = result.InvoiceId }, result);
    }

    /// <summary>Replaces "Delete invoice": reverses stock, balance and rebate, keeps the record (defect D1). Admin only.</summary>
    [HttpPost("{id:int}/void"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Void(int id, VoidRequest r, CancellationToken ct)
    {
        await voids.VoidAsync(id, r.Reason, Me, ct);
        return NoContent();
    }
}

[ApiController, Route("api/customers")]
public class CustomersController(ICurrentUser cu, PartnerQueries q, PartnerService svc, CustomerPaymentService payments) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Admin)]
    public Task<PagedResult<CustomerDto>> List([FromQuery] PageRequest page, CancellationToken ct) => q.CustomersAsync(page, ct);

    /// <summary>Picker for the sale form. Clerks may not open the Customers screen but must be able to choose a customer.</summary>
    [HttpGet("lookup"), Authorize(Policy = Policies.Staff)]
    public Task<List<CustomerLookupDto>> Lookup([FromQuery] string? search, CancellationToken ct) => q.LookupAsync(search, ct);

    /// <summary>Clerks can add a customer while making a sale (as in the desktop app) — decision D10 is open.</summary>
    [HttpPost, Authorize(Policy = Policies.Staff)]
    public async Task<IActionResult> Create(CustomerInput input, CancellationToken ct) => Ok(new { id = await svc.CreateCustomerAsync(input, Me, ct) });

    [HttpPut("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(int id, CustomerInput input, CancellationToken ct) { await svc.UpdateCustomerAsync(id, input, Me, ct); return NoContent(); }

    [HttpDelete("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteCustomerAsync(id, Me, ct); return NoContent(); }

    [HttpPost("{id:int}/payments"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Pay(int id, PaymentRequest r, CancellationToken ct) =>
        Ok(new { applied = await payments.RecordAsync(id, r.Amount, string.IsNullOrWhiteSpace(r.Method) ? "Cash" : r.Method, Me, ct) });
}

[ApiController, Route("api/suppliers")]
public class SuppliersController(ICurrentUser cu, PartnerQueries q, PartnerService svc) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Admin)]
    public Task<PagedResult<SupplierDto>> List([FromQuery] PageRequest page, CancellationToken ct) => q.SuppliersAsync(page, ct);

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Create(SupplierInput input, CancellationToken ct) => Ok(new { id = await svc.CreateSupplierAsync(input, Me, ct) });

    [HttpPut("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Update(int id, SupplierInput input, CancellationToken ct) { await svc.UpdateSupplierAsync(id, input, Me, ct); return NoContent(); }

    [HttpDelete("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) { await svc.DeleteSupplierAsync(id, Me, ct); return NoContent(); }
}

[ApiController, Route("api/purchases")]
public class PurchasesController(ICurrentUser cu, PurchaseService svc, PurchaseQueries q) : AppController(cu)
{
    [HttpGet, Authorize(Policy = Policies.Admin)]
    public Task<PagedResult<PurchaseRowDto>> List([FromQuery] PageRequest page, CancellationToken ct) => q.ListAsync(page, ct);

    [HttpGet("{id:int}"), Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<PurchaseDetailDto>> Get(int id, CancellationToken ct) => await q.GetAsync(id, ct) is { } d ? d : NotFound();

    [HttpPost, Authorize(Policy = Policies.Admin)]
    public async Task<ActionResult<PurchaseResult>> Create(PurchaseRequest req, CancellationToken ct)
    {
        var result = await svc.SaveAsync(req, Me, Request.Headers["Idempotency-Key"].FirstOrDefault(), ct);
        return CreatedAtAction(nameof(Get), new { id = result.PurchaseOrderId }, result);
    }

    /// <summary>Book a Pending order into stock — transactional, logged, and refused the second time (defect D2).</summary>
    [HttpPost("{id:int}/receive"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Receive(int id, ReceiveRequest r, CancellationToken ct) { await svc.ReceiveAsync(id, r.WarehouseId, Me, ct); return NoContent(); }

    [HttpPost("{id:int}/pay"), Authorize(Policy = Policies.Admin)]
    public async Task<IActionResult> Pay(int id, CancellationToken ct) { await svc.PayAsync(id, Me, ct); return NoContent(); }
}
