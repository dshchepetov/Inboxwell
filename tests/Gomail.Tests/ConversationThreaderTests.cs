using Gomail.Core;

namespace Gomail.Tests;

public sealed class ConversationThreaderTests
{
    [Theory]
    [InlineData("Re: Re: Project update", "project update")]
    [InlineData("Fwd:   Project   update ", "project update")]
    [InlineData("Ответ: План запуска", "план запуска")]
    [InlineData("", "(no subject)")]
    public void NormalizeSubject_RemovesReplyPrefixesAndWhitespace(string input, string expected) =>
        Assert.Equal(expected, ConversationThreader.NormalizeSubject(input));

    [Fact]
    public void CreateThreadKey_PrefersProviderThreadId()
    {
        var accountId = Guid.NewGuid();
        var key = ConversationThreader.CreateThreadKey(accountId, "thread-42", "<message@host>", null, null, "Subject");
        Assert.Equal($"provider:{accountId:N}:thread-42", key);
    }

    [Fact]
    public void CreateThreadKey_IsStableForFallbackParticipants()
    {
        var accountId = Guid.NewGuid();
        var first = ConversationThreader.CreateThreadKey(accountId, null, null, null, null, "Re: Hello", new[]
        {
            new MailAddress("A", "A@Example.com"),
            new MailAddress("B", "b@example.com")
        });
        var second = ConversationThreader.CreateThreadKey(accountId, null, null, null, null, "hello", new[]
        {
            new MailAddress("B", "b@example.com"),
            new MailAddress("A", "a@example.com")
        });
        Assert.Equal(first, second);
    }
}
