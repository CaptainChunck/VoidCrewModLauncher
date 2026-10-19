using System.Text;

namespace VoidCrewModLauncher.Core.Steam;

/// <summary>
/// A node of Valve's KeyValues text format (libraryfolders.vdf, appmanifest_*.acf): either a string value or a
/// block of named children. Key lookups ignore case, as Steam does.
/// </summary>
public sealed class VdfNode
{
    public string? Value { get; init; }
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? this[string key] => Children.TryGetValue(key, out VdfNode? node) ? node : null;

    public string? Get(string key) => this[key]?.Value;
}

/// <summary>Reads KeyValues text. Unknown syntax is skipped rather than thrown on; Steam's files are simple.</summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int position = 0;
        var root = new VdfNode();
        ReadBlock(text, ref position, root);
        return root;
    }

    public static VdfNode? Load(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e)
        {
            AppLog.Warn($"Could not read {path}: {e.Message}");
            return null;
        }
    }

    private static void ReadBlock(string text, ref int position, VdfNode block)
    {
        while (true)
        {
            string? key = NextToken(text, ref position, out bool isBrace);
            if (key == null) return;
            if (isBrace)
            {
                if (key == "}") return;
                continue; // A stray "{" without a key.
            }

            string? value = NextToken(text, ref position, out bool valueIsBrace);
            if (value == null) return;
            if (valueIsBrace && value == "{")
            {
                var child = new VdfNode();
                ReadBlock(text, ref position, child);
                block.Children[key] = child;
            }
            else if (!valueIsBrace)
            {
                block.Children[key] = new VdfNode { Value = value };
            }
            else
            {
                return; // "}" right after a key.
            }
        }
    }

    private static string? NextToken(string text, ref int position, out bool isBrace)
    {
        isBrace = false;
        while (position < text.Length)
        {
            char c = text[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }
            if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
            {
                while (position < text.Length && text[position] != '\n') position++;
                continue;
            }
            if (c == '{' || c == '}')
            {
                position++;
                isBrace = true;
                return c.ToString();
            }
            if (c == '"')
            {
                position++;
                var token = new StringBuilder();
                while (position < text.Length && text[position] != '"')
                {
                    if (text[position] == '\\' && position + 1 < text.Length)
                    {
                        position++;
                        char escaped = text[position];
                        token.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                    }
                    else
                    {
                        token.Append(text[position]);
                    }
                    position++;
                }
                position++; // Closing quote.
                return token.ToString();
            }

            // Unquoted token.
            int start = position;
            while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] != '{' && text[position] != '}' && text[position] != '"')
            {
                position++;
            }
            return text[start..position];
        }
        return null;
    }
}
