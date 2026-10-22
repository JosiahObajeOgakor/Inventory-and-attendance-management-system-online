using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Messaging;
using Inventory.Application.Sales;
using Inventory.Application.Stock;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Approvals;

/// <summary>Carries out one kind of approved action, as the approver, through the same service the action normally uses.</summary>
public interface IApprovalHandler
{
    string Action { get; }
    Task ExecuteAsync(string payload, CurrentUser approver, CancellationToken ct);
}

public sealed record AdjustPayload(int BatchId, int Delta, string Reason);
public sealed record VoidPayload(int InvoiceId, string Reason);
public sealed record ApprovalRuleDto(string Action, decimal ThresholdAmount, bool Enabled);
public sealed record ApprovalDto(int Id, string Action, decimal Amount, string Summary, string Status, string RequestedBy, DateTime RequestedAt,
    string? DecidedBy, DateTime? DecidedAt, string? DecisionNote, string? FailureReason);
/// <summary>What the caller gets back when an action was held for approval instead of being carried out.</summary>
public sealed record HeldForApproval(int ApprovalId, string Message);

/// <summary>
/// Separation of duties: above a business-set amount, an action is not performed but held until someone else approves it.
/// The requester can never approve their own request. Approval replays the original request through the normal service, so every stock and money
/// rule still applies at that moment.
/// </summary>
public sealed class ApprovalService(IBusinessDbContext db, TransactionRunner tx, IClock clock, IEnumerable<IApprovalHandler> handlers)
{
    public async Task<bool> NeedsApprovalAsync(string action, decimal amount, CancellationToken ct)
    {
        var rule = await db.ApprovalRules.AsNoTracking().SingleOrDefaultAsync(r => r.Action == action, ct);
        return rule is { Enabled: true } && amount > rule.ThresholdAmount;
    }

    /// <summary>What a stock adjustment is worth: units moved × the product's cost price (a gain or a loss counts the same).</summary>
    public async Task<decimal> AdjustmentValueAsync(int batchId, int delta, CancellationToken ct) =>
        Math.Abs(delta) * await (from b in db.StockBatches join p in db.Products on b.ProductId equals p.Id where b.Id == batchId select p.CostPrice).SingleOrDefaultAsync(ct);

    /// <summary>What voiding an invoice is worth: its total.</summary>
    public Task<decimal> VoidValueAsync(int invoiceId, CancellationToken ct) =>
        db.Invoices.Where(i => i.Id == invoiceId).Select(i => i.TotalAmount).SingleOrDefaultAsync(ct);

