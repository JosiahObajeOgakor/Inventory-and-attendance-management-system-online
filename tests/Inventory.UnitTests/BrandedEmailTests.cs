using Inventory.Application.Partners;
using Inventory.Infrastructure.Partners;

namespace Inventory.UnitTests;

public class BrandedEmailTests
{
    [Fact]
    public void A_code_email_carries_the_logo_the_code_and_the_footer_and_escapes_what_it_shows()
    {
        var m = new BrandedMail("a@b.c", null, "123456 is your sign-in code", "Sign in to your partner account", ["Enter this code <now>."], ["candid"], Code: "123456",
            Footnote: "The code works for 15 minutes.");
        var html = BrandedEmail.Html(m, "Candid Purrfect", "12 Allen Avenue · 0803 · hello@candid.ng", "#c2410c", "#1f2937", true);
        Assert.Contains("cid:logo", html);
        Assert.Contains("123456", html);
        Assert.Contains("Enter this code &lt;now&gt;.", html);
        Assert.Contains("Candid Purrfect", html);
        var dir = Environment.GetEnvironmentVariable("EMAIL_PREVIEW_DIR");
        if (!string.IsNullOrEmpty(dir)) File.WriteAllText(Path.Combine(dir, "code-email.html"), html);
    }

    [Fact]
    public void A_big_logo_is_shrunk_for_email()
    {
        using var bmp = new SkiaSharp.SKBitmap(1600, 800);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        var big = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
        var small = BrandedEmail.Shrink(big)!;
        using var back = SkiaSharp.SKBitmap.Decode(small);
        Assert.Equal(320, back.Width);
    }
}
