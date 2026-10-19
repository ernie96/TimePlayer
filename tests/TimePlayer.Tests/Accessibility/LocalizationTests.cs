using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using TimePlayer.Core.Accessibility;
using TimePlayer.Core.Localization;
using Xunit;

namespace TimePlayer.Tests.Accessibility;

/// <summary>Loc is app-wide; these tests change the language, so they run on their own.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public class LanguageCollection
{
}

[Collection("Language")]
public class LocalizationTests : IDisposable
{
    public void Dispose() => Loc.Use("en");

    private static IReadOnlyDictionary<string, string> Table(string className) =>
        (IReadOnlyDictionary<string, string>)typeof(Loc).Assembly
            .GetType($"TimePlayer.Core.Localization.{className}", throwOnError: true)!
            .GetField("Table", BindingFlags.Public | BindingFlags.Static)!
            .GetValue(null)!;

    [Theory]
    [InlineData("ro", 1, 0)]
    [InlineData("ro", 2, 1)]
    [InlineData("ro", 19, 1)]
    [InlineData("ro", 20, 2)]
    [InlineData("ro", 101, 1)]
    [InlineData("ro", 0, 1)]
    [InlineData("uk", 1, 0)]
    [InlineData("uk", 21, 0)]
    [InlineData("uk", 11, 2)]
    [InlineData("uk", 3, 1)]
    [InlineData("uk", 23, 1)]
    [InlineData("uk", 13, 2)]
    [InlineData("uk", 5, 2)]
    [InlineData("en", 1, 0)]
    [InlineData("en", 2, 1)]
    [InlineData("es", 1, 0)]
    [InlineData("es", 0, 1)]
    [InlineData("es", 21, 1)]
    public void PluralRules(string language, long count, int form)
    {
        Loc.Use(language);
        Assert.Equal(form, Loc.PluralIndex(count));
    }

    [Fact]
    public void Counts_ReadNaturally_InEachLanguage()
    {
        Loc.Use("ro");
        Assert.Equal("1 videoclip", Loc.Plural(1, "{0} video", "{0} videos"));
        Assert.Equal("5 videoclipuri", Loc.Plural(5, "{0} video", "{0} videos"));
        Assert.Equal("25 de videoclipuri", Loc.Plural(25, "{0} video", "{0} videos"));
        Assert.Equal("acum 3 ani", AccessibleTextFormatter.TranslateAge("3 years ago"));

        Loc.Use("uk");
        Assert.Equal("1 канал", Loc.Plural(1, "{0} channel", "{0} channels"));
        Assert.Equal("3 канали", Loc.Plural(3, "{0} channel", "{0} channels"));
        Assert.Equal("5 каналів", Loc.Plural(5, "{0} channel", "{0} channels"));
        Assert.Equal("2 хвилини, 1 секунда", AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(121)));
        Assert.Equal("Пропущено «Song»: воно недоступне.", Loc.T("Skipped {0}: {1}.", "Song", Loc.T("it isn't available")));

