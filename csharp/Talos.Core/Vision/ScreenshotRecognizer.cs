namespace Talos.Core.Vision;

/// <summary>
/// Conservative, independently implemented local template prefill. It does not run
/// an OCR model, load files, infer sealed versus empty rooms, or confirm a GameState.
/// </summary>
public sealed class ScreenshotRecognizer
{
    private sealed record PreparedTemplate(string Species, double[] Descriptor);
    private readonly PreparedTemplate[] templates;
    private readonly string[] templateIssues;

    public ScreenshotRecognizer(IReadOnlyList<VisionTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (templates.Count > 512) throw new ArgumentException("模板数量不能超过512个。", nameof(templates));
        var prepared = new List<PreparedTemplate>();
        var problems = new List<string>();
        foreach (var template in templates)
        {
            if (template is null || !string.Equals(template.Group, "faces", StringComparison.OrdinalIgnoreCase)) continue;
            var species = NormalizeSpecies(template.Label);
            if (species is null) { problems.Add("忽略了不支持的兽种模板。"); continue; }
            try
            {
                var descriptor = PixelOperations.Descriptor(template.Image);
                if (descriptor.All(x => x == 0)) { problems.Add("忽略了缺少纹理的兽种模板。"); continue; }
                prepared.Add(new PreparedTemplate(species, descriptor));
            }
            catch (ArgumentException) { problems.Add("忽略了像素数据损坏的兽种模板。"); }
        }
        this.templates = prepared.ToArray();
        templateIssues = problems.Distinct().ToArray();
    }

    public VisionResult Recognize(PixelImage screenshot, VisionCalibration calibration)
    {
        var issues = new List<string>(templateIssues)
        {
            "本地模板仅预填兽种和边框品质建议，全部结果需要人工核对；相似度不是准确率或概率。",
            "天数、当前位置、目的地、刑具、事件、标记与操作阶段均未自动读取；剩余天数不能直接作为当前天数。"
        };
        try { PixelOperations.Validate(screenshot); }
        catch (ArgumentException error) { return Failed(issues, error.Message); }
        var problem = ValidateCalibration(calibration, screenshot);
        if (problem is not null) return Failed(issues, problem);
        if (templates.Length == 0) return Failed(issues, "没有可用兽种模板，请确认程序内的 templates 文件夹完整。");
        if (calibration.ReferenceWidth == 0 || calibration.ReferenceHeight == 0)
            issues.Add("当前校准未记录参考截图尺寸，请检查本次布局并重新保存校准。");

        var rooms = new List<RoomRecognition>(16)
        {
            new() { Index = 0, Unknown = false, Message = "校准的审讯室位置；未识别典狱长是否在此。" }
        };
        for (var n = 1; n < 16; n++)
        {
            var center = calibration.RoomCenters[n];
            var match = MatchSpecies(screenshot, calibration, center);
            if (match.Species is null)
            {
                rooms.Add(new RoomRecognition
                {
                    Index = n, Score = match.Score, SpeciesScore = match.Score,
                    Message = "没有可靠匹配的兽头；请人工区分锁门、空房、鼠巢或囚犯。"
                });
                continue;
            }
            var tier = ReadTier(screenshot, calibration, center);
            rooms.Add(new RoomRecognition
            {
                Index = n, Species = match.Species, Tier = tier.Tier,
                Score = match.Score, SpeciesScore = match.Score, TierScore = tier.Score,
                Unknown = tier.Tier is null,
                Message = tier.Tier is null
                    ? "兽种仅为外观建议，品质边框不明确，请人工核对。"
                    : "兽种及品质均为待确认建议；装饰、光效和不同布局可能导致误判。"
            });
        }
        var unknownCount = rooms.Skip(1).Count(x => x.Unknown);
        if (unknownCount > 0) issues.Add($"{unknownCount}间牢房未能同时读取兽种和品质；未知不等同于空房或未开启。");
        return new VisionResult { Rooms = rooms, Issues = issues };
    }

    private static VisionResult Failed(List<string> issues, string reason)
    {
        issues.Add(reason);
        return new VisionResult
        {
            Rooms = Enumerable.Range(0, 16).Select(n => new RoomRecognition { Index = n, Message = reason }).ToArray(),
            Issues = issues
        };
    }

