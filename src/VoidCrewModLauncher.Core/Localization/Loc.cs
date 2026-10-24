using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace VoidCrewModLauncher.Core.Localization;

/// <summary>A language the launcher can show: its code (the translation file's name) and its name in itself.</summary>
public sealed record LauncherLanguage(string Code, string NativeName);

/// <summary>
/// The launcher's translations. The English text is the key: <c>Loc.T("SERVER BROWSER")</c> gives the translation in the
/// chosen language, or the English text when there is none. Translations are embedded resources Localization/&lt;code&gt;.json,
/// each one flat JSON object { "English text": "translation" }. Logs stay in English.
/// </summary>
public static class Loc
{
    public const string English = "en";

    /// <summary>The supported languages, English first.</summary>
    public static IReadOnlyList<LauncherLanguage> Languages { get; } = new LauncherLanguage[]
    {
        new("en", "English"),
        new("fr", "Français"),
        new("de", "Deutsch"),
        new("es", "Español"),
        new("it", "Italiano"),
        new("pl", "Polski"),
        new("pt-BR", "Português (Brasil)"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("zh-Hans", "简体中文"),
        new("ja", "日本語"),
        new("ko", "한국어"),
    };

    private static Dictionary<string, string> _table = new(StringComparer.Ordinal);

    /// <summary>The code of the language in use.</summary>
    public static string Current { get; private set; } = English;

    /// <summary>
    /// Switches to a language: <paramref name="code"/> is one of <see cref="Languages"/>, or null/empty for the Windows
    /// language (English when the launcher doesn't have it). Sets the UI culture, not the number and date formats.
    /// </summary>
    public static void Use(string? code)
    {
        string chosen = string.IsNullOrEmpty(code) ? Pick(CultureInfo.CurrentUICulture) : Supported(code) ?? English;
        Current = chosen;
        _table = chosen == English ? new Dictionary<string, string>(StringComparer.Ordinal) : Load(chosen);
        try
        {
            var culture = CultureInfo.GetCultureInfo(chosen);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // The launcher's own text is still translated.
        }
    }

    /// <summary>The supported language for a Windows culture: its exact name, then its language (pt is pt-BR, zh is zh-Hans).</summary>
    internal static string Pick(CultureInfo culture)
    {
        if (Supported(culture.Name) is { } exact) return exact;
        string language = culture.TwoLetterISOLanguageName;
        return language switch
        {
            "pt" => "pt-BR",
            "zh" => "zh-Hans",
            _ => Supported(language) ?? English,
        };
    }

    /// <summary>The code as <see cref="Languages"/> spells it, or null when it isn't supported.</summary>
    internal static string? Supported(string code) =>
        Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>The translation of <paramref name="english"/>, or the English text itself.</summary>
    public static string T(string english) => Translate(_table, english);

    /// <summary><see cref="T"/>, then string.Format with the current culture's number formats.</summary>
    public static string F(string english, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(english), args);

    internal static string Translate(IReadOnlyDictionary<string, string> table, string english) =>
        table.TryGetValue(english, out string? translated) && !string.IsNullOrEmpty(translated) ? translated : english;

    /// <summary>The embedded table for a language; empty (English) when it's missing or broken.</summary>
    internal static Dictionary<string, string> Load(string code)
    {
        try
        {
            using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Localization/{code}.json");
            if (stream == null)
            {
                AppLog.Warn($"No translation for {code}; the launcher is in English.");
                return new Dictionary<string, string>(StringComparer.Ordinal);
            }
            return Parse(stream);
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
        {
            AppLog.Warn($"The {code} translation is broken ({e.Message}); the launcher is in English.");
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary>Reads one translation file: a flat object of strings. Throws JsonException when it isn't one.</summary>
    internal static Dictionary<string, string> Parse(Stream stream)
    {
        var table = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? throw new JsonException("The file is empty.");
        return new Dictionary<string, string>(table, StringComparer.Ordinal);
    }
}
