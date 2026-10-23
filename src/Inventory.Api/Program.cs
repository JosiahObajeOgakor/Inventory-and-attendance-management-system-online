using System.Net;
using System.Threading.RateLimiting;
using Inventory.Api.Infrastructure;
using Inventory.Application;
using Inventory.Application.Abstractions;
using Inventory.Domain;
using Inventory.Infrastructure.Identity;
using Inventory.Infrastructure.Persistence;
using Inventory.Infrastructure.Services;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// ---- configuration & persistence (secrets come from the environment, never from git) ----
var registry = new CompanyRegistry(builder.Configuration);
builder.Services.AddSingleton(registry);
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<IdentityStore>(o => o.UseMySQL(registry.ConnectionFor(registry.IdentitySchema)));
builder.Services.AddScoped<RequestCompany>();
builder.Services.AddScoped<LedgerSyncFilter>();
builder.Services.AddScoped<ICompanyContext>(sp => sp.GetRequiredService<RequestCompany>());
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<BusinessDbContext>(sp => sp.GetRequiredService<CompanyRegistry>().CreateFor(sp.GetRequiredService<RequestCompany>().Info));
builder.Services.AddScoped<IBusinessDbContext>(sp => sp.GetRequiredService<BusinessDbContext>());
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IDbErrorClassifier, MySqlErrorClassifier>();
builder.Services.AddScoped<DatabaseProvisioner>();
builder.Services.AddSingleton<Inventory.Application.Documents.IDocumentRenderer, Inventory.Infrastructure.Documents.QuestDocumentRenderer>();
builder.Services.AddScoped<Inventory.Infrastructure.Documents.ExcelExporter>();
builder.Services.AddMemoryCache();
builder.Services.Configure<Inventory.Infrastructure.Payments.PaystackOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Payments.PaystackOptions.Section));
builder.Services.Configure<Inventory.Infrastructure.Payments.NotifyOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Payments.NotifyOptions.Section));
builder.Services.Configure<Inventory.Infrastructure.Payments.AlatPayOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Payments.AlatPayOptions.Section));
builder.Services.Configure<Inventory.Application.Payments.FulfilmentOptions>(builder.Configuration.GetSection(Inventory.Application.Payments.FulfilmentOptions.Section));
// Both processors are registered as IPaymentGateway; PaymentGateways picks one by name (the customer's choice, or the link's own provider).
builder.Services.AddHttpClient<Inventory.Infrastructure.Payments.PaystackGateway>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<Inventory.Infrastructure.Payments.AlatPayGateway>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<Inventory.Application.Payments.IPaymentGateway>(sp => sp.GetRequiredService<Inventory.Infrastructure.Payments.PaystackGateway>());
builder.Services.AddScoped<Inventory.Application.Payments.IPaymentGateway>(sp => sp.GetRequiredService<Inventory.Infrastructure.Payments.AlatPayGateway>());
builder.Services.AddHostedService<Inventory.Api.Infrastructure.PendingPaymentReconciler>();
// The public "choose how to pay" page linked from receipts and quotations (Site:PublicUrl, e.g. https://chewypetsfeeds.com).
builder.Services.Configure<Inventory.Api.Controllers.SiteOptions>(builder.Configuration.GetSection(Inventory.Api.Controllers.SiteOptions.Section));
builder.Services.AddScoped<Inventory.Application.Payments.IPayPageLinks, Inventory.Api.Controllers.PayPageLinks>();
// WhatsApp messages are stored by the webhook and answered here, with retries, in the background.
builder.Services.Configure<Inventory.Application.SalesAssistant.InboxOptions>(builder.Configuration.GetSection(Inventory.Application.SalesAssistant.InboxOptions.Section));
builder.Services.AddSingleton<Inventory.Api.Infrastructure.DailyAlertsWorker>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Inventory.Api.Infrastructure.DailyAlertsWorker>());
builder.Services.AddHostedService<Inventory.Api.Infrastructure.WhatsAppInboxWorker>();
builder.Services.AddHttpClient("sms", c => c.Timeout = TimeSpan.FromSeconds(20));
// WhatsApp sales chat: Twilio carries the messages, AlatPay issues the single-use bank account shown inside the chat.
builder.Services.Configure<Inventory.Infrastructure.Payments.AlatPayOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Payments.AlatPayOptions.Section));
builder.Services.AddHttpClient<Inventory.Application.Payments.IVirtualAccountGateway, Inventory.Infrastructure.Payments.AlatPayVirtualAccounts>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.Configure<Inventory.Infrastructure.WhatsApp.TwilioOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.WhatsApp.TwilioOptions.Section));
builder.Services.AddHttpClient<Inventory.Infrastructure.WhatsApp.TwilioWhatsAppClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<Inventory.Application.Messaging.IChatSender>(sp => sp.GetRequiredService<Inventory.Infrastructure.WhatsApp.TwilioWhatsAppClient>());
builder.Services.AddSingleton<Inventory.Api.Infrastructure.ChatStoreRouter>();   // one WhatsApp line sells for every store
Inventory.Application.SalesAssistant.Chat.ChatText.LoadOverrides(builder.Configuration["SalesAssistant:PhrasesFile"]);
// Campaigns: admin-scheduled reminders, offers and restock alerts. Email goes through SendGrid when a key is set, otherwise the existing SMTP mailbox.
builder.Services.Configure<Inventory.Application.Campaigns.CampaignOptions>(builder.Configuration.GetSection(Inventory.Application.Campaigns.CampaignOptions.Section));
// A WhatsApp number mapped to a company name that is only a near-miss ("chewypetsfeeds" for "chewypets") is mapped to the real company instead of being silently ignored.
builder.Services.PostConfigure<Inventory.Infrastructure.WhatsApp.TwilioOptions>(o =>
{
    foreach (var n in o.Numbers)
        if (registry.Find(n.CompanyKey) is null && registry.Resolve(n.CompanyKey) is { } real)
        {
            Console.WriteLine($"[config] Twilio number {n.From}: company \"{n.CompanyKey}\" is not a company key; using \"{real.Key}\". Set Twilio__Numbers__*__CompanyKey={real.Key} to silence this.");
            n.CompanyKey = real.Key;
        }
});
builder.Services.PostConfigure<Inventory.Application.Campaigns.CampaignOptions>(o =>
{
    var cfg = builder.Configuration;
    if (o.AdminPhone.Length == 0) o.AdminPhone = cfg["Notify:AdminPhone"] ?? "";
    if (o.AdminEmail.Length == 0) o.AdminEmail = cfg["Notify:AdminEmail"] ?? "";
    if (o.PublicBaseUrl.Length == 0) o.PublicBaseUrl = cfg["Twilio:PublicBaseUrl"] ?? "";
    if (o.SecretKey.Length == 0) o.SecretKey = cfg["Twilio:AuthToken"] ?? "";
});
builder.Services.Configure<Inventory.Infrastructure.Email.SendGridOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Email.SendGridOptions.Section));
builder.Services.AddHttpClient<Inventory.Application.Messaging.ICampaignEmailSender, Inventory.Infrastructure.Email.CampaignEmailSender>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHostedService<Inventory.Api.Infrastructure.CampaignScheduler>();
builder.Services.AddScoped<Inventory.Application.Payments.IAdminNotifier, Inventory.Infrastructure.Payments.AdminNotifier>();
builder.Services.AddScoped<Inventory.Application.Payments.ISaleAlerts, Inventory.Infrastructure.Payments.SaleAlertService>();
builder.Services.Configure<Inventory.Infrastructure.Localization.LocalizationOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Localization.LocalizationOptions.Section));
builder.Services.AddSingleton<Inventory.Infrastructure.Localization.LanguageCatalogue>();
builder.Services.Configure<Inventory.Infrastructure.Maintenance.ArchiveOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Maintenance.ArchiveOptions.Section));
builder.Services.AddScoped<Inventory.Infrastructure.Maintenance.ArchiveService>();
builder.Services.Configure<Inventory.Application.Ai.AiOptions>(builder.Configuration.GetSection(Inventory.Application.Ai.AiOptions.Section));
builder.Services.Configure<Inventory.Application.SalesAssistant.SalesAssistantOptions>(builder.Configuration.GetSection(Inventory.Application.SalesAssistant.SalesAssistantOptions.Section));
builder.Services.Configure<Inventory.Infrastructure.WhatsApp.WhatsAppOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.WhatsApp.WhatsAppOptions.Section));
// Registered on the concrete type (not just the IWhatsAppSender interface) so WhatsAppController can also reach its
// webhook-only methods (ForPhoneNumberId, IsValidSignature, VerifyToken) that aren't part of the IWhatsAppSender contract.
builder.Services.AddHttpClient<Inventory.Infrastructure.WhatsApp.MetaWhatsAppClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<Inventory.Application.Messaging.IWhatsAppSender>(sp => sp.GetRequiredService<Inventory.Infrastructure.WhatsApp.MetaWhatsAppClient>());
builder.Services.Configure<Inventory.Application.Email.SmtpOptions>(builder.Configuration.GetSection(Inventory.Application.Email.SmtpOptions.Section));
builder.Services.AddHttpClient<Inventory.Application.Ai.IChatModel, Inventory.Infrastructure.Ai.OpenAiChatModel>(c => c.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<Inventory.Application.Ai.IAiUsageStore, Inventory.Infrastructure.Ai.AiUsageStore>();
builder.Services.AddSingleton<Inventory.Application.Email.IEmailSender, Inventory.Infrastructure.Email.SmtpEmailSender>();
builder.Services.AddScoped<Inventory.Application.Queries.IUserDirectory, UserDirectory>();
builder.Services.AddApplication();
builder.Services.AddSingleton(new Inventory.Application.AppsEngine.AppAccess(builder.Configuration["Apps:Enabled"]));
builder.Services.AddSingleton<Inventory.Api.Infrastructure.CompanyScopes>();
builder.Services.AddScoped<Inventory.Infrastructure.Messaging.TermiiSmsSender>();
builder.Services.AddHttpClient<Inventory.Infrastructure.Messaging.TwilioSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<Inventory.Application.Messaging.ISmsSender>(sp => sp.GetRequiredService<Inventory.Infrastructure.Messaging.TwilioSmsSender>());
// Trade partners: CAC check with an outside provider (manual when none is set up) and alerts to staff and partners
builder.Services.Configure<Inventory.Infrastructure.Partners.VerificationOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Partners.VerificationOptions.Section));
builder.Services.AddHttpClient<Inventory.Infrastructure.Partners.DojahVerifier>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<Inventory.Application.Partners.IBusinessVerifier>(sp => sp.GetRequiredService<Inventory.Infrastructure.Partners.DojahVerifier>());
builder.Services.AddScoped<Inventory.Application.Partners.IStaffAlerts, Inventory.Infrastructure.Partners.StaffAlertService>();
builder.Services.AddHostedService<Inventory.Api.Infrastructure.BroadcastWorker>();

