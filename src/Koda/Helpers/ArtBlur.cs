using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Koda.Core;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Koda.Helpers;

/// <summary>
/// The blurred cover behind Now Playing. The old backdrop decoded the art at 24
/// pixels and stretched it, which on a large monitor reads as a flat wash: there
/// is no structure left to see. This decodes a real thumbnail, runs a three-pass
/// box blur (a Gaussian to the eye) and hands XAML a 384px source, so the cover's
/// shapes and colours survive the softening.
///
/// The blur runs on the UI thread after the download and decode; at this size it
/// costs a few milliseconds, and the result is cached per artwork URL.
/// </summary>
public static class ArtBlur
{
    private const int Pixels = 384;
    private const int Radius = 20;
    private const int CacheLimit = 16;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Dictionary<string, SoftwareBitmapSource> Cache = new(StringComparer.Ordinal);

    /// <summary>The blurred cover, or null when the art cannot be read.</summary>
    public static async Task<SoftwareBitmapSource?> FromAsync(Artwork? artwork)
    {
        if (artwork is null)
        {
            return null;
        }

        var key = artwork.Resized(640).ToString();
        if (Cache.TryGetValue(key, out var known))
        {
            return known;
        }

        try
        {
            var bytes = await Http.GetByteArrayAsync(key);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);
            var pixels = (await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                new BitmapTransform { ScaledWidth = Pixels, ScaledHeight = Pixels },
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();

            Blur(pixels, Pixels, Pixels, Radius);

            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(), BitmapPixelFormat.Bgra8, Pixels, Pixels, BitmapAlphaMode.Premultiplied);
            var source = new SoftwareBitmapSource();
            await source.SetBitmapAsync(bitmap);

            if (Cache.Count >= CacheLimit)
            {
                Cache.Clear();
            }

            Cache[key] = source;
            return source;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Three box passes, horizontal then vertical: a cheap Gaussian.</summary>
    private static void Blur(byte[] pixels, int width, int height, int radius)
    {
        var temp = new byte[pixels.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Horizontal(pixels, temp, width, height, radius);
            Vertical(temp, pixels, width, height, radius);
        }
    }

    private static void Horizontal(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var y = 0; y < height; y++)
        {
            var row = y * width * 4;
            int blue = 0, green = 0, red = 0;
            for (var x = -radius; x <= radius; x++)
            {
                var i = row + Math.Clamp(x, 0, width - 1) * 4;
                blue += source[i];
                green += source[i + 1];
                red += source[i + 2];
            }

            for (var x = 0; x < width; x++)
            {
                var o = row + x * 4;
                target[o] = (byte)(blue / window);
                target[o + 1] = (byte)(green / window);
                target[o + 2] = (byte)(red / window);
                target[o + 3] = 255;

                var add = row + Math.Clamp(x + radius + 1, 0, width - 1) * 4;
                var remove = row + Math.Clamp(x - radius, 0, width - 1) * 4;
                blue += source[add] - source[remove];
                green += source[add + 1] - source[remove + 1];
                red += source[add + 2] - source[remove + 2];
            }
        }
    }

    private static void Vertical(byte[] source, byte[] target, int width, int height, int radius)
    {
        var window = radius * 2 + 1;
        for (var x = 0; x < width; x++)
        {
            var column = x * 4;
            int blue = 0, green = 0, red = 0;
            for (var y = -radius; y <= radius; y++)
            {
                var i = Math.Clamp(y, 0, height - 1) * width * 4 + column;
                blue += source[i];
                green += source[i + 1];
                red += source[i + 2];
            }

            for (var y = 0; y < height; y++)
            {
                var o = y * width * 4 + column;
                target[o] = (byte)(blue / window);
                target[o + 1] = (byte)(green / window);
                target[o + 2] = (byte)(red / window);
                target[o + 3] = 255;

                var add = Math.Clamp(y + radius + 1, 0, height - 1) * width * 4 + column;
                var remove = Math.Clamp(y - radius, 0, height - 1) * width * 4 + column;
                blue += source[add] - source[remove];
                green += source[add + 1] - source[remove + 1];
                red += source[add + 2] - source[remove + 2];
            }
        }
    }

    private static IBuffer AsBuffer(this byte[] bytes) =>
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(bytes);
}
