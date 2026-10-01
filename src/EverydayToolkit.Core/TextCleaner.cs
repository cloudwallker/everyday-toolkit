namespace EverydayToolkit.Core;
public static class TextCleaner
{
    public static string Apply(string text, TextRule rule)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return rule switch
        {
            TextRule.Trim => text.Trim(),
            TextRule.RemoveBlankLines => string.Join('\n', text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line))),
            TextRule.DeduplicateLines => string.Join('\n', text.Split('\n').Distinct(StringComparer.Ordinal)),
            TextRule.JoinLines => text.Replace("\n", ""),
            TextRule.JoinWithSpaces => text.Replace('\n', ' '),
            TextRule.ToHalfWidth => string.Concat(text.Select(c => c == '\u3000' ? ' ' : c is >= '\uff01' and <= '\uff5e' ? (char)(c - 0xfee0) : c)),
            TextRule.ToFullWidth => string.Concat(text.Select(c => c == ' ' ? '\u3000' : c is >= '!' and <= '~' ? (char)(c + 0xfee0) : c)),
            _ => throw new ArgumentOutOfRangeException(nameof(rule))
        };
    }
}
