using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VoidCrewModLauncher.Core.Localization;

namespace VoidCrewModLauncher.Tests;

public class LocalizationTests
{
    [Fact]
    public void Missing_keys_fall_back_to_English()
    {
        var table = new Dictionary<string, string> { ["SERVER BROWSER"] = "NAVIGATEUR", ["Empty"] = "" };
        Assert.Equal("NAVIGATEUR", Loc.Translate(table, "SERVER BROWSER"));
        Assert.Equal("MOD MANAGER", Loc.Translate(table, "MOD MANAGER"));
        Assert.Equal("Empty", Loc.Translate(table, "Empty"));
    }

    [Fact]
    public void A_missing_language_is_English()
    {
        Assert.Empty(Loc.Load("xx-unknown"));
        Assert.Null(Loc.Supported("xx-unknown"));
        Assert.Equal("Ready.", Loc.T("Ready."));
    }

    [Fact]
    public void A_broken_file_throws_a_json_error_that_Load_catches()
    {
        using var broken = new MemoryStream(Encoding.UTF8.GetBytes("{ \"a\": 1 }"));
        Assert.ThrowsAny<JsonException>(() => Loc.Parse(broken));
        using var good = new MemoryStream(Encoding.UTF8.GetBytes("{ \"Ready.\": \"Prêt.\" }"));
        Assert.Equal("Prêt.", Loc.Parse(good)["Ready."]);
    }

