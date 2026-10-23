namespace VoidCrewModLauncher.Core.Thunderstore;

/// <summary>
/// Compares mod versions: Thunderstore's Major.Minor.Patch and BepInEx plugin versions (which may have a fourth part).
/// "1.2.0" and "1.2.0.0" are the same version. Anything that isn't numbers compares as text, after real versions.
/// </summary>
public static class VersionNumber
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    public static int Compare(string? a, string? b)
    {
        int[]? left = Parse(a);
        int[]? right = Parse(b);
        if (left == null || right == null)
        {
            if (left != null) return 1;
            if (right != null) return -1;
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }
        for (int i = 0; i < Math.Max(left.Length, right.Length); i++)
        {
            int x = i < left.Length ? left[i] : 0;
            int y = i < right.Length ? right[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public static bool Same(string? a, string? b) => Compare(a, b) == 0;

    /// <summary>"1.2", "1.2.3" or "1.2.3.4" (a "v" in front is fine): what's worth looking up when another player names it.</summary>
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 40) return false;
        string[] parts = text.Trim().TrimStart('v', 'V').Split('.');
        return parts.Length is >= 2 and <= 4 && parts.All(p => p.Length is > 0 and <= 9 && p.All(char.IsAsciiDigit));
    }

    private static int[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string trimmed = text.Trim().TrimStart('v', 'V');
        int dash = trimmed.IndexOfAny(new[] { '-', '+', ' ' });
        if (dash > 0) trimmed = trimmed[..dash];
        string[] parts = trimmed.Split('.');
        var numbers = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out numbers[i])) return null;
        }
        return numbers;
    }
}
