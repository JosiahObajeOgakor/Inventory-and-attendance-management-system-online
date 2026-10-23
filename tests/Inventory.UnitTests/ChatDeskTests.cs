using Inventory.Application.SalesAssistant.Chat;

namespace Inventory.UnitTests;

/// <summary>Which question the enquiry desk hears in a message, without any AI.</summary>
public class ChatDeskTests
{
    private static string? Q(string text) => ChatLexicon.EnquiryOf(ChatLexicon.Normalize(text));

    [Theory]
    [InlineData("Hi! Do you deliver to my area, and how much is delivery?", ChatLexicon.AskDelivery)]
    [InlineData("una dey deliver to ikeja?", ChatLexicon.AskDelivery)]
    [InlineData("how much is chewypets als", ChatLexicon.AskPrice)]
    [InlineData("wetin be the price", ChatLexicon.AskPrice)]
    [InlineData("do you have puppy food", ChatLexicon.AskStock)]
    [InlineData("una get booster?", ChatLexicon.AskStock)]
    [InlineData("how much should i feed a 10kg dog", ChatLexicon.AskInfo)]
    [InlineData("which one is best for my puppy", ChatLexicon.AskRecommend)]
    [InlineData("what time do you open on sunday", ChatLexicon.AskHours)]
    [InlineData("where is your shop", ChatLexicon.AskLocation)]
    [InlineData("can i pay on delivery", ChatLexicon.AskPayHow)]
    [InlineData("is it safe to pay you", ChatLexicon.AskPayHow)]
    [InlineData("last price abeg", ChatLexicon.AskDiscount)]
    [InlineData("i want to speak to someone", ChatLexicon.AskHuman)]
    public void Questions_are_recognised(string text, string intent) => Assert.Equal(intent, Q(text));

    [Theory]
    [InlineData("2 chewypets als 10kg")]
    [InlineData("yes")]
    [InlineData("I have paid")]
    [InlineData("4")]
    [InlineData("cancel")]
    public void Orders_and_answers_are_not_questions(string text) => Assert.Null(Q(text));

    [Theory]
    [InlineData("which food is best for my puppy", "dog", "young")]
    [InlineData("food for my kitten", "cat", "young")]
    [InlineData("my old dog", "dog", "senior")]
    [InlineData("adult cat", "cat", "adult")]
    public void A_pet_and_its_stage_are_picked_out(string text, string species, string stage) =>
        Assert.Equal((species, stage), ChatLexicon.PetOf(ChatLexicon.Normalize(text)));
}
