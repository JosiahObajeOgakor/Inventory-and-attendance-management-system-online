using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Website;

public sealed record ReviewInput(string Name, string? Place, int Rating, string Text, bool ConsentToPublish);
public sealed record ReviewDto(int Id, string Name, string? Place, int Rating, string Text, bool ConsentToPublish, string Status, DateTime CreatedAt, string? DecidedBy);
/// <summary>What the website shows: first name only, never a surname, phone or email.</summary>
public sealed record PublicReview(string Name, string? Place, int Rating, string Text, DateOnly On);
public sealed record PartnerDto(int Id, string Name, string? Url, bool PermissionGiven, bool IsActive, int SortOrder);
public sealed record PublicPartner(int Id, string Name, string? Url);
public sealed record PartnerInput(string Name, string? Url, bool PermissionGiven, bool IsActive, int SortOrder);

/// <summary>Things that happen on the public website, counted for the Campaigns insights. Nothing personal is stored with them.</summary>
public static class SiteEvents
{
    public const string Channel = "website";
    public const string View = "site_view", BasketStart = "basket_start", BasketSent = "basket_whatsapp", Checkout = "checkout_start", Paid = "checkout_paid";
    public static readonly string[] Public = [View, BasketStart, BasketSent, Checkout];
}

/// <summary>Website content that staff manage and visitors add to: reviews (moderated), partner logos (with permission), and visit counts.</summary>
public sealed class WebsiteService(IBusinessDbContext db, IClock clock)
{
    // ------------------------------------------------------------------ reviews

