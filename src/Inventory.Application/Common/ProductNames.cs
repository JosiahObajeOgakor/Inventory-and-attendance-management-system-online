namespace Inventory.Application.Common;

/// <summary>One product, one name: names are compared with extra spaces removed and capitals ignored, so "Adult Dog Food 20kg" and "adult  dog food 20KG" are the same product.</summary>
public static class ProductNames
{
    /// <summary>Trims and collapses runs of spaces; this is the form stored.</summary>
    public static string Clean(string? name) => string.Join(' ', (name ?? "").Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The form two names are compared in.</summary>
    public static string Key(string? name) => Clean(name).ToUpperInvariant();
}
