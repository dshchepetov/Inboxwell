using Gomail.Core;
using Gomail.Providers;

namespace Gomail.Tests;

public sealed class ProviderSafetyTests
{
    [Fact]
    public void DemoProvider_ProducesAUsableMailbox()
    {
        var account = new MailAccount
        {
            Id = Guid.NewGuid(),
            Provider = ProviderKind.Demo,
            Email = "demo@example.com",
            DisplayName = "Demo"
        };
        var batch = DemoMailProvider.CreateDemoBatch(account);
        Assert.Contains(batch.Folders, folder => folder.SpecialKind == SpecialFolderKind.Inbox);
        Assert.Equal(batch.Conversations.Count, batch.Messages.Count);
        Assert.All(batch.Messages, message => Assert.Equal(account.Id, message.AccountId));
    }

    [Fact]
    public void Sanitizer_RemovesExecutableEmailContent()
    {
        Gomail.Core.IHtmlSanitizer sanitizer = new SecureEmailHtmlSanitizer();
        var safe = sanitizer.Sanitize("<script>alert(1)</script><img src='https://example.com/pixel.png' onerror='steal()'><p>Hello</p>");
        Assert.DoesNotContain("<script", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", safe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hello", safe);
        Assert.Contains("Content-Security-Policy", safe);
        Assert.Contains("data-gomail-src", safe, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("img-src data: cid: https:", safe, StringComparison.OrdinalIgnoreCase);

        var explicitlyLoaded = sanitizer.Sanitize(safe, allowExternalImages: true);
        Assert.Contains("src=\"https://example.com/pixel.png\"", explicitlyLoaded, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("img-src data: cid: https: http:", explicitlyLoaded, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, CountOccurrences(explicitlyLoaded, "<!doctype html>"));
        Assert.Contains("data-inboxwell-sanitized=\"1\"", explicitlyLoaded, StringComparison.OrdinalIgnoreCase);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }
}