// ---- identity: cookie session, legacy-hash aware, lockout 5 tries / 15 min (rule A) ----
builder.Services.AddIdentity<AppUser, IdentityRole<int>>(o =>
    {
        o.Password.RequiredLength = 1;           // real rules live in DesktopPasswordPolicy
        o.Password.RequireDigit = o.Password.RequireLowercase = o.Password.RequireUppercase = o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.Lockout.AllowedForNewUsers = true;
        o.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<IdentityStore>()
    .AddPasswordValidator<DesktopPasswordPolicy>()
    .AddDefaultTokenProviders();   // needed for admin password resets
builder.Services.AddScoped<IPasswordHasher<AppUser>, LegacyAwarePasswordHasher>();
// A role the CEO grants or revokes (e.g. manager delegated to a clerk and taken back) reaches that person's session within a minute.
builder.Services.Configure<SecurityStampValidatorOptions>(o =>
{
    o.ValidationInterval = TimeSpan.FromMinutes(1);
    // The refresh rebuilds the principal from the user store (fresh roles); carry over the claims we add at sign-in.
    o.OnRefreshingPrincipal = ctx =>
    {
        var carried = ctx.CurrentPrincipal?.Claims.Where(c => c.Type is AppClaims.Company or AppClaims.FullName or MustChangePasswordMiddleware.ClaimType).ToList() ?? [];
        if (ctx.NewPrincipal?.Identity is System.Security.Claims.ClaimsIdentity id)
            foreach (var c in carried) if (!id.HasClaim(c.Type, c.Value)) id.AddClaim(new System.Security.Claims.Claim(c.Type, c.Value));
        return Task.CompletedTask;
    };
});
builder.Services.ConfigureApplicationCookie(o =>
{
    o.Cookie.Name = "inventory.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
    o.SlidingExpiration = true;
    // An API answers 401/403, it never redirects to a login page.
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
});

// ---- authorization: deny by default, explicit policies per endpoint ----
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
    .AddPolicy(Policies.Ceo, p => p.RequireRole(RoleNames.Admin))
    .AddPolicy(Policies.Admin, p => p.RequireRole(RoleNames.Admin, RoleNames.Manager))
    .AddPolicy(Policies.Staff, p => p.RequireRole(RoleNames.Admin, RoleNames.Manager, RoleNames.Clerk))
    .AddPolicy(Policies.Marketing, p => p.RequireRole(RoleNames.Admin, RoleNames.Manager, RoleNames.Marketer));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Policies.AuthRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Meta's webhook calls all arrive from its own servers, not the customer's IP, so this only guards against a flood/misconfiguration —
    // the real per-customer limit is SalesAssistantOptions.DailyMessagesPerConversation, enforced inside SalesAssistantService.
    // Storing a message is cheap now (answering happens in the background), so this only guards against floods; each customer is
    // separately capped by SalesAssistant:DailyMessagesPerConversation. Meta retries anything refused here.
    o.AddPolicy(Policies.WhatsAppRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter("whatsapp",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:WhatsAppPerMinute", 1200), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));   // 20 messages a second across all customers; was 2 a second
    o.AddPolicy(Policies.WebChatRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(Policies.ApplyRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
});

builder.Services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// Behind NGINX only: trust forwarded headers from loopback so the real client IP/scheme are seen.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownProxies.Add(IPAddress.Loopback);
    o.KnownProxies.Add(IPAddress.IPv6Loopback);
});

// CORS: the SPA is same-origin behind NGINX, so none is needed by default; allow-list is opt-in from configuration.
var origins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
if (origins.Length > 0)
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowCredentials().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseSerilogRequestLogging();
if (origins.Length > 0) app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<CsrfHeaderMiddleware>();
app.UseMiddleware<MustChangePasswordMiddleware>();
app.UseMiddleware<MarketerFence>();
app.UseAuthorization();
app.MapControllers();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseProvisioner>().EnsureAsync();

    // Where a company has no duplicate product names, make the database itself refuse new ones (the app already checks). Where duplicates exist it stays off until an admin
    // runs Stock > Tidy up, which merges them and then turns the guard on. Never blocks start-up.
    foreach (var company in registry.All)
    {
        try
        {
            using var companyScope = app.Services.CreateScope();
            companyScope.ServiceProvider.GetRequiredService<Inventory.Api.Infrastructure.RequestCompany>().BackgroundKey = company.Key;
            var guarded = await companyScope.ServiceProvider.GetRequiredService<Inventory.Application.Stock.StockConsolidationService>().EnsureUniqueNameGuardAsync(default);
            if (!guarded) app.Logger.LogWarning("Company {Company} has products that share a name. Open Stock > Tidy up to merge them.", company.Key);
        }
        catch (Exception ex) { app.Logger.LogWarning(ex, "Could not check product-name uniqueness for {Company}", company.Key); }
    }
}

// Demo records for the one business named in Demo:SeedCompany (only if it is empty); no other business is touched.
if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    try { await Inventory.Api.Infrastructure.K9DemoSeeder.RunAsync(app.Services, registry, builder.Configuration["Demo:SeedCompany"], app.Logger); }
    catch (Exception ex) { app.Logger.LogWarning(ex, "Could not fill the demo business with records."); }
}

