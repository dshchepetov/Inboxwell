using System.Globalization;
using System.Net;
using System.Text;
using Gomail.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;

namespace Gomail_App;

internal sealed record RichTextContent(string PlainText, string Html, string Rtf);

internal static class RichTextEditorUtilities
{
    // Invisible boundaries let a signature remain fully editable while still being
    // replaceable from the signature picker without treating it as a locked preview.
    public const char SignatureStartMarker = '\u2063';
    public const char SignatureEndMarker = '\u2064';

    public static RichTextContent Capture(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var rawText);
        rawText = rawText.TrimEnd('\r');
        editor.Document.GetText(TextGetOptions.FormatRtf, out var rtf);
        return new RichTextContent(RemoveSignatureMarkers(rawText), CreateHtml(editor, rawText), rtf);
    }

    public static string? GetSignatureText(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        var start = text.IndexOf(SignatureStartMarker);
        if (start < 0) return null;
        var end = text.IndexOf(SignatureEndMarker, start + 1);
        return end < 0 ? null : text[(start + 1)..end].Trim('\r', '\n');
    }

    public static void ReplaceSignature(RichEditBox editor, Signature? signature)
    {
        RemoveSignature(editor);
        if (signature is null) return;

        editor.Document.GetText(TextGetOptions.None, out var text);
        var position = text.TrimEnd('\r').Length;
        var prefix = position == 0 ? string.Empty : "\r\r";
        var startRange = editor.Document.GetRange(position, position);
        startRange.SetText(TextSetOptions.None, prefix + SignatureStartMarker);

        var signatureRange = editor.Document.GetRange(startRange.EndPosition, startRange.EndPosition);
        if (!string.IsNullOrWhiteSpace(signature.Rtf))
        {
            signatureRange.SetText(TextSetOptions.FormatRtf, signature.Rtf);
        }
        else
        {
            signatureRange.SetText(TextSetOptions.None, signature.PlainText);
        }

        var endRange = editor.Document.GetRange(signatureRange.EndPosition, signatureRange.EndPosition);
        endRange.SetText(TextSetOptions.None, SignatureEndMarker.ToString());
        editor.Document.Selection.SetRange(endRange.EndPosition, endRange.EndPosition);
    }

    private static void RemoveSignature(RichEditBox editor)
    {
        editor.Document.GetText(TextGetOptions.None, out var text);
        var start = text.IndexOf(SignatureStartMarker);
        if (start < 0) return;
        var end = text.IndexOf(SignatureEndMarker, start + 1);
        if (end < 0) return;
        var removalStart = start;
        while (removalStart > 0 && removalStart > start - 2 && text[removalStart - 1] is '\r' or '\n') removalStart--;
        editor.Document.GetRange(removalStart, end + 1).SetText(TextSetOptions.None, string.Empty);
    }

    private static string RemoveSignatureMarkers(string text) => text
        .Replace(SignatureStartMarker.ToString(), string.Empty, StringComparison.Ordinal)
        .Replace(SignatureEndMarker.ToString(), string.Empty, StringComparison.Ordinal);

    private static string CreateHtml(RichEditBox editor, string rawText)
    {
        if (rawText.Length == 0) return string.Empty;
        var html = new StringBuilder("<div style=\"font-family:'Segoe UI',Arial,sans-serif;font-size:13px;line-height:1.5\">");
        var bold = false;
        var italic = false;
        var underline = false;
        var fontSize = 0f;

        for (var index = 0; index < rawText.Length; index++)
        {
            var character = rawText[index];
            if (character is SignatureStartMarker or SignatureEndMarker) continue;
            var range = editor.Document.GetRange(index, index + 1);
            var nextBold = range.CharacterFormat.Bold == FormatEffect.On;
            var nextItalic = range.CharacterFormat.Italic == FormatEffect.On;
            var nextUnderline = range.CharacterFormat.Underline != UnderlineType.None;
            var nextSize = range.CharacterFormat.Size;
            if (nextSize <= 0) nextSize = 13;
            if (nextBold != bold || nextItalic != italic || nextUnderline != underline || Math.Abs(nextSize - fontSize) > 0.1)
            {
                CloseFormatting(html, bold, italic, underline, fontSize);
                html.Append(CultureInfo.InvariantCulture, $"<span style=\"font-size:{nextSize:0.#}pt\">");
                if (nextBold) html.Append("<strong>");
                if (nextItalic) html.Append("<em>");
                if (nextUnderline) html.Append("<u>");
                bold = nextBold;
                italic = nextItalic;
                underline = nextUnderline;
                fontSize = nextSize;
            }

            html.Append(character is '\r' or '\n' ? "<br>" : WebUtility.HtmlEncode(character.ToString()));
        }

        CloseFormatting(html, bold, italic, underline, fontSize);
        html.Append("</div>");
        return html.ToString();
    }

    private static void CloseFormatting(StringBuilder html, bool bold, bool italic, bool underline, float fontSize)
    {
        if (underline) html.Append("</u>");
        if (italic) html.Append("</em>");
        if (bold) html.Append("</strong>");
        if (fontSize > 0) html.Append("</span>");
    }
}
