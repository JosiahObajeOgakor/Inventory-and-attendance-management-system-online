namespace Inventory.Application.Payments;

/// <summary>Who is told once an online order is paid. (Delivery zones and fees are managed in the app — see DeliveryZoneService.)</summary>
public sealed class FulfilmentOptions
{
    public const string Section = "Fulfilment";

    /// <summary>The owner's WhatsApp number (any format) — told about every paid online order.</summary>
    public string AdminWhatsApp { get; set; } = "";
    /// <summary>The single dispatch rider every paid order goes to.</summary>
    public string RiderName { get; set; } = "";
    public string RiderPhone { get; set; } = "";

    /// <summary>Meta-approved template names. They must exist, approved, on the WhatsApp Business account.</summary>
    public string AdminTemplate { get; set; } = "order_paid_admin";
    public string RiderTemplate { get; set; } = "dispatch_new_order";
    public string TemplateLanguage { get; set; } = "en";
}
