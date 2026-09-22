namespace Inventory.Application.Common;

/// <summary>A rule was broken; the message is safe to show the user (mapped to HTTP 400/409/404).</summary>
public class BusinessRuleException(string message) : Exception(message);

public sealed class NotFoundException(string what) : BusinessRuleException($"{what} was not found.");

public sealed class ForbiddenActionException(string message) : BusinessRuleException(message);

public sealed record Shortfall(int ProductId, string ProductName, int Requested, int Available, string Advice)
{
    public int ShortBy => Math.Max(0, Requested - Available);
}

/// <summary>Raised instead of saving a sale that would take more off the shelf than is on it (rule S4).</summary>
public sealed class InsufficientStockException(IReadOnlyList<Shortfall> shortfalls)
    : BusinessRuleException(BuildMessage(shortfalls))
{
    public IReadOnlyList<Shortfall> Shortfalls { get; } = shortfalls;

    private static string BuildMessage(IReadOnlyList<Shortfall> items)
    {
        var lines = items.Select(i =>
            $"• {i.ProductName} — asked for {i.Requested}, only {i.Available} in stock (short {i.ShortBy})." +
            (i.Advice.Length > 0 ? $"\n   Restock: {i.Advice}" : ""));
        return "Not enough stock to save this sale:\n\n" + string.Join("\n", lines) +
               "\n\nReduce the quantities, or record the purchase/production that brings the stock in first.";
    }
}
