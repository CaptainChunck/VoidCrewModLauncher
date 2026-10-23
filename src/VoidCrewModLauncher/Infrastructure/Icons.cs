using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoidCrewModLauncher.Core;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>
/// Package icons: downloaded once into the cache, decoded small, shared between rows. A failed download is tried again a
/// minute later; a file that won't decode stays blank for the session.
/// </summary>
public sealed class Icons
{
    private readonly ThunderstoreClient _thunderstore;
    private readonly ConcurrentDictionary<string, ImageSource?> _loaded = new();
    private readonly ConcurrentDictionary<string, DateTime> _failed = new();
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    public Icons(ThunderstoreClient thunderstore)
    {
        _thunderstore = thunderstore;
    }

    public async Task<ImageSource?> GetAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_loaded.TryGetValue(url, out ImageSource? cached)) return cached;
        if (_failed.TryGetValue(url, out DateTime failed) && DateTime.UtcNow - failed < RetryAfter) return null;
        string? file = await _thunderstore.GetIconAsync(url);
        if (file == null)
        {
            _failed[url] = DateTime.UtcNow;
            return null;
        }
        _failed.TryRemove(url, out _);
        ImageSource? image = await Task.Run(() => Decode(file));
        _loaded[url] = image;
        return image;
    }

    private static ImageSource? Decode(string file)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.UriSource = new Uri(file, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException or FormatException
                                       or ArgumentException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // FileFormatException (a corrupt image) is a FormatException.
            AppLog.Warn($"Icon {file}: {e.Message}");
            return null;
        }
    }
}
