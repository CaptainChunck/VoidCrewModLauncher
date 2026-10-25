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
    public static string T(string english)
    {
        string text = Translate(_table, english);
        Remember(text, english, null);
        return text;
    }

    /// <summary><see cref="T"/>, then string.Format with the current culture's number formats.</summary>
    public static string F(string english, params object?[] args)
    {
        string text = string.Format(CultureInfo.CurrentCulture, Translate(_table, english), args);
        Remember(text, english, args);
        return text;
    }

    // ---------------------------------------------------------------- exception messages in the English log

    /// <summary>The last text <see cref="T"/> or <see cref="F"/> made on this thread, and its English (see <see cref="InEnglish"/>).</summary>
    [ThreadStatic] private static string? _lastText;

    [ThreadStatic] private static string? _lastEnglish;

    [ThreadStatic] private static object?[]? _lastArgs;

    /// <summary>The key of an exception's English message in Exception.Data.</summary>
    private const string EnglishKey = "VoidCrewModLauncher.EnglishMessage";

    internal static void Remember(string text, string english, object?[]? args)
    {
        _lastText = text;
        _lastEnglish = english;
        _lastArgs = args;
    }

    /// <summary>
    /// Marks <paramref name="exception"/>, whose message <see cref="T"/> or <see cref="F"/> has just made on this thread,
    /// with that message in English, which the launcher's log (English, for whoever helps the player) writes instead: the
    /// player reads it in their language. Straight after the message is made: <c>throw new IOException(Loc.F(...)).InEnglish();</c>
    /// </summary>
    public static TException InEnglish<TException>(this TException exception) where TException : Exception
    {
        if (_lastText == null || _lastEnglish == null || exception.Message != _lastText) return exception;
        try
        {
            string english = _lastArgs == null ? _lastEnglish : string.Format(CultureInfo.InvariantCulture, _lastEnglish, _lastArgs);
            if (english != _lastText) exception.Data[EnglishKey] = english;
        }
        catch (FormatException)
        {
            // The log gets the message as the player read it rather than losing the exception.
        }
        return exception;
    }

    /// <summary>An exception's message as the log writes it: in English when <see cref="InEnglish"/> marked it.</summary>
    public static string LogMessage(Exception exception) => exception.Data[EnglishKey] as string ?? exception.Message;

    /// <summary>An exception as the log writes it (its type, messages and stack), with the marked messages in English.</summary>
    public static string LogText(Exception exception)
    {
        string text = exception.ToString();
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            Exception e = pending.Pop();
            if (e.Data[EnglishKey] is string english && e.Message.Length > 0) text = text.Replace(e.Message, english, StringComparison.Ordinal);
            if (e is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions) pending.Push(inner);
            }
            else if (e.InnerException != null)
            {
                pending.Push(e.InnerException);
            }
        }
        return text;
    }

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
