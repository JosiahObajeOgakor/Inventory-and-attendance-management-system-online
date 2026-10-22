namespace Inventory.Domain.Entities;

/// <summary>
/// A branch (shop, depot, office). Warehouses belong to one; staff can be tied to a branch so they only sell and move stock from its warehouses.
/// </summary>
public class Branch
{
    public int Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Address { get; set; }
    public string? State { get; set; }
    public string? Phone { get; set; }
    /// <summary>Exactly one: where anything not tied to a branch belongs.</summary>
    public bool IsHeadOffice { get; set; }
    public bool Active { get; set; } = true;
}
