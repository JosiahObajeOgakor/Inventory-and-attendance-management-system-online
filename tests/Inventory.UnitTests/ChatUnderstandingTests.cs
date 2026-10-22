using System.Security.Cryptography;
using System.Text;
using Inventory.Application.SalesAssistant.Chat;
using Inventory.Domain.Entities;
using Inventory.Infrastructure.WhatsApp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Inventory.UnitTests;

public class ChatUnderstandingTests
{
    private static readonly List<CatalogItem> Cat =
    [
        new(1, "Adult Dog Food 20kg", "Bag", 11500, 10),
        new(2, "Puppy Starter Food 5kg", "Bag", 4200, 10),
        new(3, "Rice 50kg", "Bag", 70000, 4),
        new(4, "Rice 25kg", "Bag", 36000, 4),
        new(5, "Chicken Feed Layers Mash", "Bag", 9000, 20),
    ];

    private static string N(string s) => ChatLexicon.Normalize(s);

    [Theory]
    [InlineData("Hi Chewy Pet! I'd like to order some dog food.", true)]       // the landing page's old prefilled message
    [InlineData("Hi! I'd like to order dog food.", true)]                       // the new one
    [InlineData("good morning, abeg i wan buy pizza", true)]
    [InlineData("hello", true)]
    [InlineData("history of dog food", false)]                                  // only whole words count: "hi" inside "history" is not a greeting
    [InlineData("2 adult dog food", false)]
    [InlineData("order pizza", false)]
    public void A_message_that_opens_with_a_greeting_is_recognised(string text, bool expected) =>
        Assert.Equal(expected, ChatLexicon.StartsWithGreeting(N(text)));

    [Theory]
    [InlineData("abeg i wan buy 2 adult dog food", ChatLanguages.Pidgin)]
    [InlineData("Ndewo, achọrọ m 1 adult dog food", ChatLanguages.Igbo)]
    [InlineData("Sannu, ina son 3 puppy starter", ChatLanguages.Hausa)]
    [InlineData("I would like 2 bags of rice please", ChatLanguages.English)]
    public void Language_is_spotted_from_the_words(string text, string expected) =>
        Assert.Equal(expected, ChatLexicon.DetectLanguage(N(text), ChatLanguages.English));

    [Fact]
    public void One_stray_word_does_not_flip_a_conversation_that_is_already_in_a_language()
    {
        Assert.Equal(ChatLanguages.Hausa, ChatLexicon.DetectLanguage(N("abeg 2 rice"), ChatLanguages.Hausa));
        Assert.Equal(ChatLanguages.Pidgin, ChatLexicon.DetectLanguage(N("abeg wetin dey"), ChatLanguages.Hausa));
    }

    [Theory]
    [InlineData("what are your list?")]
    [InlineData("i want quotation")]
    [InlineData("i want CHEWYPETS ALS 10KG")]
    public void A_message_plainly_in_English_brings_the_conversation_back_to_English(string text) =>
        Assert.Equal(ChatLanguages.English, ChatLexicon.DetectLanguage(N(text), ChatLanguages.Pidgin));

    [Theory]
    [InlineData("4")]
    [InlineData("yes")]
    [InlineData("abeg i wan buy dog food")]
    public void Short_replies_and_pidgin_keep_pidgin(string text) =>
        Assert.Equal(ChatLanguages.Pidgin, ChatLexicon.DetectLanguage(N(text), ChatLanguages.Pidgin));

    [Theory]
    [InlineData("i wan buy dog food", true)]
    [InlineData("do you have cat food", true)]
    [InlineData("2 chewypets als 10kg", false)]
    public void General_words_ask_for_the_range(string text, bool generic) => Assert.Equal(generic, ChatLexicon.IsGeneric(N(text)));

    [Theory]
    [InlineData("yes", true), InlineData("Ehen, do am", true), InlineData("no wahala", true), InlineData("ee", true), InlineData("toh", true), InlineData("ok", true)]
    [InlineData("no", false), InlineData("mba", false), InlineData("a'a", false), InlineData("cancel", false)]
    public void Yes_is_understood_in_each_language(string text, bool yes) => Assert.Equal(yes, ChatLexicon.IsYes(N(text)));

