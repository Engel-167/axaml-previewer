using System.Text.RegularExpressions;

namespace AxamlPreview.Services;

public enum PreviewTheme { None, Light, Dark }

/// <summary>
/// Applies the chosen theme variant to the preview. Avalonia 12 has no Design.RequestedThemeVariant, but the
/// designer adds the root's Design.DesignStyle to the preview window's styles, so a design-only style that sets the
/// window's RequestedThemeVariant does it for any root (UserControl, Window, …) without touching the file on disk.
/// </summary>
public static partial class XamlTheme
{
    public static string Apply(string xaml, PreviewTheme theme)
    {
        if (theme == PreviewTheme.None) return xaml;
        var root = RootStart().Match(xaml);
        if (!root.Success) return xaml;
        var name = root.Value[1..];
        var end = TagEnd(xaml, root.Index);
        if (end < 0) return xaml;

        // The root already has a design style of its own: leave it alone rather than break the file.
        if (xaml.Contains("<Design.DesignStyle", StringComparison.Ordinal) || DesignStyleAttribute().IsMatch(xaml[root.Index..end]))
            return xaml;

        var designStyle =
            $"<Design.DesignStyle><Style Selector=\":is(Window)\"><Setter Property=\"RequestedThemeVariant\" Value=\"{theme}\" /></Style></Design.DesignStyle>";
        if (xaml[end] == '/')
        {
            // <Root … /> → <Root …>style</Root>
            return xaml[..end] + ">" + designStyle + $"</{name}>" + xaml[(end + 2)..];
        }
        return xaml[..(end + 1)] + designStyle + xaml[(end + 1)..];
    }

    /// <summary>Index of the '>' (or the '/' of "/>") closing the tag that starts at <paramref name="start"/>, skipping quoted values.</summary>
    private static int TagEnd(string text, int start)
    {
        var quote = '\0';
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return text[i - 1] == '/' ? i - 1 : i;
        }
        return -1;
    }

    // The first element: skips the XML declaration, comments and processing instructions.
    [GeneratedRegex(@"(?<=^(?:\s|<\?.*?\?>|<!--.*?-->)*)<[\w:.]+", RegexOptions.Singleline)]
    private static partial Regex RootStart();

    [GeneratedRegex(@"\bDesign\.DesignStyle\s*=")]
    private static partial Regex DesignStyleAttribute();
}
