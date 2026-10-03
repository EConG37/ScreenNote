using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenNote;

/// <summary>Explicit, reversible text transforms. OCR text is never automatically reformatted.</summary>
public static class TextCleanup
{
    public static string NormalizeLineEndings(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string RemoveEmptyLines(string text) =>
        string.Join("\n", NormalizeLineEndings(text).Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)));

    public static string CleanSpaces(string text)
    {
        // Keep tabs and leading indentation (code/list layout); collapse excess spaces inside a line.
        return string.Join("\n", NormalizeLineEndings(text).Split('\n').Select(line =>
        {
            var indentationLength = line.TakeWhile(character => character is ' ' or '\t' or '\u3000').Count();
            var indentation = line[..indentationLength];
            var content = Regex.Replace(line[indentationLength..], "[ \\u00A0\\u3000]+", " ");
            content = Regex.Replace(content, @"(?<=[\u2E80-\u9FFF\uF900-\uFAFF\uFF01-\uFF60]) +(?=[\u2E80-\u9FFF\uF900-\uFAFF\uFF01-\uFF60])", "");
            return (indentation + content).TrimEnd(' ', '\u00A0', '\u3000');
        }));
    }

    public static string MergeLines(string text)
    {
        var lines = NormalizeLineEndings(text).Split('\n');
        var result = new StringBuilder();
        var inCodeFence = false;
        var previousProtected = false;
        var previousWasEmpty = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var isFence = line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal);
            var isProtected = inCodeFence || isFence || IsStructuredLine(rawLine);
            var isEmpty = line.Length == 0;

            if (result.Length > 0 || previousWasEmpty)
            {
                if (isEmpty || previousWasEmpty || isProtected || previousProtected)
                    result.Append('\n');
                else if (NeedsWordSeparator(result[^1], line[0]))
                    result.Append(' ');
            }

            result.Append(isProtected ? rawLine : line);
            if (isFence)
                inCodeFence = !inCodeFence;
            previousProtected = isProtected;
            previousWasEmpty = isEmpty;
        }

        return result.ToString();
    }

    private static bool IsStructuredLine(string line)
    {
        if (line.StartsWith('\t') || line.StartsWith("    ", StringComparison.Ordinal))
            return true;
        var trimmed = line.Trim();
        if (Regex.IsMatch(trimmed, @"^(?:[-*+•]\s|\d+[.)、]\s?|#{1,6}\s|>\s?|\|)"))
            return true;
        // Preserve common code lines even when OCR omitted their indentation.
        return Regex.IsMatch(trimmed, @";\s*$|^[{}]\s*;?$|^(?:using |namespace |(?:public|private|protected|internal)\s|(?:var|let|const)\s+\w+\s*=|(?:def|class|function)\s+|//|/\*|\*/)");
    }

    private static bool NeedsWordSeparator(char previous, char next)
    {
        if (IsCjk(previous) || IsCjk(next))
            return false;
        if (char.IsWhiteSpace(previous) || char.IsWhiteSpace(next) || previous == '-' || previous == '/')
            return false;
        return !("([{\"'".Contains(previous) || ",.;:!?)]}\"'".Contains(next));
    }

    private static bool IsCjk(char value) =>
        value is >= '\u2E80' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or >= '\uFE30' and <= '\uFE4F' or >= '\uFF01' and <= '\uFF60';
}
