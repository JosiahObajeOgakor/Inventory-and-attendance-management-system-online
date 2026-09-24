using Inventory.Application.Ai;
using Inventory.Application.Queries;
using Microsoft.Extensions.Options;

namespace Inventory.Application.SalesAssistant;

/// <summary>
/// The "vet tool": general pet nutrition/feeding guidance, tied to a product's real stats when one is given.
/// This is deliberately a SEPARATE, narrowly-scoped model call (its own system prompt, no tools, small token
/// budget) so it can never itself create an order, and a keyword pre-filter forces the safety framing even
/// before the model runs. It must never diagnose an illness or recommend a medication/dose — any symptom-shaped
/// question is answered with "see a vet now" and nothing else speculative.
/// </summary>
public sealed class VetGuidanceService(IChatModel model, CatalogQueries catalog, IOptions<SalesAssistantOptions> options)
{
    private static readonly string[] UrgentWords =
        ["blood", "bleeding", "vomit", "seizure", "collapse", "poison", "won't eat", "wont eat", "not eating",
         "not breathing", "dying", "diarrhea", "diarrhoea", "swollen", "limping", "fever", "lethargic"];

    public async Task<string> AskAsync(string question, int? productId, CancellationToken ct)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0) return "Ask a specific question about feeding or nutrition and I'll help.";

        var flare = UrgentWords.Any(w => question.Contains(w, StringComparison.OrdinalIgnoreCase))
            ? "\nIMPORTANT: this question describes a possible symptom or emergency. Lead your entire reply with: \"This sounds like it needs a licensed veterinarian's attention — please contact one now or visit a vet clinic.\" Then stop; do not speculate about causes, diagnoses or treatment."
            : "";

        var product = productId is int id ? await catalog.ProductAsync(id, isAdmin: false, ct) : null;
        var stats = product is null
            ? "No specific product was named."
            : $"{product.Name}" +
              (product.Species is null ? "" : $" (for {product.Species}{(product.LifeStage is null ? "" : $", {product.LifeStage}")})") +
              (product.ProteinPct is null && product.FatPct is null ? "" : $" — protein {product.ProteinPct?.ToString("0.#") ?? "?"}%, fat {product.FatPct?.ToString("0.#") ?? "?"}%") +
              (string.IsNullOrWhiteSpace(product.NutritionSummary) ? "" : $". {product.NutritionSummary}");

        var sys = $"""
            You give general pet nutrition and feeding guidance, grounded in the product facts given to you plus well-known basic canine/feline care facts.
            Product context: {stats}
            You must NEVER diagnose an illness, NEVER suggest a medication, dose or treatment, and NEVER tell the owner what is medically wrong with their pet.
            Any symptom, injury, illness or "something seems wrong" must be answered by recommending a licensed veterinarian immediately — nothing else speculative.
            Stay under 120 words. Discuss only pet nutrition/feeding/general care — nothing else.{flare}
            """;

        var reply = await model.CompleteAsync([new ChatMessage("system", sys), new ChatMessage("user", question)], [], options.Value.VetToolMaxTokens, ct);
        return reply.Content?.Trim() is { Length: > 0 } text ? text : "Please ask your vet about this.";
    }
}
