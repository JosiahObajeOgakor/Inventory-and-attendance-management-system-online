using FluentValidation;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.Company;

public sealed record BankDto(string BankName, string AccountName, string AccountNumber);
public sealed record CompanyProfileDto(string LegalName, string Address, string Phone, string Email, string TaxId, decimal DefaultVatRate,
    decimal DefaultRebateRatePct, IReadOnlyList<BankDto> Banks, IReadOnlyList<string> Assets, bool HasPriceLists, bool BuysGoods);
public sealed record CompanyProfileInput(string LegalName, string Address, string Phone, string Email, string TaxId, decimal DefaultVatRate,
    decimal DefaultRebateRatePct, List<BankDto> Banks);
public sealed record AssetFile(string ContentType, byte[] Data);

public sealed class CompanyProfileValidator : AbstractValidator<CompanyProfileInput>
{
    public CompanyProfileValidator()
    {
        RuleFor(x => x.LegalName).NotEmpty().MaximumLength(150);
        RuleFor(x => x.Address).MaximumLength(250);
        RuleFor(x => x.Phone).MaximumLength(60);
        RuleFor(x => x.Email).MaximumLength(100);
        RuleFor(x => x.TaxId).MaximumLength(60);
        RuleFor(x => x.DefaultVatRate).InclusiveBetween(0, 100);
        RuleFor(x => x.DefaultRebateRatePct).InclusiveBetween(0, 100);
        RuleFor(x => x.Banks).Must(b => b.Count <= 3).WithMessage("At most three bank accounts are printed on documents.");
        RuleForEach(x => x.Banks).ChildRules(b =>
        {
            b.RuleFor(x => x.BankName).NotEmpty().MaximumLength(80);
            b.RuleFor(x => x.AccountName).NotEmpty().MaximumLength(150);
            b.RuleFor(x => x.AccountNumber).NotEmpty().MaximumLength(40);
        });
    }
}

/// <summary>Address, tax id, bank accounts and artwork that appear on every printed document (replaces App.config).</summary>
public sealed class CompanyProfileService(IBusinessDbContext db, ICompanyContext company, IClock clock)
{
    public const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>The profile, created on first use from configuration (legal name and the bank accounts the desktop app already had).</summary>
    public async Task<CompanyProfile> EnsureAsync(CancellationToken ct)
    {
        var p = await db.CompanyProfiles.Include(x => x.Banks).SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (p is not null) return p;
        p = new CompanyProfile { Id = 1, LegalName = company.LegalName };
        var i = 0;
        foreach (var b in company.DefaultBanks) p.Banks.Add(new CompanyBank { SortOrder = i++, BankName = b.Bank, AccountName = b.AccountName, AccountNumber = b.AccountNumber });
        db.CompanyProfiles.Add(p);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { db.ClearTracker(); p = await db.CompanyProfiles.Include(x => x.Banks).SingleAsync(x => x.Id == 1, ct); }   // a parallel first request won
        return p;
    }

    public async Task<CompanyProfileDto> GetAsync(CancellationToken ct)
    {
        var p = await EnsureAsync(ct);
        var kinds = await db.CompanyAssets.AsNoTracking().Select(a => a.Kind).ToListAsync(ct);
        return new CompanyProfileDto(p.LegalName, p.Address, p.Phone, p.Email, p.TaxId, p.DefaultVatRate, p.DefaultRebateRatePct,
            p.Banks.OrderBy(b => b.SortOrder).Select(b => new BankDto(b.BankName, b.AccountName, b.AccountNumber)).ToList(), kinds,
            company.HasPriceLists, company.BuysGoods);
    }

    public async Task UpdateAsync(CompanyProfileInput input, CurrentUser user, CancellationToken ct)
    {
        await new CompanyProfileValidator().ValidateAndThrowAsync(input, ct);
        var p = await EnsureAsync(ct);
        p.LegalName = input.LegalName.Trim(); p.Address = input.Address.Trim(); p.Phone = input.Phone.Trim(); p.Email = input.Email.Trim(); p.TaxId = input.TaxId.Trim();
        p.DefaultVatRate = input.DefaultVatRate; p.DefaultRebateRatePct = input.DefaultRebateRatePct;
        db.CompanyBanks.RemoveRange(p.Banks);
        p.Banks = input.Banks.Select((b, i) => new CompanyBank { SortOrder = i, BankName = b.BankName.Trim(), AccountName = b.AccountName.Trim(), AccountNumber = b.AccountNumber.Trim() }).ToList();
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "COMPANY_PROFILE_UPDATED", Entity = "CompanyProfile", EntityId = "1", At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Stores a PNG or JPEG. The bytes are checked, not just the declared type: a renamed file is refused.</summary>
    public async Task SetAssetAsync(string kind, byte[] data, CurrentUser user, CancellationToken ct)
    {
        if (!AssetKinds.All.Contains(kind)) throw new BusinessRuleException("Unknown image type.");
        if (data.Length == 0) throw new BusinessRuleException("Choose an image file.");
        if (data.Length > MaxImageBytes) throw new BusinessRuleException("That image is larger than 2 MB. Use a smaller file.");
        var type = Sniff(data) ?? throw new BusinessRuleException("Only PNG or JPEG images can be used.");
        var a = await db.CompanyAssets.SingleOrDefaultAsync(x => x.Kind == kind, ct);
        if (a is null) db.CompanyAssets.Add(a = new CompanyAsset { Kind = kind });
        a.ContentType = type; a.Data = data; a.UpdatedAt = clock.UtcNow;
        db.AuditLogs.Add(new AuditLog { UserId = user.Id, UserName = user.FullName, Action = "COMPANY_IMAGE_SET", Entity = "CompanyAsset", EntityId = kind, At = clock.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    public async Task<AssetFile?> GetAssetAsync(string kind, CancellationToken ct)
    {
        var a = await db.CompanyAssets.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == kind, ct);
        return a is null ? null : new AssetFile(a.ContentType, a.Data);
    }

    public async Task RemoveAssetAsync(string kind, CancellationToken ct)
    {
        var a = await db.CompanyAssets.SingleOrDefaultAsync(x => x.Kind == kind, ct);
        if (a is null) return;
        db.CompanyAssets.Remove(a);
        await db.SaveChangesAsync(ct);
    }

    private static string? Sniff(byte[] d)
    {
        if (d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        return null;
    }
}
