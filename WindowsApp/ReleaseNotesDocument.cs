using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Voltra;

internal static partial class ReleaseNotesDocument
{
    internal static string ToRtf(string markdown, Color text, Color codeBackground)
    {
        markdown = HiddenHtml().Replace(WebUtility.HtmlDecode(markdown), "");
        markdown = HtmlBreak().Replace(markdown, "\n");
        markdown = HtmlTag().Replace(markdown, "");
        if (string.IsNullOrWhiteSpace(markdown)) markdown = "No release notes were provided.";

        var output = new StringBuilder(@"{\rtf1\ansi\uc1\deff0{\fonttbl{\f0 Segoe UI;}{\f1 Consolas;}}");
        output.Append(@"{\colortbl;");
        AppendColor(output, text);
        AppendColor(output, codeBackground);
        output.Append('}');

        bool paragraph = false, first = true, inCode = false;
        foreach (string source in markdown.Split('\n'))
        {
            string line = source.Trim();
            if (line.StartsWith("\u0060\u0060\u0060", StringComparison.Ordinal) ||
                line.StartsWith("~~~", StringComparison.Ordinal))
            {
                EndParagraph();
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                output.Append(@"\pard\cf1\f1\fs19\sa20 ");
                AppendText(output, source.TrimEnd('\r'));
                output.Append(@"\par ");
                first = false;
                continue;
            }
            if (line.Length == 0)
            {
                EndParagraph();
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                EndParagraph();
                string title = ClosingHashes().Replace(heading.Groups[2].Value, "");
                if (first && title.Equals("What's new", StringComparison.OrdinalIgnoreCase))
                    continue;
                int size = heading.Groups[1].Length switch { 1 => 30, 2 => 26, _ => 23 };
                output.Append(@"\pard\cf1\f0\b\sb").Append(first ? 0 : 140)
                    .Append(@"\sa65\fs").Append(size).Append(' ');
                AppendInline(output, title);
                output.Append(@"\b0\par ");
            }
            else if (ListItem().Match(line) is { Success: true } item)
            {
                EndParagraph();
                int indent = Math.Min(3, (source.Length - source.TrimStart().Length) / 2) * 240 + 300;
                output.Append(@"\pard\cf1\f0\fs20\sa55\li").Append(indent)
                    .Append(@"\fi-270\tx").Append(indent).Append(' ');
                string marker = item.Groups[1].Value;
                if (marker.Length == 1) output.Append(@"\bullet ");
                else AppendText(output, marker);
                output.Append(@"\tab ");
                AppendInline(output, item.Groups[2].Value);
                output.Append(@"\par ");
            }
            else
            {
                output.Append(paragraph ? @"\line " : @"\pard\cf1\f0\fs20\sa110 ");
                AppendInline(output, line);
                paragraph = true;
            }
            first = false;
        }
        EndParagraph();
        if (first) output.Append(@"\pard\cf1\f0\fs20 No release notes were provided.\par ");
        return output.Append('}').ToString();

        void EndParagraph()
        {
            if (!paragraph) return;
            output.Append(@"\par ");
            paragraph = false;
        }
    }

    private static void AppendInline(StringBuilder output, string text, bool bold = false)
    {
        int end = 0;
        foreach (Match token in InlineToken().Matches(text))
        {
            AppendText(output, text.AsSpan(end, token.Index - end));
            if (token.Groups["code"] is { Success: true } code)
            {
                output.Append(@"{\f1\highlight2 ");
                AppendText(output, code.ValueSpan);
                output.Append('}');
            }
            else if (token.Groups["label"] is { Success: true } label)
            {
                AppendText(output, label.ValueSpan);
            }
            else
            {
                var strong = token.Groups["strong"];
                if (!strong.Success) strong = token.Groups["underscore"];
                output.Append(@"{\b ");
                if (bold) AppendText(output, strong.ValueSpan);
                else AppendInline(output, strong.Value, bold: true);
                output.Append('}');
            }
            end = token.Index + token.Length;
        }
        AppendText(output, text.AsSpan(end));
    }

    private static void AppendText(StringBuilder output, ReadOnlySpan<char> text)
    {
        foreach (char character in text)
        {
            if (character is '\\' or '{' or '}') output.Append('\\').Append(character);
            else if (character == '\t') output.Append(@"\tab ");
            else if (character is >= ' ' and <= '~') output.Append(character);
            else if (!char.IsControl(character)) output.Append(@"\u").Append((short)character).Append('?');
        }
    }

    private static void AppendColor(StringBuilder output, Color color) =>
        output.Append(@"\red").Append(color.R).Append(@"\green").Append(color.G).Append(@"\blue").Append(color.B).Append(';');

    [GeneratedRegex(@"<!--.*?(?:-->|\z)|<\s*script\b[^>]*>.*?(?:</\s*script\s*>|\z)|<\s*style\b[^>]*>.*?(?:</\s*style\s*>|\z)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking)]
    private static partial Regex HiddenHtml();

    [GeneratedRegex(@"<\s*(?:br\s*/?|/p|/div|/li)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"</?[a-zA-Z](?:[^'""<>]|'[^']*'|""[^""]*"")*(?:>|\z)|<![^>]*(?:>|\z)",
        RegexOptions.NonBacktracking)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"^(#{1,6})\s+(.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"\s+#+$")]
    private static partial Regex ClosingHashes();

    [GeneratedRegex(@"^([-*+]|\d{1,9}[.)])\s+(.+)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"[\x60](?<code>[^\x60]+)[\x60]|\*\*(?<strong>.+?)\*\*|__(?<underscore>.+?)__|\[(?<label>[^\]]+)\]\([^\s)]+\)",
        RegexOptions.NonBacktracking)]
    private static partial Regex InlineToken();
}
