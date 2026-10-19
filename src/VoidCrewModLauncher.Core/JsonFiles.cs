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

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<T>(stream, Options);
        }
        catch (Exception e)
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
    }

    /// <summary>Writes through a temporary file so a crash never leaves half a file behind.</summary>
    public static void Save<T>(string path, T value, JsonSerializerOptions? options = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        using (FileStream stream = File.Create(temp))
        {
            JsonSerializer.Serialize(stream, value, options ?? Options);
        }
        File.Move(temp, path, true);
    }
}
