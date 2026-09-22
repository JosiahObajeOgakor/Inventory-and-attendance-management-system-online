using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Inventory.Infrastructure.Localization;

public sealed class LocalizationOptions
{
    public const string Section = "Localization";
    /// <summary>Optional folder holding ig.json: a flat { "English text": "translation" } map that corrects or extends the built-in Igbo without a rebuild (same idea as the desktop app's Assets\lang\ig.json).</summary>
    public string OverridesFolder { get; set; } = "";
}

public sealed record LanguageInfo(string Code, string Name);

/// <summary>
/// English → Igbo. Keys are the exact English text the screens show. The first block is carried over from the desktop app's Lang.vb; the second
/// covers the web screens. MACHINE-ASSISTED Igbo: have a native speaker review it, then correct it through the override file, not by editing code.
/// </summary>
public sealed class LanguageCatalogue(IOptions<LocalizationOptions> options)
{
    public static readonly IReadOnlyList<LanguageInfo> Languages = [new("en", "English"), new("ig", "Igbo")];

    public IReadOnlyDictionary<string, string> Get(string code)
    {
        if (!string.Equals(code, "ig", StringComparison.OrdinalIgnoreCase)) return new Dictionary<string, string>();
        var map = new Dictionary<string, string>(Igbo, StringComparer.Ordinal);
        try
        {
            var folder = options.Value.OverridesFolder;
            var path = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, "ig.json");
            if (path is not null && File.Exists(path) && new FileInfo(path).Length < 512 * 1024)
                foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [])
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value is not null && kv.Value.Length <= 300) map[kv.Key] = kv.Value;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { /* a bad override file never breaks the app: the built-in text is used */ }
        return map;
    }

    private static readonly Dictionary<string, string> Igbo = new(StringComparer.Ordinal)
    {
        // ---- carried over from the desktop app (Lang.vb)
        ["Dashboard"] = "Nchịkọta", ["Inventory"] = "Ngwongwo", ["Sales"] = "Ire ahịa", ["Receipts"] = "Akwụkwọ nnata", ["Purchases"] = "Ịzụ ahịa",
        ["Suppliers"] = "Ndị na-eweta ngwongwo", ["Customers"] = "Ndị ahịa", ["Rebates"] = "Ego mgbaghara", ["Waybill"] = "Akwụkwọ mbupu", ["Income"] = "Ego mbata",
        ["Finance"] = "Ego", ["Expenses"] = "Mmefu ego", ["Employees"] = "Ndị ọrụ", ["Appearance"] = "Ọdịdị", ["Sign out"] = "Pụọ", ["Sign in"] = "Banye",
        ["Username"] = "Aha njirimara", ["Password"] = "Okwuntughe", ["Confirm password"] = "Kwenye okwuntughe", ["New password"] = "Okwuntughe ọhụrụ",
        ["Current password"] = "Okwuntughe ugbu a", ["Save password"] = "Chekwaa okwuntughe", ["Full name"] = "Aha zuru ezu", ["Role"] = "Ọrụ",
        ["Email"] = "Ozi-e", ["Save"] = "Chekwaa", ["Cancel"] = "Kagbuo", ["OK"] = "Ọ dị mma", ["Delete"] = "Hichapụ", ["Edit"] = "Dezie",
        ["Export Excel"] = "Bupụta Excel", ["New sale"] = "Ire ọhụrụ", ["Save sale"] = "Chekwaa ire", ["Record payment"] = "Dekọọ ịkwụ ụgwọ",
        ["Record production"] = "Dekọọ ihe e mepụtara", ["Production history"] = "Akụkọ ihe e mepụtara", ["Gross profit"] = "Uru mbụ", ["Subtotal"] = "Mkpọkọta nta",
        ["Discount"] = "Mbelata ọnụahịa", ["Outstanding"] = "Ụgwọ fọdụrụ", ["Warehouse"] = "Ụlọ nkwakọba", ["Product"] = "Ngwaahịa", ["Customer"] = "Onye ahịa",
        ["Quantity"] = "Ọnụọgụ", ["Distributor"] = "Onye nkesa", ["Wholesaler"] = "Onye na-ere ọtụtụ", ["Retailer"] = "Onye na-ere nta", ["Walk-in"] = "Onye bịara ọbịbịa",
        ["Ranking"] = "Ọkwa", ["Payment method"] = "Ụzọ ịkwụ ụgwọ", ["Cost of goods sold"] = "Ọnụahịa ngwongwo erere", ["Driver name"] = "Aha onye ọkwọ ụgbọala",
        ["Destination"] = "Ebe a na-eje", ["Notes"] = "Ndetu", ["Create waybill"] = "Mepụta akwụkwọ mbupu", ["Monthly payroll"] = "Ụgwọ ọnwa", ["Give loan"] = "Nye mbinye ego",
        ["Please confirm"] = "Biko kwenye", ["Notice"] = "Ọkwa", ["Search:"] = "Chọọ:", ["Edit prices"] = "Dezie ọnụahịa", ["Expiry"] = "Ụbọchị ngwụcha",

        // ---- web screens: navigation
        ["Counter"] = "Ebe ire ahịa", ["Trade"] = "Ahịa", ["Office"] = "Ụlọ ọrụ", ["Stock"] = "Ngwongwo", ["Products"] = "Ngwaahịa", ["Price book"] = "Akwụkwọ ọnụahịa",
        ["Serial numbers"] = "Nọmba njirimara", ["Quotations"] = "Ọnụahịa e kwuru", ["Waybills"] = "Akwụkwọ mbupu", ["Analytics"] = "Nyocha", ["Team"] = "Ndị otu",
        ["Company & documents"] = "Ụlọ ọrụ na akwụkwọ", ["People & access"] = "Ndị mmadụ na ohere", ["Activity log"] = "Akụkọ ọrụ", ["Database storage"] = "Nchekwa data",
        ["Change my password"] = "Gbanwee okwuntughe m", ["Skip to content"] = "Wụga n'ọdịnaya", ["Signed in as"] = "Banyere dịka", ["Administrator"] = "Onye nlekọta",
        ["Warehouse clerk"] = "Onye ọrụ ụlọ nkwakọba", ["Language"] = "Asụsụ", ["Overview"] = "Nchịkọta",
        // ---- buttons and actions
        ["New customer"] = "Onye ahịa ọhụrụ", ["New product"] = "Ngwaahịa ọhụrụ", ["New supplier"] = "Onye na-eweta ọhụrụ", ["New purchase"] = "Ịzụ ahịa ọhụrụ",
        ["New quotation"] = "Ọnụahịa ọhụrụ", ["New waybill"] = "Akwụkwọ mbupu ọhụrụ", ["New employee"] = "Onye ọrụ ọhụrụ", ["Add customer"] = "Tinye onye ahịa", ["Add supplier"] = "Tinye onye na-eweta",
        ["Save changes"] = "Chekwaa mgbanwe", ["Remove"] = "Wepụ", ["Close"] = "Mechie", ["Back"] = "Laghachi", ["Previous"] = "Nke gara aga", ["Next"] = "Sochie", ["Refresh"] = "Megharịa",
        ["Download PDF"] = "Budata PDF", ["Receipt (PDF)"] = "Akwụkwọ nnata (PDF)", ["Print this page"] = "Bipụta ibe a", ["Email"] = "Ozi-e", ["Void sale"] = "Kagbuo ire", ["Excel"] = "Excel",
        ["Send email"] = "Zipu ozi-e", ["Make a sale"] = "Mee ire", ["Cancel"] = "Kagbuo", ["Record expense"] = "Dekọọ mmefu ego", ["Give back"] = "Nyeghachi", ["Check in"] = "Debanye aha", ["Not now"] = "Ọ bụghị ugbu a",
        ["Ask"] = "Jụọ", ["Loans"] = "Mbinye ego", ["Label"] = "Akara", ["History"] = "Akụkọ", ["Add products"] = "Tinye ngwaahịa", ["Save prices"] = "Chekwaa ọnụahịa", ["Discard"] = "Hapụ",
        ["Take back"] = "Weghachi", ["Write off"] = "Hichapụ dị ka ọghọm", ["Receive units"] = "Nata ngwaahịa", ["Generate"] = "Mepụta", ["Pay"] = "Kwụọ", ["Pay all"] = "Kwụọ ha niile",
        // ---- table headings and fields
        ["Date"] = "Ụbọchị", ["Total"] = "Mkpọkọta", ["Status"] = "Ọnọdụ", ["Name"] = "Aha", ["Phone"] = "Ekwentị", ["Address"] = "Adreesị", ["Category"] = "Ụdị", ["Note"] = "Ndetu",
        ["Amount"] = "Ọnụọgụ ego", ["Price"] = "Ọnụahịa", ["Qty"] = "Ọnụọgụ", ["Balance"] = "Ụgwọ fọdụrụ", ["Line total"] = "Mkpọkọta ahịrị", ["In stock"] = "Dị na ngwongwo",
        ["Paid"] = "Akwụọla", ["Unpaid"] = "Akwụghị", ["Partial"] = "Akwụrụ ụfọdụ", ["Voided"] = "Ekagbuola", ["Pending"] = "Na-echere", ["Received"] = "Natara", ["Open"] = "Mepere emepe",
        ["Converted"] = "Agbanwere", ["Cancelled"] = "Kagbuola", ["Low stock"] = "Ngwongwo na-agwụ", ["Out of stock"] = "Ngwongwo agwụla", ["Retail"] = "Nta", ["Wholesale"] = "Ọtụtụ",
        ["Cost"] = "Ọnụ ahịa e zụrụ", ["Unit"] = "Otu", ["Invoice"] = "Akwụkwọ ụgwọ", ["Quotation"] = "Ọnụahịa e kwuru", ["Salary"] = "Ụgwọ ọnwa", ["Net pay"] = "Ụgwọ ọnwa a ga-anata",
        ["Owes"] = "Ji ụgwọ", ["Month"] = "Ọnwa", ["Year"] = "Afọ", ["From"] = "Site na", ["To"] = "Ruo", ["Reason"] = "Ihe kpatara", ["Actions"] = "Ihe a ga-eme", ["Employee"] = "Onye ọrụ",
        ["Supplier"] = "Onye na-eweta", ["Location"] = "Ebe", ["Contact person"] = "Onye a ga-akpọtụrụ", ["Credit limit"] = "Oke mbinye ego", ["Tax ID"] = "Nọmba ụtụ isi",
        // ---- headings and messages
        ["Top customers"] = "Ndị ahịa kacha", ["Revenue"] = "Ego batara", ["Income received"] = "Ego anatara", ["Profit"] = "Uru", ["Losses"] = "Ọghọm", ["Recommendations"] = "Ndụmọdụ",
        ["Who owes, and when"] = "Onye ji ụgwọ, na mgbe", ["Stock by warehouse"] = "Ngwongwo n'ụlọ nkwakọba", ["Largest overdue balances"] = "Ụgwọ ọ kachasị gafere oge",
        ["Not selling"] = "Anaghị eme ire", ["Total inventory"] = "Ngwongwo niile", ["Recent sales"] = "Ire ahịa n'oge na-adịbeghị anya", ["Needs reordering"] = "Chọrọ ịzụ ọzọ",
        ["Low or expiring stock"] = "Ngwongwo na-agwụ ma ọ bụ na-agwụ ụbọchị", ["Good day — start your shift?"] = "Ụtụtụ ọma — malite ọrụ gị?",
        ["Ask about your business"] = "Jụọ banyere azụmahịa gị", ["Demand forecast"] = "Amụma mkpa ahịa", ["Sold together"] = "Ere ọnụ", ["Unusual movements"] = "Mmegharị ndị pụrụ iche",
        ["Spend by month"] = "Ego e mefuru kwa ọnwa", ["Most bought products"] = "Ngwaahịa a zụrụ kacha", ["Company details"] = "Nkọwa ụlọ ọrụ", ["Registered name"] = "Aha edebanyere",
        ["Bank accounts"] = "Akaụntụ ụlọ akụ", ["Nothing overdue. Well done."] = "Ọ dịghị ihe gafere oge. Ọ dị mma.", ["Sign-in"] = "Ịbanye", ["Stock desk"] = "Ọfịs ngwongwo",
        ["Archive old records"] = "Chekwaa ndekọ ochie", ["Archive files"] = "Faịlụ nchekwa ochie", ["Bold column headers"] = "Isiokwu ogidi dị arọ", ["Reduce card shadows"] = "Belata ndò kaadị",
    };
}