    [Theory]
    [InlineData("no", true), InlineData("mba", true), InlineData("a'a", true), InlineData("no be so", true), InlineData("yes", false), InlineData("no wahala", false)]
    public void No_is_understood_in_each_language(string text, bool no) => Assert.Equal(no, ChatLexicon.IsNo(N(text)));

    [Theory]
    [InlineData("I don pay"), InlineData("i have paid"), InlineData("akwụọla m ụgwọ"), InlineData("na biya"), InlineData("payment done")]
    public void A_claim_of_payment_is_recognised(string text) => Assert.True(ChatLexicon.IsPaidClaim(N(text)));

    [Fact]
    public void A_single_item_with_quantity_and_filler_words_reduces_to_name_and_number()
    {
        var line = Assert.Single(ProductMatcher.Split(N("abeg i wan buy 2 bags of adult dog food")));
        Assert.Equal(2, line.Qty);
        Assert.Equal("adult dog food", line.Text);
    }

    [Theory]
    [InlineData("2 puppy starter and 3 chicken feed", 2, 3)]
    [InlineData("puppy starter 2 chicken feed 3", 2, 3)]
    [InlineData("two puppy starter, three chicken feed", 2, 3)]
    [InlineData("biyu puppy starter da uku chicken feed", 2, 3)]
    [InlineData("abuo puppy starter na ato chicken feed", 2, 3)]
    public void Several_items_in_one_message_each_keep_their_own_quantity(string text, int first, int second)
    {
        var lines = ProductMatcher.Split(N(text));
        Assert.Equal(2, lines.Count);
        Assert.Equal(first, lines[0].Qty);
        Assert.Equal(second, lines[1].Qty);
        Assert.Equal(2, ProductMatcher.Match(lines[0], Cat).Matches.Single().Id);
        Assert.Equal(5, ProductMatcher.Match(lines[1], Cat).Matches.Single().Id);
    }

    [Theory]
    [InlineData("Ndewo, achọrọ m 1 adult dog food")]
    [InlineData("Sannu, ina son 1 adult dog food")]
    [InlineData("Good morning oga, abeg 1 adult dog food")]
    public void A_greeting_in_front_of_the_order_does_not_hide_the_product(string text)
    {
        var line = Assert.Single(ProductMatcher.Split(N(text)));
        Assert.Equal(1, line.Qty);
        Assert.Equal(1, ProductMatcher.Match(line, Cat).Matches.Single().Id);
    }

    [Fact]
    public void The_word_one_in_an_answer_like_the_5kg_one_is_not_a_quantity()
    {
        Assert.Null(ProductMatcher.FirstQuantity(N("the 5kg one"), ignoreWordOne: true));
        Assert.Equal(1, ProductMatcher.FirstQuantity(N("one")));
    }

    [Theory]
    [InlineData("08031234567", "2348031234567")]
    [InlineData("0803 123 4567", "2348031234567")]
    [InlineData("+234 803 123 4567", "2348031234567")]
    [InlineData("2348031234567", "2348031234567")]
    [InlineData("8031234567", "2348031234567")]
    [InlineData("my number na 0803-123-4567", "2348031234567")]
    [InlineData("+44 7700 900123", "447700900123")]
    [InlineData("123", null)]
    [InlineData("call me", null)]
    [InlineData("0803123", null)]
    public void A_typed_phone_number_is_understood_in_the_usual_nigerian_forms(string text, string? expected) =>
        Assert.Equal(expected, ChatFlowService.ParsePhone(text));

    [Fact]
    public void A_size_in_the_product_name_is_not_mistaken_for_a_quantity()
    {
        var line = Assert.Single(ProductMatcher.Split(N("rice 50kg")));
        Assert.Null(line.Qty);
        Assert.Equal(3, ProductMatcher.Match(line, Cat).Matches.Single().Id);
    }

    [Fact]
    public void A_word_that_fits_two_products_is_ambiguous_and_never_guessed()
    {
        var m = ProductMatcher.Match(new RawLine("rice", 2), Cat);
        Assert.Equal(2, m.Matches.Count);
    }

