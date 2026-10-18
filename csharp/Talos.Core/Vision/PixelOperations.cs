namespace Talos.Core.Vision;

/// <summary>Platform-independent operations; no third-party graphics/runtime dependency.</summary>
public static class PixelOperations
{
    private const int MaxPixels = 24_000_000;

    public static void Validate(PixelImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > MaxPixels ||
            image.Rgba is null || (long)image.Rgba.Length != (long)image.Width * image.Height * 4)
            throw new ArgumentException("图像须为24百万像素以内的完整 RGBA 数据。", nameof(image));
    }

    public static bool IsValidRect(NormalizedRect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.X >= 0 && rect.Y >= 0 && rect.Width > 0 && rect.Height > 0 &&
        rect.X + rect.Width <= 1.0000000001 && rect.Y + rect.Height <= 1.0000000001;

    public static PixelImage Crop(PixelImage image, NormalizedRect rect)
    {
        Validate(image);
        if (!IsValidRect(rect)) throw new ArgumentException("裁剪范围必须位于图像内，坐标使用0至1的归一化值。", nameof(rect));
        var left = Math.Clamp((int)Math.Round(rect.X * image.Width), 0, image.Width - 1);
        var top = Math.Clamp((int)Math.Round(rect.Y * image.Height), 0, image.Height - 1);
        var right = Math.Clamp((int)Math.Round((rect.X + rect.Width) * image.Width), left + 1, image.Width);
        var bottom = Math.Clamp((int)Math.Round((rect.Y + rect.Height) * image.Height), top + 1, image.Height);
        var width = right - left;
        var height = bottom - top;
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
            Array.Copy(image.Rgba, ((top + y) * image.Width + left) * 4, pixels, y * width * 4, width * 4);
        return new PixelImage(width, height, pixels);
    }

    public static PixelImage Resize(PixelImage image, int width, int height)
    {
        Validate(image);
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new ArgumentOutOfRangeException(nameof(width), "缩放目标须为24百万像素以内的正尺寸。");
        var output = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++)
        {
            var sy = Math.Clamp((y + 0.5) * image.Height / height - 0.5, 0, image.Height - 1);
            var y0 = (int)sy;
            var y1 = Math.Min(y0 + 1, image.Height - 1);
            var fy = sy - y0;
            for (var x = 0; x < width; x++)
            {
                var sx = Math.Clamp((x + 0.5) * image.Width / width - 0.5, 0, image.Width - 1);
                var x0 = (int)sx;
                var x1 = Math.Min(x0 + 1, image.Width - 1);
                var fx = sx - x0;
                var a = (y0 * image.Width + x0) * 4;
                var b = (y0 * image.Width + x1) * 4;
                var c = (y1 * image.Width + x0) * 4;
                var d = (y1 * image.Width + x1) * 4;
                var dest = (y * width + x) * 4;
                for (var channel = 0; channel < 4; channel++)
                {
                    var upper = image.Rgba[a + channel] * (1 - fx) + image.Rgba[b + channel] * fx;
                    var lower = image.Rgba[c + channel] * (1 - fx) + image.Rgba[d + channel] * fx;
                    output[dest + channel] = (byte)Math.Clamp((int)Math.Round(upper * (1 - fy) + lower * fy), 0, 255);
                }
            }
        }
        return new PixelImage(width, height, output);
    }

    /// <summary>
    /// Brightness-normalized grayscale correlation in [0,1]. Blank images return zero,
    /// including a blank image compared with itself: no shape evidence is present.
    /// </summary>
    public static double Match(PixelImage first, PixelImage second)
    {
        var a = Descriptor(first);
        var b = Descriptor(second);
        return MatchDescriptors(a, b);
    }

    internal static double[] Descriptor(PixelImage image)
    {
        var small = Resize(image, 30, 36);
        var feature = new double[30 * 36];
        double mean = 0;
        for (var i = 0; i < feature.Length; i++)
        {
            var offset = i * 4;
            var alpha = small.Rgba[offset + 3] / 255.0;
            feature[i] = (0.299 * small.Rgba[offset] + 0.587 * small.Rgba[offset + 1] + 0.114 * small.Rgba[offset + 2]) * alpha / 255;
            mean += feature[i];
        }
        mean /= feature.Length;
        double norm = 0;
        for (var i = 0; i < feature.Length; i++)
        {
            feature[i] -= mean;
            norm += feature[i] * feature[i];
        }
        // Reject nearly flat regions rather than amplify compression/rounding noise.
        if (Math.Sqrt(norm / feature.Length) < 0.018) return new double[feature.Length];
        norm = Math.Sqrt(norm);
        for (var i = 0; i < feature.Length; i++) feature[i] /= norm;
        return feature;
    }

    internal static double MatchDescriptors(double[] a, double[] b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++) sum += a[i] * b[i];
        return Math.Clamp(sum, 0, 1);
    }
}