    [Fact]
    public void F_formats_with_the_current_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Downloading Fleet 1,5 (2/3)", Loc.F("Downloading {0} {1:0.0} ({2}/{3})", "Fleet", 1.5, 2, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Theory]
    [InlineData("fr-FR", "fr")]
    [InlineData("fr", "fr")]
    [InlineData("pt-BR", "pt-BR")]
    [InlineData("pt-PT", "pt-BR")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-TW", "zh-Hans")]
    [InlineData("de-AT", "de")]
    [InlineData("uk-UA", "uk")]
    [InlineData("ko-KR", "ko")]
    [InlineData("nl-NL", "en")]
    [InlineData("en-GB", "en")]
    public void The_Windows_language_picks_a_supported_one(string culture, string expected)
    {
        Assert.Equal(expected, Loc.Pick(CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void The_languages_are_listed_in_order_with_English_first()
    {
        Assert.Equal(new[] { "en", "fr", "de", "es", "it", "pl", "pt-BR", "ru", "uk", "zh-Hans", "ja", "ko" }, Loc.Languages.Select(l => l.Code));
        foreach (LauncherLanguage language in Loc.Languages) CultureInfo.GetCultureInfo(language.Code);
    }

    [Fact]
    public void Every_Loc_call_has_a_literal_key()
    {
        var bad = new List<string>();
        foreach (string file in LocKeys.SourceFiles("*.cs"))
        {
            string text = LocKeys.WithoutComments(File.ReadAllText(file));
            foreach (Match call in Regex.Matches(text, @"\bLoc\.[TF]\((?>\s*)(?!@?"")"))
            {
                bad.Add($"{Path.GetFileName(file)}: {text.Substring(call.Index, Math.Min(60, text.Length - call.Index))}");
            }
        }
        Assert.Empty(bad);
    }

    [Fact]
    public void Keys_json_is_up_to_date()
    {
        List<string> keys = LocKeys.Scan();
        Assert.True(keys.Count > 100, $"Only {keys.Count} keys found.");
        Assert.True(File.Exists(LocKeys.KeysFile), "Run tools\\extract-strings.ps1 to make keys.json.");
        List<string> saved = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(LocKeys.KeysFile))!;
        Assert.True(keys.SequenceEqual(saved),
            "keys.json is out of date: run tools\\extract-strings.ps1. New: " + string.Join(" | ", keys.Except(saved).Take(10))
            + " Gone: " + string.Join(" | ", saved.Except(keys).Take(10)));
    }

    [Fact]
    public void Every_translation_file_has_every_key_with_the_same_placeholders()
    {
        List<string> keys = LocKeys.Scan();
        var problems = new List<string>();
        foreach (LauncherLanguage language in Loc.Languages.Skip(1))
        {
            string file = Path.Combine(LocKeys.Root, "src", "VoidCrewModLauncher.Core", "Localization", language.Code + ".json");
            if (!File.Exists(file)) continue;
            Dictionary<string, string> table;
            using (FileStream stream = File.OpenRead(file)) table = Loc.Parse(stream);
            Assert.Equal(table.Count, Loc.Load(language.Code).Count);
            foreach (string key in keys)
            {
                if (!table.TryGetValue(key, out string? translation) || string.IsNullOrWhiteSpace(translation))
                {
                    problems.Add($"{language.Code}: missing \"{key}\"");
                    continue;
                }
                if (!LocKeys.Placeholders(key).SequenceEqual(LocKeys.Placeholders(translation)))
                {
                    problems.Add($"{language.Code}: placeholders differ in \"{key}\" -> \"{translation}\"");
                }
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(40)));
    }

    [Fact]
    public void The_scanner_reads_every_kind_of_literal()
    {
        Assert.Equal(new[] { "a \"b\"\n", @"c\d ""e""" },
            LocKeys.KeysInCode("Loc.T(\"a \\\"b\\\"\\n\"); Loc.F(\n  @\"c\\d \"\"e\"\"\", x);"));
        Assert.Equal(new[] { "VOID CREW FOLDER", "REMOVE THE LAUNCHER'S LOADER", "{0} of {1} on", "<your name> Room", "A, B" },
            LocKeys.KeysInXaml("Text=\"{i:T  VOID CREW FOLDER }\" Content=\"{i:T 'REMOVE THE LAUNCHER\\'S LOADER'}\" "
                               + "StringFormat=\"{i:T '\\{0\\} of \\{1\\} on'}\" Tag=\"{i:T &lt;your name&gt; Room}\" X=\"{i:T 'A, B'}\""));
    }
}

/// <summary>
/// Finds every translation key in the source: Loc.T("...")/Loc.F("...") in src/**/*.cs and {i:T ...} in src/**/*.xaml.
/// tools/extract-strings.ps1 does the same to write keys.json.
/// </summary>
internal static class LocKeys
{
    public static string Root { get; } = FindRoot();

    public static string KeysFile => Path.Combine(Root, "src", "VoidCrewModLauncher.Core", "Localization", "keys.json");

    private static string FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VoidCrewModLauncher.slnx"))) return dir.FullName;
        }
        throw new DirectoryNotFoundException("The repository folder (with VoidCrewModLauncher.slnx) isn't above the test folder.");
    }

    public static IEnumerable<string> SourceFiles(string pattern)
    {
        string src = Path.Combine(Root, "src");
        string sep = Path.DirectorySeparatorChar.ToString();
        return Directory.EnumerateFiles(src, pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "obj" + sep) && !f.Contains(sep + "bin" + sep)
                        && !f.Contains(sep + "Localization" + sep + "Loc.cs") && !f.EndsWith("TExtension.cs", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);
    }

    /// <summary>All keys, sorted (ordinal), each once.</summary>
    public static List<string> Scan()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in SourceFiles("*.cs")) keys.UnionWith(KeysInCode(File.ReadAllText(file)));
        foreach (string file in SourceFiles("*.xaml")) keys.UnionWith(KeysInXaml(File.ReadAllText(file)));
        return keys.ToList();
    }

    /// <summary>The text without // comments (not inside strings; good enough for this code base: no URL-like // in Loc keys).</summary>
    public static string WithoutComments(string code) =>
        string.Join("\n", code.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : l));

    public static List<string> KeysInCode(string code)
    {
        var keys = new List<string>();
        code = WithoutComments(code);
        foreach (Match call in Regex.Matches(code, @"\bLoc\.[TF]\(\s*(@?)"""))
        {
            bool verbatim = call.Groups[1].Value == "@";
            var key = new StringBuilder();
            for (int i = call.Index + call.Length; i < code.Length; i++)
            {
                char c = code[i];
                if (verbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < code.Length && code[i + 1] == '"')
                        {
                            key.Append('"');
                            i++;
                            continue;
                        }
                        break;
                    }
                    key.Append(c);
                }
                else
                {
                    if (c == '"') break;
                    if (c != '\\')
                    {
                        key.Append(c);
                        continue;
                    }
                    char next = code[++i];
                    switch (next)
                    {
                        case 'n': key.Append('\n'); break;
                        case 'r': key.Append('\r'); break;
                        case 't': key.Append('\t'); break;
                        case '0': key.Append('\0'); break;
                        case 'u':
                            key.Append((char)Convert.ToInt32(code.Substring(i + 1, 4), 16));
                            i += 4;
                            break;
                        default: key.Append(next); break;
                    }
                }
            }
            keys.Add(key.ToString());
        }
        return keys;
    }

    /// <summary>
    /// {i:T text} values as WPF reads them: XML entities decoded; quoted ('...' or "...") or bare (trimmed, up to , or });
    /// backslash escapes the next character.
    /// </summary>
    public static List<string> KeysInXaml(string xaml)
    {
        var keys = new List<string>();
        foreach (Match m in Regex.Matches(xaml, @"\{i:T\s"))
        {
            int i = m.Index + m.Length;
            while (i < xaml.Length && char.IsWhiteSpace(xaml[i])) i++;
            var raw = new StringBuilder();
            char quote = xaml[i] is '\'' or '"' ? xaml[i++] : '\0';
            for (; i < xaml.Length; i++)
            {
                char c = xaml[i];
                if (c == '\\' && i + 1 < xaml.Length)
                {
                    raw.Append(xaml[++i]);
                    continue;
                }
                if (quote != '\0' ? c == quote : c is '}' or ',') break;
                raw.Append(c);
            }
            string key = WebUtility.HtmlDecode(raw.ToString());
            keys.Add(quote == '\0' ? key.Trim() : key);
        }
        return keys;
    }

    /// <summary>The {n...} placeholders of a format string, sorted; {{ and }} are not placeholders.</summary>
    public static List<string> Placeholders(string text) =>
        Regex.Matches(text.Replace("{{", "").Replace("}}", ""), @"\{\d+(,[^}:]*)?(:[^}]*)?\}").Select(m => m.Value).OrderBy(p => p, StringComparer.Ordinal).ToList();
}
