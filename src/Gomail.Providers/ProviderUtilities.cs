using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gomail.Core;

namespace Gomail.Providers;

internal static class ProviderUtilities
{
    public static Guid StableGuid(Guid namespaceId, string value)
    {
        var input = Encoding.UTF8.GetBytes($"{namespaceId:N}:{value}");
        var hash = SHA256.HashData(input);
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        return new Guid(bytes);
    }

    public static string GetSetting(this MailAccount account, string key, string? defaultValue = null)
    {
        if (account.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value;
        }
        if (defaultValue is not null)
        {
            return defaultValue;
        }
        throw new MailProviderException($"Account setting '{key}' is missing.");
    }

    public static int GetIntSetting(this MailAccount account, string key, int defaultValue) =>
        account.Settings.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : defaultValue;

    public static bool GetBoolSetting(this MailAccount account, string key, bool defaultValue) =>
        account.Settings.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue;

    public static string SecretKey(this MailAccount account, string purpose) => $"account:{account.Id:N}:{purpose}";

    public static T? DeserializePayload<T>(PendingOperation operation)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(operation.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException exception)
        {
            throw new MailProviderException("The queued mail action is malformed.", false, exception);
        }
    }

    public static string StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(html.Length);
        var insideTag = false;
        foreach (var character in html)
        {
            if (character == '<')
            {
                insideTag = true;
            }
            else if (character == '>')
            {
                insideTag = false;
                builder.Append(' ');
            }
            else if (!insideTag)
            {
                builder.Append(character);
            }
        }
        return System.Net.WebUtility.HtmlDecode(builder.ToString()).Trim();
    }
}

internal sealed record RemoteOperationPayload(string? FolderRemoteId = null, string? DestinationRemoteId = null, uint? Uid = null, string? LabelId = null);
