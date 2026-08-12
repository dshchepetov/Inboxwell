using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Gomail.Core;

public static partial class ConversationThreader
{
    [GeneratedRegex(@"^(?:(?:re|fw|fwd|aw|sv|ответ|пересл)\s*:\s*)+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubjectPrefixRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    public static string NormalizeSubject(string? subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return "(no subject)";
        }

        var withoutPrefixes = SubjectPrefixRegex().Replace(subject.Trim(), string.Empty);
        return WhitespaceRegex().Replace(withoutPrefixes, " ").Trim().ToLowerInvariant();
    }

    public static string CreateThreadKey(
        Guid accountId,
        string? providerThreadId,
        string? internetMessageId,
        string? inReplyTo,
        IReadOnlyList<string>? references,
        string? subject,
        IEnumerable<MailAddress>? participants = null)
    {
        if (!string.IsNullOrWhiteSpace(providerThreadId))
        {
            return $"provider:{accountId:N}:{providerThreadId.Trim()}";
        }

        var rootReference = references?
            .Select(NormalizeMessageId)
            .FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

        var parentId = NormalizeMessageId(inReplyTo);
        var ownId = NormalizeMessageId(internetMessageId);
        var stableRoot = rootReference ?? parentId ?? ownId;

        if (!string.IsNullOrWhiteSpace(stableRoot))
        {
            return $"rfc:{accountId:N}:{stableRoot}";
        }

        var participantKey = string.Join(',', (participants ?? Array.Empty<MailAddress>())
            .Select(static address => address.Address.Trim().ToLowerInvariant())
            .Where(static address => address.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static address => address, StringComparer.Ordinal));
        var fallback = $"{accountId:N}|{NormalizeSubject(subject)}|{participantKey}";
        return $"fallback:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fallback))).ToLowerInvariant()}";
    }

    public static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().Trim('<', '>').ToLowerInvariant();
    }
}