    public async Task SubmitReviewAsync(ReviewInput r, CancellationToken ct)
    {
        var name = (r.Name ?? "").Trim(); var text = (r.Text ?? "").Trim();
        if (name.Length is < 2 or > 60) throw new BusinessRuleException("Please give your first name.");
        if (text.Length is < 10 or > 1000) throw new BusinessRuleException("Please write between 10 and 1,000 characters.");
        if (r.Rating is < 1 or > 5) throw new BusinessRuleException("Choose a rating from 1 to 5 stars.");
        if (!r.ConsentToPublish) throw new BusinessRuleException("Please tick the box to let us publish your review.");
        // the same words twice in a day is a double tap or a bot
        var since = clock.UtcNow.AddDays(-1);
        if (await db.SiteReviews.AnyAsync(x => x.Text == text && x.CreatedAt > since, ct)) return;
        db.SiteReviews.Add(new SiteReview { Name = name, Place = string.IsNullOrWhiteSpace(r.Place) ? null : r.Place.Trim()[..Math.Min(60, r.Place.Trim().Length)], Rating = r.Rating, Text = text, ConsentToPublish = true, CreatedAt = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<ReviewDto>> ReviewsAsync(string? status, CancellationToken ct) =>
        await db.SiteReviews.AsNoTracking().Where(x => status == null || x.Status == status).OrderByDescending(x => x.Id).Take(500)
            .Select(x => new ReviewDto(x.Id, x.Name, x.Place, x.Rating, x.Text, x.ConsentToPublish, x.Status, x.CreatedAt, x.DecidedBy)).ToListAsync(ct);

    public async Task DecideReviewAsync(int id, bool approve, CurrentUser user, CancellationToken ct)
    {
        var r = await db.SiteReviews.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Review");
        r.Status = approve ? SiteReviewStatuses.Approved : SiteReviewStatuses.Rejected; r.DecidedAt = clock.UtcNow; r.DecidedBy = user.FullName;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = approve ? "REVIEW_APPROVED" : "REVIEW_REJECTED", Entity = "SiteReview", EntityId = id.ToString(), At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<PublicReview>> PublicReviewsAsync(int take, CancellationToken ct) =>
        (await db.SiteReviews.AsNoTracking().Where(x => x.Status == SiteReviewStatuses.Approved && x.ConsentToPublish).OrderByDescending(x => x.Id).Take(Math.Clamp(take, 1, 50))
            .Select(x => new { x.Name, x.Place, x.Rating, x.Text, x.CreatedAt }).ToListAsync(ct))
        .Select(x => new PublicReview(x.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0], x.Place, x.Rating, x.Text, DateOnly.FromDateTime(x.CreatedAt.AddHours(1)))).ToList();

    // ------------------------------------------------------------------ partners

    public async Task<List<PartnerDto>> PartnersAsync(CancellationToken ct) =>
        await db.SitePartners.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PartnerDto(p.Id, p.Name, p.Url, p.PermissionGiven, p.IsActive, p.SortOrder)).ToListAsync(ct);

    public async Task<List<PublicPartner>> PublicPartnersAsync(CancellationToken ct) =>
        await db.SitePartners.AsNoTracking().Where(p => p.IsActive && p.PermissionGiven).OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PublicPartner(p.Id, p.Name, p.Url)).ToListAsync(ct);

    public async Task<(string ContentType, byte[] Data)?> PartnerLogoAsync(int id, bool publicOnly, CancellationToken ct) =>
        await db.SitePartners.AsNoTracking().Where(p => p.Id == id && (!publicOnly || (p.IsActive && p.PermissionGiven))).Select(p => new { p.ContentType, p.Logo }).FirstOrDefaultAsync(ct) is { } x
            ? (x.ContentType, x.Logo) : null;

    public async Task<int> SavePartnerAsync(int? id, PartnerInput input, byte[]? logo, CurrentUser user, CancellationToken ct)
    {
        var name = (input.Name ?? "").Trim();
        if (name.Length is < 2 or > 100) throw new BusinessRuleException("Give the partner's name (2 to 100 characters).");
        var url = string.IsNullOrWhiteSpace(input.Url) ? null : input.Url.Trim();
        if (url is not null && !(Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")) throw new BusinessRuleException("The website must start with https://");
        var p = id is int i ? await db.SitePartners.FirstOrDefaultAsync(x => x.Id == i, ct) ?? throw new NotFoundException("Partner") : null;
        if (p is null)
        {
            if (logo is null) throw new BusinessRuleException("Choose the partner's logo (PNG or JPEG).");
            p = new SitePartner(); db.SitePartners.Add(p);
        }
        if (logo is not null)
        {
            p.ContentType = ImageType(logo) ?? throw new BusinessRuleException("The logo must be a PNG or JPEG image.");
            if (logo.Length > 1_000_000) throw new BusinessRuleException("The logo must be under 1 MB.");
            p.Logo = logo;
        }
        p.Name = name; p.Url = url; p.PermissionGiven = input.PermissionGiven; p.IsActive = input.IsActive; p.SortOrder = input.SortOrder; p.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PARTNER_SAVED", Entity = "SitePartner", EntityId = p.Id == 0 ? null : p.Id.ToString(), At = clock.UtcNow, Detail = name });
        await db.SaveChangesAsync(ct);
        return p.Id;
    }

    public async Task DeletePartnerAsync(int id, CurrentUser user, CancellationToken ct)
    {
        var p = await db.SitePartners.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Partner");
        db.SitePartners.Remove(p);
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "PARTNER_REMOVED", Entity = "SitePartner", EntityId = id.ToString(), At = clock.UtcNow, Detail = p.Name });
        await db.SaveChangesAsync(ct);
    }

    private static string? ImageType(byte[] b) =>
        b.Length > 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47 ? "image/png"
        : b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF ? "image/jpeg" : null;

    // ------------------------------------------------------------------ visits

    public async Task TrackAsync(string kind, decimal? amount, CancellationToken ct)
    {
        if (!SiteEvents.Public.Contains(kind)) return;
        db.ChatEvents.Add(new ChatEvent { At = clock.UtcNow, Channel = SiteEvents.Channel, Kind = kind, Amount = amount is > 0 and < 100_000_000 ? amount : null });
        await db.SaveChangesAsync(ct);
    }
}