    [Fact]
    public void Typos_and_plurals_still_find_the_product()
    {
        Assert.Equal(2, ProductMatcher.Match(new RawLine("puppy starer food", 1), Cat).Matches.Single().Id);   // one typo in the identifying word
        Assert.Equal(5, ProductMatcher.Match(new RawLine("chickens feeds", 1), Cat).Matches.Single().Id);
    }

    [Fact]
    public void Something_we_do_not_sell_matches_nothing()
    {
        var m = ProductMatcher.Match(new RawLine("pizza", 1), Cat);
        Assert.Empty(m.Matches);
        Assert.Empty(m.Near);
    }

    [Fact]
    public void Ai_output_is_only_trusted_for_ids_we_offered_and_sane_quantities()
    {
        var r = ChatAiInterpreter.Parse("Sure! {\"intent\":\"order\",\"language\":\"igl\",\"items\":[{\"id\":2,\"qty\":3},{\"id\":999,\"qty\":1},{\"id\":5,\"qty\":-4},{\"id\":3,\"qty\":\"x\"}]}", Cat)!;
        Assert.Equal("order", r.Intent);
        Assert.Equal(ChatLanguages.Igala, r.Language);
        Assert.Equal([(2, 3), (5, 1), (3, 1)], r.Lines.Select(l => (l.ProductId!.Value, l.Qty!.Value)).ToArray());   // 999 dropped; bad quantities become 1, never a negative
        Assert.Null(ChatAiInterpreter.Parse("no json here", Cat));
    }

    [Fact]
    public void Every_english_sentence_exists_in_pidgin_igbo_and_hausa_with_the_same_placeholders()
    {
        static string[] Used(string lang, string key) =>
            System.Text.RegularExpressions.Regex.Matches(ChatText.Get(lang, key, "<0>", "<1>", "<2>", "<3>", "<4>"), @"<\d>").Select(m => m.Value).Distinct().Order().ToArray();
        foreach (var key in ChatText.Keys)
            foreach (var lang in new[] { ChatLanguages.Pidgin, ChatLanguages.Igbo, ChatLanguages.Hausa })
            {
                Assert.True(ChatText.Covers(lang, key), $"{lang} is missing '{key}'");
                Assert.Equal(Used(ChatLanguages.English, key), Used(lang, key));     // same placeholders, so no value is dropped from a translation
            }
    }

    [Fact]
    public void Igala_falls_back_to_pidgin_until_a_native_speaker_supplies_it()
    {
        Assert.False(ChatText.Covers(ChatLanguages.Igala, "welcome"));
        Assert.Equal(ChatText.Get(ChatLanguages.Pidgin, "cancelled"), ChatText.Get(ChatLanguages.Igala, "cancelled"));
    }

    [Fact]
    public void Twilio_signature_is_hmac_sha1_of_url_plus_sorted_fields()
    {
        var client = new TwilioWhatsAppClient(new HttpClient(), Options.Create(new TwilioOptions { AuthToken = "12345" }), NullLogger<TwilioWhatsAppClient>.Instance);
        const string url = "https://mycompany.com/myapp.php?foo=1&bar=2";
        var form = new Dictionary<string, string> { ["Digits"] = "1234", ["To"] = "+18005551212", ["From"] = "+14158675310", ["Caller"] = "+14158675310", ["CallSid"] = "CA1234567890ABCDE" };
        // Example from Twilio's own documentation: fields sorted by name and appended to the URL, HMAC-SHA1 with the auth token, Base64.
        Assert.True(client.IsValidSignature(url, form, "GvWf1cFY/Q7PnoempGyD5oXAezc="));
        Assert.False(client.IsValidSignature(url, form, "GvWf1cFY/Q7PnoempGyD5oXAezd="));
        form["Digits"] = "9999";
        Assert.False(client.IsValidSignature(url, form, "GvWf1cFY/Q7PnoempGyD5oXAezc="));
    }

    [Fact]
    public void Long_replies_are_split_for_whatsapp_without_losing_text()
    {
        var text = string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i} " + new string('x', 20)));
        var parts = TwilioWhatsAppClient.Chunk(text, 1500).ToList();
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.True(p.Length <= 1500));
        Assert.Equal(text.Replace("\n", ""), string.Concat(parts).Replace("\n", ""));
    }
}
