using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VoidCrewModLauncher.Core.Thunderstore;

namespace VoidCrewModLauncher.Infrastructure;

/// <summary>Package icons: downloaded once into the cache, decoded small, shared between rows.</summary>
public sealed class Icons
{
    private readonly ThunderstoreClient _thunderstore;
    private readonly ConcurrentDictionary<string, ImageSource?> _loaded = new();

    public Icons(ThunderstoreClient thunderstore)
    {
        _thunderstore = thunderstore;
    }

    public async Task<ImageSource?> GetAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (_loaded.TryGetValue(url, out ImageSource? cached)) return cached;
        string? file = await _thunderstore.GetIconAsync(url);
        ImageSource? image = file == null ? null : await Task.Run(() => Decode(file));
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
        catch (Exception e) when (e is NotSupportedException or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
