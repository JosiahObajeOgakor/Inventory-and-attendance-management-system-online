using Inventory.Application.Ledger;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Inventory.Api.Infrastructure;

/// <summary>Before any accounting screen answers, bring the books up to date with the documents recorded since they were last read.</summary>
public sealed class LedgerSyncFilter(LedgerSync sync) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        await sync.RunAsync(context.HttpContext.RequestAborted);
        await next();
    }
}
