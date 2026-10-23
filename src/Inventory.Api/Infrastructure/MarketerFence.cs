using Inventory.Application.AppsEngine;
using Inventory.Domain;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Infrastructure;

/// <summary>
/// A digital marketer reaches Campaigns, the Marketing apps and the Website, and nothing else, whatever policy an endpoint carries.
/// Deny by default: a new endpoint is closed to marketers until it is added here, so a forgotten [Authorize] can never leak sales, stock or money.
/// </summary>
public sealed class MarketerFence(RequestDelegate next)
{
    private static readonly string[] Open = ["/api/auth/", "/api/campaigns", "/api/website/", "/api/marketing/"];

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.IsInRole(RoleNames.Marketer) && !ctx.User.IsInRole(RoleNames.Admin) && !ctx.User.IsInRole(RoleNames.Manager)
            && ctx.Request.Path.StartsWithSegments("/api") && !Allows(ctx.Request.Method, ctx.Request.Path.Value ?? ""))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Your login is for marketing only.", Detail = "Ask an administrator if you need more." });
            return;
        }
        await next(ctx);
    }

    public static bool Allows(string method, string path)
    {
        var p = path.ToLowerInvariant().TrimEnd('/') + "/";
        if (Open.Any(o => p.StartsWith(o.TrimEnd('/') + "/", StringComparison.Ordinal))) return true;
        if (p == "/api/company/assets/logo/" && HttpMethods.IsGet(method)) return true;
        if (!p.StartsWith("/api/apps/", StringComparison.Ordinal)) return false;
        // the apps endpoint: the menu list, the pickers, and the Marketing apps themselves (approvals are the admin's)
        var app = p["/api/apps/".Length..].Split('/')[0];
        return app is "enabled" or "options" || (AppAccess.Marketing.Contains(app) && !p.Contains("/approve/") && !p.Contains("/reject/"));
    }
}
