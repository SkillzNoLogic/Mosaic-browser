using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Mosaic.Core.Browser;
using Mosaic.Core.Productivity;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Mosaic.App.Features.Productivity;

internal static class Clipboard
{
    public static void CopyText(string text)
    {
        var package = new DataPackage();
        package.SetText(text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
    }
}

/// <summary>Re-encodes a downloaded site icon with Windows' imaging codecs (PNG, ICO, JPEG, GIF, BMP, WebP…).</summary>
internal static class IconImaging
{
    private const int MaxSide = 128;

    public static async Task<byte[]?> ReencodeAsync(byte[] data)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            await input.WriteAsync(data.AsBuffer());
            input.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(input);
            var (width, height) = (decoder.PixelWidth, decoder.PixelHeight);
            if (width < 16 || height < 16) return null;
            var scale = Math.Min(1.0, (double)MaxSide / Math.Max(width, height));
            var (outWidth, outHeight) = ((uint)Math.Max(1, Math.Round(width * scale)), (uint)Math.Max(1, Math.Round(height * scale)));
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform { ScaledWidth = outWidth, ScaledHeight = outHeight, InterpolationMode = BitmapInterpolationMode.Fant },
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, outWidth, outHeight, 96, 96, pixels.DetachPixelData());
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            output.Seek(0);
            await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
            return bytes;
        }
        catch (Exception)
        {
            return null;  // not an image Windows can read
        }
    }
}

/// <summary>Palette entries for Mosaic's own pages (every <see cref="FeatureMounts.InternalPages"/> host).</summary>
internal static class InternalPagesPalette
{
    private static readonly Dictionary<string, (string Title, string Icon)> Known = new()
    {
        ["bookmarks"] = ("Bookmarks", "book"),
        ["history"] = ("History", "clock"),
        ["downloads"] = ("Downloads", "arrow.down.circle"),
        ["readinglist"] = ("Reading List", "eyeglasses"),
        ["newtab"] = ("New Tab Page", "square.grid.3x3.square"),
    };

    public static IEnumerable<PaletteAction> Actions(BrowserModel model)
    {
        foreach (var host in FeatureMounts.InternalPages.Keys.Order(StringComparer.Ordinal).ToList())
        {
            var (title, icon) = Known.TryGetValue(host, out var known)
                ? known
                : (char.ToUpperInvariant(host[0]) + host[1..], "doc");
            yield return new PaletteAction($"page.{host}", $"Open {title}", () => ProductivityIntegration.OpenInternalPage(model, host))
            {
                Keywords = new[] { "show", "library", "mosaic://" + host },
                Icon = icon,
                Kind = PaletteKind.Page,
            };
        }
    }
}
