using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoidCrewModLauncher.Core;

/// <summary>Reading and writing the launcher's own JSON files.</summary>
public static class JsonFiles
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The bridge protocol: camelCase, one object per line, nulls left out.</summary>
    public static readonly JsonSerializerOptions Protocol = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Reads a JSON file: null when it isn't there, or when its content isn't valid (a copy is kept as .broken and the
    /// caller starts from defaults). A file that can't be opened (held by antivirus or a sync tool) is tried a few times
    /// and then throws, so the caller never mistakes it for a missing file and overwrites it with defaults.
    /// </summary>
    public static T? Load<T>(string path) where T : class => Load<T>(path, 5, TimeSpan.FromMilliseconds(200));

    internal static T? Load<T>(string path, int attempts, TimeSpan wait) where T : class
    {
        for (int attempt = 1; ; attempt++)
        {
            if (!File.Exists(path)) return null;
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<T>(stream, Options);
            }
            catch (JsonException e)
            {
                AppLog.Error($"Could not read {path}; starting from defaults", e);
                try
                {
                    File.Copy(path, path + ".broken", true);
                }
                catch
                {
                    // Keeping a copy of the broken file is only a courtesy.
                }
                return null;
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // Deleted between the check and the open.
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= attempts) throw new IOException(Loc.F("Could not open {0}: {1}", path, e.Message), e);
                Thread.Sleep(wait);
            }
        }
    }

    /// <summary>Writes through a temporary file, flushed to disk before it replaces the old one, so a crash or power cut never leaves half a file behind.</summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, value, options ?? Options);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }
}
