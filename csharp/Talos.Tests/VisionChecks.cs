using Talos.Core.Vision;

namespace Talos.Tests;

internal static class VisionChecks
{
    public static void Register(Checks suite)
    {
        suite.Add("vision.crop preserves RGBA coordinates and original pixels", () =>
        {
            var image = Pattern(8, 8);
            var original = image.Rgba.ToArray();
            var crop = PixelOperations.Crop(image, new(.25, .25, .5, .5));
            Check.Equal(4, crop.Width); Check.Equal(4, crop.Height);
            Check.True(crop.Rgba[..4].SequenceEqual(image.Rgba[((2 * 8 + 2) * 4)..][..4]));
            crop.Rgba[0] ^= 255;
            Check.True(original.SequenceEqual(image.Rgba), "Crop aliased/mutated source screenshot");
        });
        suite.Add("vision.crop rejects out of range and nonfinite geometry", () =>
        {
            foreach (var rect in new[] { new NormalizedRect(-.01, 0, 1, 1), new(0, 0, 0, 1), new(.8, 0, .3, 1), new(double.NaN, 0, 1, 1), new(0, 0, double.PositiveInfinity, 1) })
                Check.Throws<ArgumentException>(() => PixelOperations.Crop(Pattern(8, 8), rect));
        });
        suite.Add("vision.rejects truncated pixels and oversized dimensions before allocation", () =>
        {
            Check.Throws<ArgumentException>(() => PixelOperations.Validate(new(2, 2, new byte[15])));
            Check.Throws<ArgumentException>(() => PixelOperations.Validate(new(int.MaxValue, int.MaxValue, [])));
            Check.Throws<ArgumentOutOfRangeException>(() => PixelOperations.Resize(Pattern(2, 2), int.MaxValue, int.MaxValue));
        });
        suite.Add("vision.resize is bilinear including alpha", () =>
        {
            var image = new PixelImage(2, 2, [0,0,0,0, 100,100,100,100, 100,100,100,100, 200,200,200,200]);
            var result = PixelOperations.Resize(image, 1, 1);
            Check.True(result.Rgba.SequenceEqual(new byte[] { 100, 100, 100, 100 }));
        });
        suite.Add("vision.match detects texture and rejects uniform/transparent evidence", () =>
        {
            var pattern = Pattern(40, 40);
            Check.Near(1, PixelOperations.Match(pattern, pattern), 1e-8);
            Check.Near(0, PixelOperations.Match(Solid(40, 40, 90), Solid(40, 40, 90)));
            var transparent = pattern with { Rgba = pattern.Rgba.ToArray() };
            for (var i = 3; i < transparent.Rgba.Length; i += 4) transparent.Rgba[i] = 0;
            Check.Near(0, PixelOperations.Match(transparent, transparent));
        });
        suite.Add("vision.unconfirmed legacy calibration never authorizes recognition", () =>
        {
            var recognizer = new ScreenshotRecognizer([new("faces", "elephant", Pattern(40, 40))]);
            var result = recognizer.Recognize(Pattern(400, 400), VisionCalibration.CreateLegacyLayout());
            Check.True(result.RequiresManualReview);
            Check.Equal(16, result.Rooms.Count);
            Check.True(result.Rooms.All(room => room.Unknown && room.Species is null && room.Tier is null));
            Check.True(result.Issues.Count > 0);
        });
        suite.Add("vision.blank screenshot stays unknown after layout confirmation", () =>
        {
            var recognizer = new ScreenshotRecognizer([new("faces", "elephant", Pattern(40, 40))]);
            var calibration = VisionCalibration.CreateLegacyLayout() with { LayoutConfirmed = true };
            var result = recognizer.Recognize(Solid(400, 400, 80), calibration);
            Check.True(result.RequiresManualReview);
            Check.True(result.Rooms.Where(room => room.Index != 0).All(room => room.Unknown && room.Species is null && room.Tier is null));
        });
        suite.Add("vision.exact texture alias is mammoth while missing tier remains unknown", () =>
        {
            var (image, calibration, template) = SyntheticBoard(false);
            var result = new ScreenshotRecognizer([new("faces", "elephant", template)]).Recognize(image, calibration);
            Check.True(result.RequiresManualReview);
            Check.True(result.Rooms.Skip(1).All(room => room.Species == "mammoth" && room.Tier is null && room.Unknown));
        });
        suite.Add("vision.frame color prefill still requires manual review", () =>
        {
            var (image, calibration, template) = SyntheticBoard(true);
            var result = new ScreenshotRecognizer([new("faces", "mammoth", template)]).Recognize(image, calibration);
            Check.True(result.RequiresManualReview);
            Check.True(result.Rooms.Skip(1).All(room => room.Species == "mammoth" && room.Tier == 4 && !room.Unknown));
        });
        suite.Add("vision.changed aspect ratio or duplicate centers invalidates calibration", () =>
        {
            var (image, calibration, template) = SyntheticBoard(false);
            var recognizer = new ScreenshotRecognizer([new("faces", "elephant", template)]);
            var wrongSize = calibration with { ReferenceWidth = 320, ReferenceHeight = 160 };
            Check.True(recognizer.Recognize(image, wrongSize).Rooms.All(room => room.Unknown));
            var centers = calibration.RoomCenters.ToArray(); centers[2] = centers[1];
            Check.True(recognizer.Recognize(image, calibration with { RoomCenters = centers }).Rooms.All(room => room.Unknown));
        });
    }

    internal static PixelImage Pattern(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var value = (byte)((x * 37 + y * 61 + (x / 3 % 2) * 90) % 256);
            var offset = (y * width + x) * 4;
            pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = value;
            pixels[offset + 3] = 255;
        }
        return new(width, height, pixels);
    }
    internal static PixelImage Solid(int width, int height, byte gray)
    {
        var pixels = new byte[checked(width * height * 4)];
        for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = pixels[i + 1] = pixels[i + 2] = gray; pixels[i + 3] = 255; }
        return new(width, height, pixels);
    }
    private static (PixelImage Image, VisionCalibration Calibration, PixelImage Template) SyntheticBoard(bool redFrames)
    {
        const int side = 160;
        var image = Solid(side, side, 20); var pattern = Pattern(16, 16);
        var centers = Enumerable.Range(0, 16).Select(index => new NormalizedPoint((index % 4 + .5) / 4, (index / 4 + .5) / 4)).ToArray();
        foreach (var center in centers)
        {
            var left = (int)Math.Round(center.X * side) - 8; var top = (int)Math.Round(center.Y * side) - 8;
            for (var y = 0; y < 16; y++) Array.Copy(pattern.Rgba, y * 16 * 4, image.Rgba, ((top + y) * side + left) * 4, 16 * 4);
            if (!redFrames) continue;
            foreach (var offset in new[] { -.82, .57 })
            {
                var x1 = (int)Math.Round((center.X + offset * .1) * side);
                var x2 = (int)Math.Round((center.X + (offset + .25) * .1) * side);
                var y1 = (int)Math.Round((center.Y - .061) * side); var y2 = (int)Math.Round((center.Y + .061) * side);
                for (var y = y1; y < y2; y++) for (var x = x1; x < x2; x++)
                { var p = (y * side + x) * 4; image.Rgba[p] = 230; image.Rgba[p + 1] = 20; image.Rgba[p + 2] = 20; }
            }
        }
        return (image, new() { RoomCenters = centers, PortraitWidth = .1, PortraitHeight = .1, LayoutConfirmed = true, ReferenceWidth = side, ReferenceHeight = side }, pattern);
    }
}
