namespace Talos.Core.Vision;

/// <summary>Unpremultiplied, row-major RGBA pixels. The recognizer never writes to Rgba.</summary>
public sealed record PixelImage(int Width, int Height, byte[] Rgba);

public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>Normalized X/Y/width/height in the enclosing image or calibrated game ROI.</summary>
public readonly record struct NormalizedRect(double X, double Y, double Width, double Height)
{
    public static NormalizedRect Full => new(0, 0, 1, 1);
}

/// <summary>UI-decompressed local PNG template; the core performs no IO or PNG decoding.</summary>
public sealed record VisionTemplate(string Group, string Label, PixelImage Image);

public sealed record VisionCalibration
{
    public NormalizedRect Roi { get; init; } = NormalizedRect.Full;
    /// <summary>Exactly 16 centers, relative to Roi. Index zero is the office.</summary>
    public IReadOnlyList<NormalizedPoint> RoomCenters { get; init; } = Array.Empty<NormalizedPoint>();
    public bool LayoutConfirmed { get; init; }
    public int ReferenceWidth { get; init; }
    public int ReferenceHeight { get; init; }
    public double PortraitWidth { get; init; } = 0.027;
    public double PortraitHeight { get; init; } = 0.050;

    /// <summary>
    /// Candidate geometry recovered by static inspection of the user's earlier package.
    /// It must be confirmed or adjusted against the current screenshot before recognition.
    /// This is independently written C#, not recovered original source or an accuracy claim.
    /// </summary>
    public static VisionCalibration CreateLegacyLayout(NormalizedRect? roi = null) => new()
    {
        Roi = roi ?? NormalizedRect.Full,
        RoomCenters = new NormalizedPoint[]
        {
            new(.282,.132), new(.370,.132), new(.455,.132), new(.541,.132),
            new(.629,.132), new(.716,.132), new(.718,.258), new(.720,.387),
            new(.725,.523), new(.636,.523), new(.547,.523), new(.455,.523),
            new(.365,.523), new(.275,.523), new(.278,.387), new(.280,.258)
        },
        LayoutConfirmed = false
    };
}

public sealed record RoomRecognition
{
    public int Index { get; init; }
    public string? Species { get; init; }
    public int? Tier { get; init; }
    /// <summary>Image similarity, not a probability or measured confidence.</summary>
    public double Score { get; init; }
    public double SpeciesScore { get; init; }
    public double TierScore { get; init; }
    public bool Unknown { get; init; } = true;
    public string Message { get; init; } = "待人工确认。";
}

public sealed record VisionResult
{
    public IReadOnlyList<RoomRecognition> Rooms { get; init; } = Array.Empty<RoomRecognition>();
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
    /// <summary>Recognition can prefill suggestions but never authorize game advice.</summary>
    public bool RequiresManualReview => true;
}
