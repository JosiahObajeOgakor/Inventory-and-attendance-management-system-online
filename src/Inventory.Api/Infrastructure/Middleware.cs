using FluentValidation;
using Inventory.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Inventory.Api.Infrastructure;

public static class Policies
{
    /// <summary>CEO or manager: management screens, adding and editing.</summary>
    public const string Admin = "Admin";
    /// <summary>The CEO only: deleting, clearing history, people and roles.</summary>
    public const string Ceo = "Ceo";
    public const string Staff = "Staff";
    /// <summary>CEO, manager or digital marketer: Campaigns, the Marketing apps and the Website.</summary>
    public const string Marketing = "Marketing";
    public const string AuthRateLimit = "auth";
    public const string WhatsAppRateLimit = "whatsapp";
    public const string WebChatRateLimit = "webchat";
    /// <summary>Trade applications carry documents: a handful per address per quarter hour is plenty for a real shop and stops a form-filling bot.</summary>
    public const string ApplyRateLimit = "apply";
}

/// <summary>
/// Defence in depth on top of SameSite=Strict cookies: every state-changing API call must carry a custom header,
/// which a cross-site form or image request cannot add.
/// </summary>
public sealed class CsrfHeaderMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Requested-With";
    public const string HeaderValue = "inventory-ui";

    public Task InvokeAsync(HttpContext ctx)
    {
        var m = ctx.Request.Method;
        var unsafeMethod = !(HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m));
        // These webhooks are called by Paystack's/AlatPay's/Meta's own servers, which cannot send our header. All are protected by an HMAC signature instead
        // (Paystack/AlatPay: also asking the processor to confirm the payment; Meta: the signature alone, since there is nothing else to confirm an inbound message against).
        var webhook = ctx.Request.Path.Equals("/api/paystack/webhook", StringComparison.OrdinalIgnoreCase)
                   || ctx.Request.Path.Equals("/api/whatsapp/webhook", StringComparison.OrdinalIgnoreCase)
                   // Twilio (HMAC-SHA1 over URL+fields with the auth token) and AlatPay (HMAC-SHA256 over the body, then confirmed with AlatPay itself; one URL per company).
                   || ctx.Request.Path.Equals("/api/twilio/whatsapp", StringComparison.OrdinalIgnoreCase)
                   || ctx.Request.Path.StartsWithSegments("/api/alatpay/webhook", StringComparison.OrdinalIgnoreCase)
                   // One-click unsubscribe (RFC 8058) is POSTed by the recipient's mail app, which cannot send our header; the signed link is the proof.
                   || ctx.Request.Path.Equals("/api/marketing/unsubscribe", StringComparison.OrdinalIgnoreCase);
        // The public site (/api/site/{company}/...): anonymous HTML forms (register for an event, post to the forum, sign a document, answer a survey). They carry no
        // cookie-based sign-in, so there is nothing for a cross-site request to ride on, and an HTML form can't add a custom header.
        webhook |= ctx.Request.Path.StartsWithSegments("/api/site", StringComparison.OrdinalIgnoreCase);
        if (unsafeMethod && !webhook && ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Headers[HeaderName] != HeaderValue)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 400, Title = "Missing required header." });
        }
        return next(ctx);
    }
}

/// <summary>An account flagged MustChangePassword can do nothing except change its password, read /me, or sign out.</summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next)
{
    public const string ClaimType = "must_change_password";
    private static readonly string[] Allowed = ["/api/auth/change-password", "/api/auth/me", "/api/auth/logout"];

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && ctx.User.HasClaim(ClaimType, "true")
            && !Allowed.Any(a => ctx.Request.Path.StartsWithSegments(a)))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "You must change your password before continuing." });
        }
        return next(ctx);
    }
}

/// <summary>Central exception → HTTP mapping. Users get safe messages; internals are logged, never returned.</summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        ProblemDetails problem;
        switch (ex)
        {
            case ValidationException v:
                var errors = v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
                problem = new ValidationProblemDetails(errors) { Status = 400, Title = "The request is not valid." };
                break;
            case NotFoundException n:
                problem = new ProblemDetails { Status = 404, Title = n.Message };
                break;
            case InsufficientStockException s:
                problem = new ProblemDetails { Status = 409, Title = "Not enough stock.", Detail = s.Message };
                problem.Extensions["shortfalls"] = s.Shortfalls;
                break;
            case ForbiddenActionException f:
                problem = new ProblemDetails { Status = 403, Title = f.Message };
                break;
            case BusinessRuleException b:
                problem = new ProblemDetails { Status = 409, Title = b.Message };
                break;
            default:
                log.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
                problem = new ProblemDetails { Status = 500, Title = "Something went wrong. Please try again." };
                break;
        }
        ctx.Response.StatusCode = problem.Status!.Value;
        await ctx.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
