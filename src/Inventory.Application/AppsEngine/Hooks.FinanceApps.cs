using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Inventory.Application.Abstractions;
using Inventory.Application.Common;
using Inventory.Application.Email;
using Inventory.Domain;
using Inventory.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Application.AppsEngine;

/// <summary>Sign: a document is sent as a link; the signer's typed name, the time and their network address are kept with it.</summary>
public sealed class SignHook(IEmailSender email) : IAppHook
{
    public string App => "sign";

    public Task BeforeSaveAsync(HookContext c, AppRecord r, JsonObject data, bool isNew, CancellationToken ct)
    {
        if (!isNew && r.Status != "Draft") throw new BusinessRuleException("Only a draft can be edited. Move it back to Draft first (what was sent stays as it was until you send again).");
        return Task.CompletedTask;
    }

    public async Task<ActionResultDto> ActionAsync(HookContext c, AppRecord r, string action, string? text, CancellationToken ct)
    {
        var d = Dx.Parse(r.Data);
        if (action == "send")
        {
            r.Code ??= Guid.NewGuid().ToString("N");
            r.Status = "Sent"; r.UpdatedAt = c.Clock.UtcNow;
            var link = $"{c.BaseUrl}/sign/{r.Code}";
            var to = d.Str("signerEmail");
            if (!string.IsNullOrWhiteSpace(to) && email.IsConfigured)
            {
                var name = d.Str("signerName") ?? "";
                var html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p>{WebUtility.HtmlEncode(c.CompanyName)} has sent you <b>{WebUtility.HtmlEncode(r.Title)}</b> to read and sign.</p><p><a href=\"{link}\">Open the document</a></p>";
                await email.SendAsync(new EmailMessage(to, name, $"Please sign: {r.Title}", html, $"Hello {name}, {c.CompanyName} has sent you \"{r.Title}\" to sign: {link}", [], c.CompanyName, null), ct);
                return new ActionResultDto($"Sent to {to}. The signing link is also below if you want to share it another way.", link);
            }
            return new ActionResultDto("Marked as sent. Email isn't set up for this signer, so share the link yourself.", link);
        }
        return new ActionResultDto("Anyone with this link can read and sign the document.", $"{c.BaseUrl}/sign/{r.Code}");
    }

    public IReadOnlyList<string> Flags(HookContext c, AppRecord r, JsonObject data) =>
        r.Status == "Sent" && (c.Clock.UtcNow - r.UpdatedAt).TotalDays >= 14 ? ["Waiting for a signature for 2 weeks or more"] : [];
}

/// <summary>ESG: yearly totals by category so the three pillars can be seen at a glance.</summary>
public sealed class EsgHook : IAppHook
{
    public string App => "esg";

    public async Task<IReadOnlyList<Card>> CardsAsync(HookContext c, CancellationToken ct)
    {
        var from = new DateOnly(c.Today.Year, 1, 1);
        var rows = await c.Db.AppRecords.AsNoTracking().Where(r => r.App == "esg" && r.Kind == "metric" && r.Date >= from).Select(r => r.Data).ToListAsync(ct);
        var counts = rows.Select(Dx.Parse).GroupBy(d => d.Str("category") ?? "").ToDictionary(g => g.Key, g => g.Count());
        return new[] { "Environmental", "Social", "Governance" }.Select(k => new Card($"{k} measures this year", counts.GetValueOrDefault(k).ToString(), null, counts.GetValueOrDefault(k) == 0 ? "warn" : "ok")).ToList();
    }
}