    private static string? ValidateCalibration(VisionCalibration? calibration, PixelImage image)
    {
        if (calibration is null || !calibration.LayoutConfirmed)
            return "尚未确认截图布局：请先框选完整游戏区域，并校准或逐一核对16个牢房中心。";
        if (!PixelOperations.IsValidRect(calibration.Roi)) return "游戏区域必须完整位于截图内，请重新框选。";
        if (calibration.RoomCenters is null || calibration.RoomCenters.Count != 16)
            return "请依次校准审讯室及15间牢房中心，共16个位置。";
        if (!double.IsFinite(calibration.PortraitWidth) || !double.IsFinite(calibration.PortraitHeight) ||
            calibration.PortraitWidth <= 0 || calibration.PortraitHeight <= 0 || calibration.PortraitWidth > .25 || calibration.PortraitHeight > .25)
            return "兽头采样宽高必须大于0且不超过游戏区域的25%。";
        for (var n = 0; n < 16; n++)
        {
            var point = calibration.RoomCenters[n];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.X > 1 || point.Y < 0 || point.Y > 1)
                return "牢房中心必须使用0至1的归一化坐标。";
            for (var j = 0; j < n; j++)
            {
                var other = calibration.RoomCenters[j];
                if (Math.Abs(point.X - other.X) + Math.Abs(point.Y - other.Y) < .00001)
                    return "牢房中心不能重复，请重新标定。";
            }
        }
        if (calibration.ReferenceWidth < 0 || calibration.ReferenceHeight < 0 ||
            (calibration.ReferenceWidth == 0) != (calibration.ReferenceHeight == 0))
            return "参考截图宽高无效，请重新保存校准。";
        if (calibration.ReferenceWidth > 0 && calibration.ReferenceHeight > 0)
        {
            var change = Math.Abs(((double)image.Width / image.Height) /
                ((double)calibration.ReferenceWidth / calibration.ReferenceHeight) - 1);
            if (change > .08) return "截图宽高比与校准时相差超过8%，请重新校准游戏区域和牢房中心。";
        }
        return null;
    }

    private (string? Species, double Score) MatchSpecies(PixelImage image, VisionCalibration calibration, NormalizedPoint point)
    {
        var bySpecies = new Dictionary<string, double>(StringComparer.Ordinal);
        // Small translations help with pixel rounding. They cannot compensate for
        // a different game layout: that still requires a confirmed calibration.
        foreach (var dx in new[] { -.07, 0, .07 })
        foreach (var dy in new[] { -.06, 0, .06 })
        {
            var local = new NormalizedRect(
                point.X - calibration.PortraitWidth / 2 + dx * calibration.PortraitWidth,
                point.Y - calibration.PortraitHeight / 2 + dy * calibration.PortraitHeight,
                calibration.PortraitWidth, calibration.PortraitHeight);
            if (!PixelOperations.IsValidRect(local)) continue;
            var patch = PixelOperations.Crop(image, InImage(calibration.Roi, local));
            var descriptor = PixelOperations.Descriptor(patch);
            foreach (var template in templates)
            {
                var score = PixelOperations.MatchDescriptors(descriptor, template.Descriptor);
                if (!bySpecies.TryGetValue(template.Species, out var current) || score > current)
                    bySpecies[template.Species] = score;
            }
        }
        var ordered = bySpecies.OrderByDescending(x => x.Value).ToArray();
        if (ordered.Length == 0) return (null, 0);
        var best = ordered[0];
        // When only one class exists, mere relative rank is no evidence of species.
        var minimum = ordered.Length == 1 ? .96 : .84;
        var gap = ordered.Length == 1 ? 1 : best.Value - ordered[1].Value;
        return best.Value >= minimum && gap >= .055 ? (best.Key, best.Value) : (null, best.Value);
    }

    private static (int? Tier, double Score) ReadTier(PixelImage image, VisionCalibration calibration, NormalizedPoint point)
    {
        // Frame proposals use narrow side strips outside the portrait. Values are
        // heuristic color evidence; absence of color does not imply ordinary tier.
        var w = calibration.PortraitWidth;
        var h = calibration.PortraitHeight;
        var strips = new[]
        {
            new NormalizedRect(point.X - w * .82, point.Y - h * .61, w * .25, h * 1.22),
            new NormalizedRect(point.X + w * .57, point.Y - h * .61, w * .25, h * 1.22)
        };
        var votes = new double[5];
        var sideVotes = new List<double[]>();
        foreach (var strip in strips)
        {
            if (!PixelOperations.IsValidRect(strip)) return (null, 0);
            var sample = PixelOperations.Resize(PixelOperations.Crop(image, InImage(calibration.Roi, strip)), 8, 36);
            var side = new double[5];
            for (var i = 0; i < sample.Rgba.Length; i += 4)
            {
                if (sample.Rgba[i + 3] < 230) continue;
                var r = sample.Rgba[i] / 255.0;
                var g = sample.Rgba[i + 1] / 255.0;
                var b = sample.Rgba[i + 2] / 255.0;
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var delta = max - min;
                var saturation = max <= 0 ? 0 : delta / max;
                if (saturation <= .20 && max >= .45) { side[0]++; continue; }
                if (saturation < .42 || max < .35) continue;
                double hue;
                if (max == r) hue = 60 * (((g - b) / delta) % 6);
                else if (max == g) hue = 60 * ((b - r) / delta + 2);
                else hue = 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
                var tier = hue < 15 || hue >= 345 ? 4 : hue >= 18 && hue < 65 ? 3 : hue >= 245 && hue <= 325 ? 2 : hue >= 175 && hue < 245 ? 1 : -1;
                if (tier >= 0) side[tier]++;
            }
            for (var i = 0; i < 5; i++) { side[i] /= 8 * 36; votes[i] += side[i] / 2; }
            sideVotes.Add(side);
        }
        var ordered = Enumerable.Range(0, 5).OrderByDescending(n => votes[n]).ToArray();
        var best = ordered[0];
        var coverage = votes[best];
        var chromatic = votes.Skip(1).Sum();
        var dominance = votes.Sum() <= 0 ? 0 : coverage / votes.Sum();
        var minimum = best == 0 ? .40 : .24;
        var consistent = sideVotes.All(v => v[best] >= (best == 0 ? .30 : .17));
        var unambiguous = dominance >= .68 && coverage - votes[ordered[1]] >= .12;
        if (coverage < minimum || !consistent || !unambiguous || best == 0 && chromatic > .08)
            return (null, Math.Clamp(coverage, 0, 1));
        return (best, Math.Clamp(coverage * dominance, 0, 1));
    }

    private static NormalizedRect InImage(NormalizedRect roi, NormalizedRect local) =>
        new(roi.X + local.X * roi.Width, roi.Y + local.Y * roi.Height, local.Width * roi.Width, local.Height * roi.Height);

    private static string? NormalizeSpecies(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "elephant" or "mammoth" => "mammoth",
        "rat" => "rat",
        "wolf" => "wolf",
        "lion" => "lion",
        _ => null
    };
}
