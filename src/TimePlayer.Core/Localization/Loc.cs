using System.Globalization;

namespace TimePlayer.Core.Localization;

/// <summary>
/// TimePlayer's translations. Text is written in English in the code and looked up here; anything
/// without a translation stays in English, so a missing entry never leaves a gap.
/// Counts go through <see cref="Plural(long, string, string)"/>, because Romanian has three plural
/// forms and Ukrainian three different ones ("1 відео", "2 відео", "5 відео" agree differently).
/// </summary>
public static class Loc
{
    /// <summary>Language codes TimePlayer speaks, with their own names; "" means "as Windows".</summary>
    public static IReadOnlyList<(string Code, string Name)> Languages { get; } = new[]
    {
        ("", "Automatic (Windows language)"),
        ("en", "English"),
        ("ro", "Română"),
        ("uk", "Українська"),
    };

    private static IReadOnlyDictionary<string, string>? _table;

    /// <summary>The language in use: "en", "ro" or "uk".</summary>
    public static string Current { get; private set; } = "en";

    /// <summary>
    /// Number and date formatting for translated text only. The app's own culture is left alone:
    /// numbers sent to mpv must keep a dot ("1.25"), which a Romanian or Ukrainian culture would change.
    /// </summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>Chooses the language; null or "" follows Windows, falling back to English.</summary>
    public static void Use(string? code)
    {
        var chosen = string.IsNullOrWhiteSpace(code) ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName : code.Trim().ToLowerInvariant();
        Current = chosen switch
        {
            "ro" => "ro",
            "uk" => "uk",
            _ => "en"
        };
        _table = Current switch
        {
            "ro" => Romanian.Table,
            "uk" => Ukrainian.Table,
            _ => null
        };
        Culture = CultureInfo.GetCultureInfo(Current switch { "ro" => "ro-RO", "uk" => "uk-UA", _ => "en-US" });
    }

    /// <summary>The translation of an English text.</summary>
    public static string T(string english) =>
        _table is not null && _table.TryGetValue(english, out var translated) && translated.Length > 0 ? translated : english;

    /// <summary>Translates a format string ("Skipped {0}.") and fills it in.</summary>
    public static string T(string englishFormat, params object?[] args) =>
        string.Format(Culture, T(englishFormat), args);

    /// <summary>
    /// A count with the right noun form: <c>Plural(n, "{0} video", "{0} videos")</c>. Translations hold
    /// their forms separated by "|" in the language's order (Romanian: one|few|other, Ukrainian: one|few|many).
    /// </summary>
    public static string Plural(long count, string englishOne, string englishOther)
    {
        var key = englishOne + "|" + englishOther;
        string form;
        if (_table is not null && _table.TryGetValue(key, out var translated) && translated.Length > 0)
        {
            var forms = translated.Split('|');
            form = forms[Math.Min(PluralIndex(count), forms.Length - 1)];
        }
        else
        {
            form = count == 1 ? englishOne : englishOther;
        }
        return string.Format(Culture, form, count);
    }

    /// <summary>Which plural form a count takes in the current language.</summary>
    public static int PluralIndex(long count)
    {
        var n = Math.Abs(count);
        switch (Current)
        {
            case "ro":
                // 1 videoclip; 2-19 videoclipuri (and 0, 101-119, ...); 20+ de videoclipuri.
                if (n == 1) return 0;
                if (n == 0 || (n % 100 >= 1 && n % 100 <= 19)) return 1;
                return 2;
            case "uk":
                // 1, 21, 31 відео; 2-4, 22-24 …; 5-20, 25-30 …
                if (n % 10 == 1 && n % 100 != 11) return 0;
                if (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) return 1;
                return 2;
            default:
                return n == 1 ? 0 : 1;
        }
    }

    /// <summary>The YouTube interface language to ask for (search results, titles where translated, captions).</summary>
    public static string YouTubeLanguage => Current;
}