    public async Task<HeldForApproval> SubmitAsync<T>(string action, decimal amount, string summary, T payload, CurrentUser user, CancellationToken ct)
    {
        var r = new ApprovalRequest
        {
            Action = action, Amount = amount, Summary = summary, Payload = System.Text.Json.JsonSerializer.Serialize(payload), Status = ApprovalStatuses.Pending,
            RequestedByUserId = user.Id, RequestedByName = user.FullName, RequestedAt = clock.UtcNow,
        };
        db.ApprovalRequests.Add(r);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "APPROVAL_REQUESTED", Entity = "Approval", At = clock.UtcNow, Detail = $"{action} ₦{amount:N2}: {summary}" });
        await db.SaveChangesAsync(ct);
        return new HeldForApproval(r.Id, $"This needs approval (over the ₦ limit set for this action). Request #{r.Id} is waiting for an approver.");
    }

    public async Task<IReadOnlyList<ApprovalDto>> ListAsync(string? status, int? requestedBy, CancellationToken ct)
    {
        var q = db.ApprovalRequests.AsNoTracking();
        if (!string.IsNullOrEmpty(status)) q = q.Where(r => r.Status == status);
        if (requestedBy is int uid) q = q.Where(r => r.RequestedByUserId == uid);
        return await q.OrderByDescending(r => r.Id).Take(200)
            .Select(r => new ApprovalDto(r.Id, r.Action, r.Amount, r.Summary, r.Status, r.RequestedByName, r.RequestedAt, r.DecidedByName, r.DecidedAt, r.DecisionNote, r.FailureReason))
            .ToListAsync(ct);
    }

    /// <summary>Approves (and carries out) or rejects a pending request. Claiming it first means a double click or two approvers can never run it twice.</summary>
    public async Task<ApprovalDto> DecideAsync(int id, bool approve, string? note, CurrentUser approver, CancellationToken ct)
    {
        var claimed = await tx.RunAsync(async inner =>
        {
            var r = await db.ApprovalRequests.FromSqlInterpolated($"SELECT * FROM approval_requests WHERE Id = {id} FOR UPDATE").SingleOrDefaultAsync(inner)
                ?? throw new NotFoundException("Approval request");
            if (r.Status != ApprovalStatuses.Pending) throw new BusinessRuleException($"This request is already {r.Status.ToLowerInvariant()}.");
            if (r.RequestedByUserId == approver.Id) throw new ForbiddenActionException("You can't approve or reject your own request. Another approver must decide.");
            r.Status = approve ? ApprovalStatuses.Executing : ApprovalStatuses.Rejected;
            r.DecidedByUserId = approver.Id; r.DecidedByName = approver.FullName; r.DecidedAt = clock.UtcNow; r.DecisionNote = note?.Trim();
            db.AuditLogs.Add(new AuditLog { UserId = approver.Id, UserName = approver.FullName, Action = approve ? "APPROVAL_GRANTED" : "APPROVAL_REJECTED", Entity = "Approval", EntityId = id.ToString(), At = clock.UtcNow, Detail = $"{r.Action}: {r.Summary}" });
            await db.SaveChangesAsync(inner);
            return (r.Action, r.Payload);
        }, ct);

        if (approve)
        {
            var handler = handlers.SingleOrDefault(h => h.Action == claimed.Action) ?? throw new InvalidOperationException($"No handler for approval action '{claimed.Action}'.");
            string? failure = null;
            try { await handler.ExecuteAsync(claimed.Payload, approver, ct); }
            catch (BusinessRuleException ex) { failure = ex.Message; db.ClearTracker(); }
            await db.ApprovalRequests.Where(r => r.Id == id).ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Status, failure is null ? ApprovalStatuses.Executed : ApprovalStatuses.Failed)
                .SetProperty(r => r.FailureReason, failure), ct);
        }
        return (await ListAsync(null, null, ct)).First(r => r.Id == id);
    }

    public async Task<IReadOnlyList<ApprovalRuleDto>> RulesAsync(CancellationToken ct)
    {
        var rules = await db.ApprovalRules.AsNoTracking().ToListAsync(ct);
        return ApprovalActions.All.Select(a => rules.FirstOrDefault(r => r.Action == a) is { } r
            ? new ApprovalRuleDto(a, r.ThresholdAmount, r.Enabled) : new ApprovalRuleDto(a, 0, false)).ToList();
    }

    public async Task SetRuleAsync(ApprovalRuleDto rule, CurrentUser user, CancellationToken ct)
    {
        if (!ApprovalActions.All.Contains(rule.Action)) throw new BusinessRuleException("Unknown action.");
        if (rule.ThresholdAmount < 0) throw new BusinessRuleException("The limit can't be negative.");
        var r = await db.ApprovalRules.SingleOrDefaultAsync(x => x.Action == rule.Action, ct);
        if (r is null) db.ApprovalRules.Add(r = new ApprovalRule { Action = rule.Action });
        r.ThresholdAmount = rule.ThresholdAmount; r.Enabled = rule.Enabled;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "APPROVAL_RULE_SET", Entity = "ApprovalRule", EntityId = rule.Action, At = clock.UtcNow, Detail = $"{(rule.Enabled ? "on" : "off")}, over ₦{rule.ThresholdAmount:N2}" });
        await db.SaveChangesAsync(ct);
    }
}

public sealed class StockAdjustApproval(StockOperations ops) : IApprovalHandler
{
    public string Action => ApprovalActions.StockAdjust;
    public Task ExecuteAsync(string payload, CurrentUser approver, CancellationToken ct)
    {
        var p = System.Text.Json.JsonSerializer.Deserialize<AdjustPayload>(payload)!;
        return ops.AdjustAsync(p.BatchId, p.Delta, p.Reason, approver, ct);
    }
}

public sealed class SalesVoidApproval(InvoiceVoidService voids) : IApprovalHandler
{
    public string Action => ApprovalActions.SalesVoid;
    public Task ExecuteAsync(string payload, CurrentUser approver, CancellationToken ct)
    {
        var p = System.Text.Json.JsonSerializer.Deserialize<VoidPayload>(payload)!;
        return voids.VoidAsync(p.InvoiceId, p.Reason, approver, ct);
    }
}