// Say loudly, once, at start-up what would otherwise fail silently later (a customer's WhatsApp message that is just ignored, a payment webhook that is just rejected).
{
    var startLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var twilio = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Inventory.Infrastructure.WhatsApp.TwilioOptions>>().Value;
    var alat = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Inventory.Infrastructure.Payments.AlatPayOptions>>().Value;
    var known = string.Join(", ", registry.All.Select(c => c.Key));
    foreach (var n in twilio.Numbers.Where(n => registry.Find(n.CompanyKey) is null))
        startLog.LogError("Twilio number {From} is mapped to company '{Key}', which is not one of the configured companies ({Known}). Its WhatsApp messages will be IGNORED. Fix Twilio__Numbers__*__CompanyKey.", n.From, n.CompanyKey, known);
    if (twilio.Numbers.Count > 0 && (twilio.AccountSid.Length == 0 || twilio.AuthToken.Length == 0)) startLog.LogError("Twilio numbers are configured but Twilio__AccountSid / Twilio__AuthToken are empty: no WhatsApp message can be verified or sent.");
    if (twilio.Numbers.Count > 0 && twilio.PublicBaseUrl.Length == 0) startLog.LogWarning("Twilio__PublicBaseUrl is empty: webhook signatures are checked against the request's own URL, which fails behind a proxy.");
    if (alat.SubscriptionKey.Length > 0 && alat.WebhookSecret.Length == 0) startLog.LogError("AlatPay is configured without AlatPay__WebhookSecret: every AlatPay webhook will be rejected, so paid orders would never complete on their own.");
    if (alat.SubscriptionKey.Length > 0 && alat.BusinessId.Length == 0) startLog.LogError("AlatPay__BusinessId is empty: no payment account can be created.");
}

app.Run();

public partial class Program;
