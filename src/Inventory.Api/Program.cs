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
builder.Services.AddHttpClient<Inventory.Application.Payments.IPaymentGateway, Inventory.Infrastructure.Payments.PaystackGateway>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient("sms", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<Inventory.Application.Payments.IAdminNotifier, Inventory.Infrastructure.Payments.AdminNotifier>();
builder.Services.Configure<Inventory.Infrastructure.Localization.LocalizationOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Localization.LocalizationOptions.Section));
builder.Services.AddSingleton<Inventory.Infrastructure.Localization.LanguageCatalogue>();
builder.Services.Configure<Inventory.Infrastructure.Maintenance.ArchiveOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.Maintenance.ArchiveOptions.Section));
builder.Services.AddScoped<Inventory.Infrastructure.Maintenance.ArchiveService>();
builder.Services.Configure<Inventory.Application.Ai.AiOptions>(builder.Configuration.GetSection(Inventory.Application.Ai.AiOptions.Section));
builder.Services.Configure<Inventory.Application.SalesAssistant.SalesAssistantOptions>(builder.Configuration.GetSection(Inventory.Application.SalesAssistant.SalesAssistantOptions.Section));
builder.Services.Configure<Inventory.Infrastructure.WhatsApp.WhatsAppOptions>(builder.Configuration.GetSection(Inventory.Infrastructure.WhatsApp.WhatsAppOptions.Section));
builder.Services.AddHttpClient<Inventory.Application.Messaging.IWhatsAppSender, Inventory.Infrastructure.WhatsApp.MetaWhatsAppClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.Configure<Inventory.Application.Email.SmtpOptions>(builder.Configuration.GetSection(Inventory.Application.Email.SmtpOptions.Section));
builder.Services.AddHttpClient<Inventory.Application.Ai.IChatModel, Inventory.Infrastructure.Ai.OpenAiChatModel>(c => c.Timeout = TimeSpan.FromSeconds(45));
builder.Services.AddScoped<Inventory.Application.Ai.IAiUsageStore, Inventory.Infrastructure.Ai.AiUsageStore>();
builder.Services.AddSingleton<Inventory.Application.Email.IEmailSender, Inventory.Infrastructure.Email.SmtpEmailSender>();
builder.Services.AddScoped<Inventory.Application.Queries.IUserDirectory, UserDirectory>();
builder.Services.AddApplication();

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
    .AddPolicy(Policies.Admin, p => p.RequireRole(RoleNames.Admin))
    .AddPolicy(Policies.Staff, p => p.RequireRole(RoleNames.Admin, RoleNames.Clerk));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(Policies.AuthRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimit:LoginPerMinute", 10), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    // Meta's webhook calls all arrive from its own servers, not the customer's IP, so this only guards against a flood/misconfiguration —
    // the real per-customer limit is SalesAssistantOptions.DailyMessagesPerConversation, enforced inside SalesAssistantService.
    o.AddPolicy(Policies.WhatsAppRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter("whatsapp",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(Policies.WebChatRateLimit, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
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
app.UseAuthorization();
app.MapControllers();

if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
{
    using var scope = app.Services.CreateScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseProvisioner>().EnsureAsync();
}

app.Run();

public partial class Program;
