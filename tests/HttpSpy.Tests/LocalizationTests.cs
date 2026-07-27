using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using HttpSpy.Core.Localization;
using Xunit;

namespace HttpSpy.Tests;

/// <summary>
/// The interface shipped English-only. These cover the catalogue itself — most
/// usefully, that the two languages cannot drift apart and that every key the
/// XAML asks for actually exists, which is the failure mode that would show up
/// as raw keys on screen.
/// </summary>
public class LocalizationTests : IDisposable
{
    private readonly AppLanguage _original = Loc.Current.Language;

    public void Dispose() => Loc.Current.Language = _original;

    [Fact]
    public void Every_english_key_has_a_russian_translation()
    {
        var missing = Loc.EnglishCatalogue.Keys.Where(k => !Loc.HasRussian(k)).ToList();
        Assert.True(missing.Count == 0, "No Russian translation for: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_russian_key_is_orphaned()
    {
        var orphans = Loc.RussianCatalogue.Keys.Where(k => !Loc.EnglishCatalogue.ContainsKey(k)).ToList();
        Assert.True(orphans.Count == 0, "Russian keys with no English original: " + string.Join(", ", orphans));
    }

    [Fact]
    public void No_translation_is_left_empty()
    {
        Assert.All(Loc.EnglishCatalogue, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
        Assert.All(Loc.RussianCatalogue, kv => Assert.False(string.IsNullOrWhiteSpace(kv.Value), kv.Key));
    }

    [Fact]
    public void Russian_strings_are_actually_translated()
    {
        // A copy-pasted English string in the Russian table is the easiest way to
        // "complete" a translation without doing it. Ignore keys where the same
        // text is genuinely correct in both (symbols, protocol names, URL).
        var sameInBoth = new HashSet<string>
        {
            "Toolbar.More", "Column.Url", "Column.Index", "Menu.Https",
            // Protocol and format names, an example URL, and "#" — translating
            // these would be wrong, not thorough.
            "Insp.Hex", "Insp.Json", "Insp.WebSocket", "Insp.Sse", "Insp.Grpc",
            "Insp.Id", "Insp.Index", "Dash.Https", "Sub.UrlPlaceholder",
        };

        var untranslated = Loc.RussianCatalogue
            .Where(kv => !sameInBoth.Contains(kv.Key))
            .Where(kv => kv.Value == Loc.EnglishCatalogue[kv.Key])
            .Select(kv => kv.Key)
            .ToList();

        Assert.True(untranslated.Count == 0, "Identical to English: " + string.Join(", ", untranslated));
    }

    [Fact]
    public void Switching_language_changes_the_strings()
    {
        Loc.Current.Language = AppLanguage.English;
        Assert.Equal("Capture", Loc.Current["Tab.Capture"]);

        Loc.Current.Language = AppLanguage.Russian;
        Assert.Equal("Захват", Loc.Current["Tab.Capture"]);
    }

    [Fact]
    public void Switching_language_notifies_bindings()
    {
        Loc.Current.Language = AppLanguage.English;

        var changed = new List<string?>();
        Loc.Current.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Loc.Current.Language = AppLanguage.Russian;

        // "Item[]" is what makes every localized binding on screen re-evaluate.
        Assert.Contains("Item[]", changed);
    }

    [Fact]
    public void Setting_the_same_language_twice_does_not_notify()
    {
        Loc.Current.Language = AppLanguage.Russian;

        int notifications = 0;
        Loc.Current.PropertyChanged += (_, _) => notifications++;
        Loc.Current.Language = AppLanguage.Russian;

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void An_unknown_key_renders_as_itself_rather_than_blank()
    {
        // A blank label looks like a layout bug; the key looks like what it is.
        Assert.Equal("Nope.NotAKey", Loc.Current["Nope.NotAKey"]);
    }

    [Fact]
    public void An_empty_key_is_empty()
    {
        Assert.Equal("", Loc.Current[""]);
    }

    [Fact]
    public void System_resolves_to_a_concrete_language()
    {
        Loc.Current.Language = AppLanguage.System;
        Assert.True(Loc.Current.Effective is AppLanguage.English or AppLanguage.Russian);
    }

    [Theory]
    [InlineData("en", AppLanguage.English)]
    [InlineData("EN", AppLanguage.English)]
    [InlineData("ru", AppLanguage.Russian)]
    [InlineData("Russian", AppLanguage.Russian)]
    [InlineData("system", AppLanguage.System)]
    [InlineData("", AppLanguage.System)]
    [InlineData(null, AppLanguage.System)]
    [InlineData("klingon", AppLanguage.System)]
    public void Persisted_language_codes_round_trip(string? code, AppLanguage expected)
    {
        Assert.Equal(expected, Loc.Parse(code));
    }

    [Fact]
    public void Language_codes_survive_a_save_and_load()
    {
        foreach (var language in new[] { AppLanguage.System, AppLanguage.English, AppLanguage.Russian })
            Assert.Equal(language, Loc.Parse(Loc.ToCode(language)));
    }

    [Fact]
    public void The_picker_offers_one_name_per_language()
    {
        Assert.Equal(Enum.GetValues<AppLanguage>().Length, Loc.LanguageNames.Count);
    }

    [Fact]
    public void Every_key_referenced_from_xaml_exists_in_the_catalogue()
    {
        // This is the test that stops a typo in markup rendering as a raw key on
        // screen, which is the whole reason unknown keys fall back to themselves.
        var root = FindRepositoryRoot();
        var views = Path.Combine(root, "src", "HttpSpy.App");
        Assert.True(Directory.Exists(views), $"view directory not found at {views}");

        var pattern = new Regex(@"\{l:T\s+([A-Za-z0-9_.]+)", RegexOptions.Compiled);
        var missing = new List<string>();

        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
            foreach (Match match in pattern.Matches(File.ReadAllText(file)))
            {
                var key = match.Groups[1].Value;
                if (!Loc.EnglishCatalogue.ContainsKey(key))
                    missing.Add($"{Path.GetFileName(file)}: {key}");
            }

        Assert.True(missing.Count == 0, "Keys used in markup but absent from the catalogue: " +
                                        string.Join(", ", missing));
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HttpSpy.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}
