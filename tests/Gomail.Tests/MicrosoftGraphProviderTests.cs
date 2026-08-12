using System.Net;
using System.Text;
using Gomail.Core;
using Gomail.Providers;

namespace Gomail.Tests;

public sealed class MicrosoftGraphProviderTests
{
    [Fact]
    public async Task SendWithSmallAttachment_CreatesDraftAttachesFileAndSends()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gomail-graph-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, "attachment payload");
        try
        {
            var requests = new List<(HttpMethod Method, string Path, string Body)>();
            using var client = new HttpClient(new RecordingHandler(async request =>
            {
                var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync();
                requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
                if (request.Method == HttpMethod.Post && request.RequestUri.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal))
                    return Json(HttpStatusCode.Created, "{\"id\":\"draft-1\"}");
                return Json(HttpStatusCode.OK, "{}");
            }));
            var provider = new MicrosoftGraphMailProvider(new FakeMicrosoftAuthentication(), new SecureEmailHtmlSanitizer(), client);
            var account = Account();
            var result = await provider.SendAsync(account, new OutgoingMessage
            {
                ClientMessageId = Guid.NewGuid(),
                AccountId = account.Id,
                To = new[] { new MailAddress("Recipient", "recipient@example.com") },
                Subject = "Attachment test",
                HtmlBody = "<p>Hello</p>",
                IsImportant = true,
                Attachments = new[] { new OutgoingAttachment("notes.txt", "text/plain", path) }
            });

            Assert.True(result.Success, result.Error);
            Assert.Contains(requests, static item => item.Path.EndsWith("/me/messages", StringComparison.Ordinal));
            Assert.Contains(requests, static item => item.Body.Contains("\"importance\":\"high\"", StringComparison.Ordinal));
            var attachmentRequest = Assert.Single(requests, static item => item.Path.EndsWith("/attachments", StringComparison.Ordinal));
            Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("attachment payload")), attachmentRequest.Body, StringComparison.Ordinal);
            Assert.Contains(requests, static item => item.Path.EndsWith("/messages/draft-1/send", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task SendWithLargeAttachment_UsesChunkedUploadSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gomail-graph-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, new byte[4 * 1024 * 1024 + 1]);
        try
        {
            var ranges = new List<string>();
            using var client = new HttpClient(new RecordingHandler(async request =>
            {
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/me/messages", StringComparison.Ordinal))
                    return Json(HttpStatusCode.Created, "{\"id\":\"draft-2\"}");
                if (request.RequestUri!.AbsolutePath.EndsWith("/createUploadSession", StringComparison.Ordinal))
                    return Json(HttpStatusCode.OK, "{\"uploadUrl\":\"https://upload.example.test/session\"}");
                if (request.Method == HttpMethod.Put)
                {
                    ranges.Add(request.Content!.Headers.ContentRange!.ToString());
                    _ = await request.Content.ReadAsByteArrayAsync();
                    return Json(ranges.Count == 1 ? HttpStatusCode.Accepted : HttpStatusCode.Created, "{}");
                }
                return Json(HttpStatusCode.OK, "{}");
            }));
            var provider = new MicrosoftGraphMailProvider(new FakeMicrosoftAuthentication(), new SecureEmailHtmlSanitizer(), client);
            var account = Account();
            var result = await provider.SendAsync(account, new OutgoingMessage
            {
                ClientMessageId = Guid.NewGuid(),
                AccountId = account.Id,
                To = new[] { new MailAddress(string.Empty, "recipient@example.com") },
                Subject = "Large attachment",
                HtmlBody = "<p>Hello</p>",
                Attachments = new[] { new OutgoingAttachment("archive.bin", "application/octet-stream", path) }
            });

            Assert.True(result.Success, result.Error);
            Assert.Equal(2, ranges.Count);
            Assert.StartsWith("bytes 0-3276799/", ranges[0], StringComparison.Ordinal);
            Assert.StartsWith("bytes 3276800-", ranges[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static MailAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Provider = ProviderKind.Microsoft365,
        Email = "sender@example.com",
        DisplayName = "Sender"
    };

    private static HttpResponseMessage Json(HttpStatusCode status, string content) => new(status)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private sealed class FakeMicrosoftAuthentication : IMicrosoftAuthenticationService
    {
        public bool IsConfigured => true;
        public Task<string> GetAccessTokenAsync(MailAccount account, bool interactive = false, CancellationToken cancellationToken = default) => Task.FromResult("test-token");
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