        Loc.Use("es");
        Assert.Equal("1 video", Loc.Plural(1, "{0} video", "{0} videos"));
        Assert.Equal("25 videos", Loc.Plural(25, "{0} video", "{0} videos"));
        Assert.Equal("hace 3 años", AccessibleTextFormatter.TranslateAge("3 years ago"));
        Assert.Equal("2 minutos, 1 segundo", AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(121)));

        Loc.Use("en");
        Assert.Equal("2 minutes, 1 second", AccessibleTextFormatter.FormatTimeSpoken(TimeSpan.FromSeconds(121)));
    }

    [Fact]
    public void MissingTranslations_FallBackToEnglish()
    {
        Loc.Use("uk");
        Assert.Equal("Something new nobody translated", Loc.T("Something new nobody translated"));
        Assert.Equal("4 widgets", Loc.Plural(4, "{0} widget", "{0} widgets"));
    }

    [Theory]
    [InlineData("Romanian")]
    [InlineData("Ukrainian")]
    [InlineData("Spanish")]
    public void EveryTranslation_KeepsItsPlaceholders_AndFormats(string language)
    {
        var placeholder = new Regex(@"\{(\d+)(?:[,:][^}]*)?\}");
        foreach (var (english, translated) in Table(language))
        {
            if (english.Contains('|') && translated.Split('|').Length is 2 or 3)
            {
                foreach (var form in translated.Split('|'))
                    _ = string.Format(CultureInfo.InvariantCulture, form, 7);
                continue;
            }
            var wanted = placeholder.Matches(english).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
            var got = placeholder.Matches(translated).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
            Assert.True(wanted.SequenceEqual(got), $"{language}: placeholders differ for \"{english}\"");
            var args = Enumerable.Range(0, 5).Select(i => (object)i).ToArray();
            _ = string.Format(CultureInfo.InvariantCulture, translated, args);
        }
    }

    [Fact]
    public void EveryLanguage_TranslatesTheSameTexts()
    {
        var ro = Table("Romanian").Keys.ToHashSet();
        foreach (var other in new[] { "Ukrainian", "Spanish" })
        {
            var keys = Table(other).Keys.ToHashSet();
            Assert.Empty(ro.Except(keys));
            Assert.Empty(keys.Except(ro));
        }
        Assert.True(ro.Count > 600);
    }

    /// <summary>New English text in the code needs Romanian and Ukrainian too; this finds any that doesn't.</summary>
    [Fact]
    public void EveryTextInTheCode_HasBothTranslations()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "TimePlayer.sln")))
            root = root.Parent;
        Assert.NotNull(root);
        var src = Path.Combine(root!.FullName, "src");

        static string Unescape(string s) => Regex.Unescape(s);
        var used = new HashSet<string>();
        var single = new Regex(@"(?<![\w.])T\(""((?:[^""\\]|\\.)*)""\s*[,)]");
        var plural = new Regex(@"(?<![\w.])Plural\([^,]+,\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)""\)");
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.EndsWith(".Strings.cs", StringComparison.Ordinal))
                continue;
            var text = File.ReadAllText(file);
            foreach (Match m in single.Matches(text))
                used.Add(Unescape(m.Groups[1].Value));
            foreach (Match m in plural.Matches(text))
                used.Add(Unescape(m.Groups[1].Value) + "|" + Unescape(m.Groups[2].Value));
        }
        var xaml = new Regex(@"\{l:T '((?:[^'\\]|\\.)*)'\}");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(src, "TimePlayer.Wpf", "Views"), "*.xaml"))
        {
            foreach (Match m in xaml.Matches(File.ReadAllText(file)))
                used.Add(System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Replace("\\'", "'")));
        }
        used.RemoveWhere(k => Regex.IsMatch(k, @"^(Control|Ctrl|Shift)\+\w+$") || !Regex.IsMatch(k, "[A-Za-z]{2}"));

        Assert.True(used.Count > 500, $"only {used.Count} texts found; the scan is broken");
        foreach (var language in new[] { "Romanian", "Ukrainian", "Spanish" })
        {
            var missing = used.Where(k => !Table(language).ContainsKey(k)).OrderBy(k => k).ToList();
            Assert.True(missing.Count == 0, $"{language} is missing {missing.Count}: " + string.Join(" | ", missing.Take(10)));
        }
    }

    /// <summary>Speech voices for one alphabet often read another as nothing, so each language is also named in the current one.</summary>
    [Fact]
    public void LanguageChoices_AreNamedSoEveryVoiceCanReadThem()
    {
        Loc.Use("en");
        var english = TimePlayer.Wpf.ViewModels.MainViewModel.BuildLanguageChoices().Select(c => c.Label).ToList();
        Assert.Equal(new[] { "Automatic (Windows language)", "English", "Română (Romanian)", "Українська (Ukrainian)", "Español (Spanish)" }, english);

        Loc.Use("uk");
        var ukrainian = TimePlayer.Wpf.ViewModels.MainViewModel.BuildLanguageChoices().Select(c => c.Label).ToList();
        Assert.Contains("Українська", ukrainian);
        Assert.Contains("English (англійська)", ukrainian);
        Assert.Equal(5, ukrainian.Count);

        Loc.Use("es");
        Assert.Contains("Español", BuildLanguageChoicesLabels());
    }

    private static List<string> BuildLanguageChoicesLabels() =>
        TimePlayer.Wpf.ViewModels.MainViewModel.BuildLanguageChoices().Select(c => c.Label).ToList();

    [Fact]
    public void Language_FollowsWindows_WhenSetToAutomatic()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("uk-UA");
            Loc.Use("");
            Assert.Equal("uk", Loc.Current);
            CultureInfo.CurrentUICulture = new CultureInfo("es-MX");
            Loc.Use("");
            Assert.Equal("es", Loc.Current);
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");
            Loc.Use(null);
            Assert.Equal("en", Loc.Current); // not translated yet: English
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
