namespace Inventory.IntegrationTests;

/// <summary>The public pages (surveys and the rest) send "Home" to the business's real website when it doesn't publish a home page here.</summary>
[Collection("api")]
public class SiteHomeTests(ApiFixture api)
{
    [Fact]
    public async Task Home_and_the_business_name_on_the_survey_pages_go_to_the_real_website()
    {
        var r = await api.Client().GetAsync("/api/site/chewypets/survey");
        r.EnsureSuccessStatusCode();
        var html = await r.Content.ReadAsStringAsync();
        Assert.Contains("<a href=\"https://chewypetsfeeds.com\">Home</a>", html);
        Assert.Contains("<b><a href=\"https://chewypetsfeeds.com\"", html);
        Assert.DoesNotContain("href=\"/api/site/chewypets\"", html);
    }
}
