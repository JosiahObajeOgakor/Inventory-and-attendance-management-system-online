using Inventory.Application.AppsEngine;
using Inventory.Application.TimeOff;

namespace Inventory.UnitTests;

/// <summary>The record-keeping apps are declared in data, so a typo in a definition would only show up when someone opened that screen. These catch it earlier.</summary>
public class AppsEngineTests
{
    public static IEnumerable<object[]> Apps() => AppCatalog.All.Select(a => new object[] { a.Key });

    [Fact]
    public void The_catalog_holds_exactly_the_bought_apps()
    {
        // Time Off has its own module; every other bought app is a definition here.
        var expected = AppAccess.Bought.Where(k => k != "time-off").Order().ToList();
        Assert.Equal(expected, AppCatalog.All.Select(a => a.Key).Order().ToList());
        Assert.Equal(AppCatalog.All.Count, AppCatalog.All.Select(a => a.Key).Distinct().Count());
    }

    [Theory, MemberData(nameof(Apps))]
    public void An_apps_definition_is_consistent(string key)
    {
        var app = AppCatalog.Find(key)!;
        Assert.NotEmpty(app.Kinds);
        Assert.Equal(app.Kinds.Length, app.Kinds.Select(k => k.Key).Distinct().Count());
        foreach (var k in app.Kinds)
        {
            Assert.NotEmpty(k.Statuses);
            Assert.Equal(k.Fields.Length, k.Fields.Select(f => f.Key).Distinct().Count());
            if (k.Parent is not null) Assert.Contains(app.Kinds, p => p.Key == k.Parent);
            // The first status is where records start; every transition must lead to a status that exists.
            foreach (var s in k.Statuses) Assert.All(s.Next, n => Assert.Contains(k.Statuses, x => x.Name == n));
            Assert.All(k.Statuses, s => Assert.Contains(s.Tone, new[] { "ok", "warn", "bad", "info" }));
            foreach (var f in k.Fields)
            {
                Assert.Contains(f.Type, new[] { "text", "textarea", "number", "money", "date", "select", "bool", "email", "phone", "lookup", "lines", "json" });
                if (f.Type == "select") Assert.NotEmpty(f.Options ?? []);
                if (f.Type == "lookup") Assert.True(f.Lookup is "customer" or "employee" or "product" || (f.Lookup?.StartsWith("record:") == true && app.Kinds.Any(x => x.Key == f.Lookup[7..])), $"{f.Key} has a bad lookup");
                if (f.Type == "select" && f.Default is not null) Assert.Contains(f.Default, f.Options!);
                Assert.False(f.ReadOnly && f.Required, $"{f.Key}: a read-only field can't be required");
            }
            Assert.Equal((k.Actions ?? []).Length, (k.Actions ?? []).Select(a => a.Key).Distinct().Count());
            foreach (var a in k.Actions ?? []) Assert.All(a.InStatuses ?? [], s => Assert.Contains(k.Statuses, x => x.Name == s));
            Assert.True(k.Fields.Any(f => f.Key == "title") || app.Key == "appraisals");   // an appraisal is titled from its employee by its hook
        }
    }

    [Fact]
    public void The_bought_list_is_the_default_and_configuration_can_only_narrow_it()
    {
        Assert.Equal(AppAccess.Bought.Order(), new AppAccess(null).Enabled.Order());
        Assert.Equal(AppAccess.Bought.Order(), new AppAccess("").Enabled.Order());
        var some = new AppAccess("events, blog; not-an-app,pos-shop");
        Assert.True(some.IsEnabled("events")); Assert.True(some.IsEnabled("blog"));
        Assert.False(some.IsEnabled("sign"));
        Assert.False(some.IsEnabled("pos-shop"));   // not built into this installation, so it can't be switched on
    }

    [Theory]
    [InlineData("Rice & Beans promo!", "rice-beans-promo")]
    [InlineData("  Ọ̀pẹ́ Day  ", "ope-day")]
    [InlineData("!!!", "page")]
    public void Web_addresses_are_made_from_titles(string title, string slug) => Assert.Equal(slug, AppHelpers.Slug(title));

    [Fact]
    public void Survey_questions_are_read_one_per_line()
    {
        var qs = SurveyQuestions.Parse("Was it fast? | yesno\nRate us | rating\nWhich do you buy most? | choice: Rice; Beans; Oil\nAnything else?\nBad choice | choice: Only one");
        Assert.Equal(["yesno", "rating", "choice", "text", "text"], qs.Select(q => q.Type));
        Assert.Equal(["Rice", "Beans", "Oil"], qs[2].Choices);
    }

    [Fact]
    public void Leave_counts_only_weekdays_both_ends_included()
    {
        // Mon 12 Oct 2026 to Mon 19 Oct 2026: Mon–Fri, then Mon again = 6 working days.
        Assert.Equal(6, TimeOffService.WorkingDays(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 19)));
        Assert.Equal(1, TimeOffService.WorkingDays(new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 12)));
        Assert.Equal(0, TimeOffService.WorkingDays(new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18)));   // Saturday and Sunday
    }
}
