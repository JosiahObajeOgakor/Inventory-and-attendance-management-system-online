using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Inventory.Application.Abstractions;

namespace Inventory.Api.Infrastructure;

/// <summary>The newer features (Campaigns, Marketing, Accounting, Branches, Time Off and the other apps) are switched on per business (<c>HasNewFeatures</c> in the company's settings). Until they are accepted, only the demo business (K9 Cravings) has them; for any other business the endpoint answers 403.</summary>
public sealed class RequiresNewFeaturesAttribute : ActionFilterAttribute
{
    public RequiresNewFeaturesAttribute() { Order = -100; }   // before anything else (ledger sync included)

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.HttpContext.RequestServices.GetRequiredService<ICompanyContext>().HasNewFeatures) return;
        context.Result = new ObjectResult(new ProblemDetails { Status = 403, Title = "This feature is being tested with K9 Cravings.", Detail = "Switch to K9 Cravings to try it." }) { StatusCode = 403 };
    }
}
