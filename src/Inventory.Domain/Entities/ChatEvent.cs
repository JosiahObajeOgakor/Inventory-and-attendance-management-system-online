namespace Inventory.Domain.Entities;

public static class ChatEventKinds
{
    /// <summary>Someone started talking to the assistant (first message of a new conversation).</summary>
    public const string Started = "started";
    /// <summary>An order was placed and a payment account issued.</summary>
    public const string Order = "order";
    /// <summary>A quotation was requested and prepared.</summary>
    public const string Quote = "quote";
    /// <summary>Someone asked for delivery / waybill information.</summary>
    public const string Waybill = "waybill";
    /// <summary>The customer was shown the product list.</summary>
    public const string Browsed = "browsed";
    /// <summary>A product went into the customer's order (Reference = "productId:qty").</summary>
    public const string Selected = "selected";
    /// <summary>The customer was shown the order summary and asked to confirm.</summary>
    public const string Summary = "summary";
    /// <summary>A quotation was refused because this number already had one in the last day.</summary>
    public const string QuoteLimited = "quote_limited";

    /// <summary>The steps of a sale in order, for the drop-off funnel: how far each conversation got.</summary>
    public static readonly string[] Funnel = [Started, Browsed, Selected, Summary, Order, Sale];
    /// <summary>The payment arrived and the sale was completed (stock deducted, invoice made).</summary>
    public const string Sale = "sale";
    /// <summary>The assistant gave up and handed over to a person.</summary>
    public const string Handoff = "handoff";
}

/// <summary>One thing a customer asked the sales assistant for (WhatsApp or website chat). Counted on the dashboard so the owner can see what the channel brings in.</summary>
public class ChatEvent
{
    public long Id { get; set; }
    public DateTime At { get; set; }
    public string Channel { get; set; } = ChatChannels.WhatsApp;
    public string Kind { get; set; } = ChatEventKinds.Started;
    public int? ConversationId { get; set; }
    public int? CustomerId { get; set; }
    /// <summary>The order, quotation or sale value, when there is one.</summary>
    public decimal? Amount { get; set; }
    /// <summary>The quotation or invoice number; for <see cref="ChatEventKinds.Selected"/>, "productId:qty".</summary>
    public string? Reference { get; set; }
}
