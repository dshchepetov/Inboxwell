using Ganss.Xss;
using Gomail.Core;
using System.Text.RegularExpressions;

namespace Gomail.Providers;

public sealed class SecureEmailHtmlSanitizer : Gomail.Core.IHtmlSanitizer
{
    private const string SanitizedMarker = "data-inboxwell-sanitized=\"1\"";
    private const string BlockedImagePolicy = "img-src data: cid:;";
    private const string AllowedImagePolicy = "img-src data: cid: https: http:;";
    private static readonly Regex ExternalImageSource = new(
        @"(?is)(<img\b[^>]*?)\s+src\s*=\s*(['""])(https?://.*?)\2",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex DeferredImageSource = new(
        @"(?is)\s+data-gomail-src\s*=",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HtmlSanitizer sanitizer = new();

    public SecureEmailHtmlSanitizer()
    {
        sanitizer.AllowedSchemes.Clear();
        sanitizer.AllowedSchemes.Add("https");
        sanitizer.AllowedSchemes.Add("http");
        sanitizer.AllowedSchemes.Add("mailto");
        sanitizer.AllowedSchemes.Add("cid");
        sanitizer.AllowedSchemes.Add("data");

        sanitizer.AllowedTags.Remove("form");
        sanitizer.AllowedTags.Remove("input");
        sanitizer.AllowedTags.Remove("button");
        sanitizer.AllowedAttributes.Remove("srcset");
        sanitizer.AllowedAttributes.Remove("formaction");
        sanitizer.AllowedAttributes.Add("data-gomail-src");
        sanitizer.RemovingAttribute += (_, args) =>
        {
            if (args.Attribute.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = false;
            }
        };
    }

    public string Sanitize(string html, bool allowExternalImages = false)
    {
        var source = html ?? string.Empty;
        if (IsSanitizedDocument(source))
        {
            if (!allowExternalImages) return source;
            return DeferredImageSource
                .Replace(source, " src=")
                .Replace(BlockedImagePolicy, AllowedImagePolicy, StringComparison.OrdinalIgnoreCase);
        }

        source = allowExternalImages
            ? DeferredImageSource.Replace(source, " src=")
            : ExternalImageSource.Replace(source, "$1 data-gomail-src=$2$3$2");
        var safe = sanitizer.Sanitize(source);
        var imageSources = allowExternalImages ? "data: cid: https: http:" : "data: cid:";
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src {{imageSources}}; style-src 'unsafe-inline'; font-src data:; base-uri 'none'; form-action 'none';">
            <meta name="color-scheme" content="light dark">
            <style>
            :root { font-family: 'Segoe UI Variable', 'Segoe UI', sans-serif; color: #1e2329; background: #ffffff; }
            @media (prefers-color-scheme: dark) { :root { color:#f4f0e8; background:#202126; } a { color:#6ea8ff; } }
            body { margin:0; overflow-wrap:anywhere; line-height:1.55; font-size:15px; }
            img { max-width:100%; height:auto; } pre { white-space:pre-wrap; }
            blockquote { margin-left:0; padding-left:14px; border-left:2px solid #d7d2c8; color:#66707c; }
            </style></head><body {{SanitizedMarker}}>{{safe}}</body></html>
            """;
    }

    private static bool IsSanitizedDocument(string source) =>
        source.Contains(SanitizedMarker, StringComparison.OrdinalIgnoreCase) ||
        (source.Contains("<!doctype html>", StringComparison.OrdinalIgnoreCase) &&
         source.Contains("default-src 'none'; img-src", StringComparison.OrdinalIgnoreCase) &&
         source.Contains("overflow-wrap:anywhere; line-height:1.55", StringComparison.OrdinalIgnoreCase));
}
