using Inventory.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;

namespace Inventory.UnitTests;

public class CompanyKeyTests
{
    private static CompanyRegistry Registry() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:Default"] = "Server=x;",
        ["Companies:0:Key"] = "chewypets", ["Companies:0:DisplayName"] = "ChewyPets", ["Companies:0:Schema"] = "a", ["Companies:0:DocumentPrefix"] = "C",
        ["Companies:1:Key"] = "candid", ["Companies:1:DisplayName"] = "Candid", ["Companies:1:Schema"] = "b", ["Companies:1:DocumentPrefix"] = "D",
    }).Build());

    [Theory]
    [InlineData("chewypets", "chewypets")]
    [InlineData("ChewyPets", "chewypets")]
    [InlineData("chewypetsfeeds", "chewypets")]        // the near-miss that would have silently dropped every WhatsApp message
    [InlineData("chewypets-feeds", "chewypets")]
    [InlineData("candid", "candid")]
    public void A_setting_that_clearly_means_a_company_resolves_to_it(string given, string expected) => Assert.Equal(expected, Registry().Resolve(given)?.Key);

    [Theory]
    [InlineData("")] [InlineData(null)] [InlineData("xy")] [InlineData("somethingelse")] [InlineData("che")]
    public void Anything_unclear_resolves_to_nothing_rather_than_a_guess(string? given) => Assert.Null(Registry().Resolve(given));

    [Fact]
    public void Logins_still_need_the_exact_key_Find_is_unchanged()
    {
        Assert.NotNull(Registry().Find("chewypets"));
        Assert.Null(Registry().Find("chewypetsfeeds"));
    }
}
