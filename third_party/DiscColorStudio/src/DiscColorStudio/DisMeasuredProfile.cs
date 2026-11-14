using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiscColorStudio;

/// <summary>DIS self-measured profiles stay separate from the pinned 95058 data.</summary>
internal static class DisMeasuredProfile
{
    internal static JsonObject Read(string path, Geometry? geometry = null)
    {
        JsonObject data = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        JsonNode calibration = data["disCalibration"] ?? throw new InvalidDataException("不是 DIS 自建实测色表。");
        bool experimental = calibration["experimental"]?.GetValue<bool>() == true;
        string status = experimental ? "experimental-unvalidated" : "software-checks-passed";
        if (calibration["schema"]?.GetValue<string>() != "dis-dvd-measured-profile-v1"
            || calibration["validationStatus"]?.GetValue<string>() != status
            || (experimental && calibration["standardValidationPassed"]?.GetValue<bool>() != false))
            throw new InvalidDataException("色表没有通过照片覆盖率和独立混色检查。");
        string report = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "quality.json");
        if (!File.Exists(report) || DisWorker.Hash(report) != calibration["reportSha256"]!.GetValue<string>())
            throw new InvalidDataException("色表的质量报告缺失或校验失败。");
        JsonNode quality = JsonNode.Parse(File.ReadAllText(report))!;
        if (quality["status"]?.GetValue<string>() != status
            || (experimental && (quality["experimental"]?.GetValue<bool>() != true
                                 || quality["standardValidationPassed"]?.GetValue<bool>() != false)))
            throw new InvalidDataException("色表的质量报告没有通过检查。");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string measurements = Path.Combine(directory, "measurements.json"), chart = Path.Combine(directory, "chart.json");
        if (!File.Exists(measurements) || !File.Exists(chart)
            || DisWorker.Hash(measurements) != calibration["sessionSha256"]!.GetValue<string>()
            || DisWorker.Hash(chart) != calibration["chartSha256"]!.GetValue<string>())
            throw new InvalidDataException("色表的不可变取样记录或纹理清单缺失、校验失败。");
        JsonNode evidence = JsonNode.Parse(File.ReadAllText(measurements))!;
        if (evidence["schema"]?.GetValue<string>() != "dis-dvd-color-session-v1"
            || evidence["chartSha256"]?.GetValue<string>() != calibration["chartSha256"]?.GetValue<string>())
            throw new InvalidDataException("色表的取样记录与纹理清单不匹配。");
        if ((evidence["synthetic"]?.GetValue<bool>() == true) != (calibration["synthetic"]?.GetValue<bool>() == true))
            throw new InvalidDataException("色表与取样记录的软件测试标记不一致。");
        var measuredChart = new DisTextureChart(chart);
        var textureData = data["textures"]!.AsArray();
        if (textureData.Count != 1154 || textureData.Where((t, i) =>
            t!["motif"]!.GetValue<string>() != measuredChart.Plan["textures"]![i]!["motif"]!.GetValue<string>()).Any())
            throw new InvalidDataException("色表纹理与原始标定盘清单不一致。");
        if (geometry is not null)
        {
            Geometry measured = calibration["geometry"]!.Deserialize<Geometry>(Design.Json)!;
            if (measured.Sectors != geometry.Sectors || Math.Abs(measured.InnerRadiusMm - geometry.InnerRadiusMm) > 1e-8
                || Math.Abs(measured.OuterRadiusMm - geometry.OuterRadiusMm) > 1e-8
                || Math.Abs(measured.PitchLinear - geometry.PitchLinear) > 1e-12
                || Math.Abs(measured.PitchQuadratic - geometry.PitchQuadratic) > 1e-12
                || Math.Abs(measured.PitchCubic - geometry.PitchCubic) > 1e-12)
                throw new InvalidDataException("实测色表与当前 DVD 盘面几何不匹配。");
        }
        _ = new ContinuousColorMap(File.ReadAllBytes(path), ContinuousColorMap.ReadLut(path));
        return data;
    }

    internal static void Attach(Design design, string path)
    {
        JsonObject data = Read(path, design.Geometry);
        byte[] profile = File.ReadAllBytes(path), lut = ContinuousColorMap.ReadLut(path);
        design.AngleColor = new AngleColorSettings
        {
            ProfileSha256 = Store.Hash(profile), LutSha256 = Store.Hash(lut),
            WindowStartDeg = data["windowStartDeg"]!.GetValue<double>(),
            WindowEndDeg = data["windowEndDeg"]!.GetValue<double>(),
        };
        design.AngleProfileAssets = new() { [AngleColorSettings.ProfileName] = profile, [AngleColorSettings.LutName] = lut };
        // Transparent image areas must not inherit the 95058 legacy background color.
        var map = new ContinuousColorMap(profile, lut);
        double beta = (design.AngleColor.WindowStartDeg + design.AngleColor.WindowEndDeg) / 2;
        var candidates = Enumerable.Range(0, map.Data.Textures.Length)
            .Select(i => (Id: i, Response: map.ResponseAt(i, beta))).Where(p => p.Response is not null).ToArray();
        var background = candidates.FirstOrDefault(p => p.Id == 1152);
        if (background.Response is null) background = candidates.OrderBy(p => ContinuousColorMap.Y(p.Response!.Value)).First();
        Color display = map.Display(background.Response!.Value);
        int id = Enumerable.Range(1, 254).Last(i => design.Palette.All(s => s.Id != i));
        design.Palette.Add(new Swatch { Id = id, Name = "Measured background", Motif = map.Data.Textures[background.Id].Motif,
            CameraRgb = [display.R, display.G, display.B] });
        design.AllowedIds.Add(id); design.BackgroundId = id; design.MatchGain = 1;
    }

    internal static void Snapshot(JsonObject plan, string directory)
    {
        string? source = plan["configuration"]?["profilePath"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(source)) return;
        JsonObject data = Read(source);
        string target = Path.Combine(directory, "measured");
        Directory.CreateDirectory(target);
        string sourceRoot = Path.GetDirectoryName(Path.GetFullPath(source))!;
        File.Copy(source, Path.Combine(target, "profile.json"), overwrite: false);
        File.Copy(Path.Combine(sourceRoot, data["lutFile"]!.GetValue<string>()),
            Path.Combine(target, data["lutFile"]!.GetValue<string>()), overwrite: false);
        File.Copy(Path.Combine(sourceRoot, "quality.json"), Path.Combine(target, "quality.json"), overwrite: false);
        File.Copy(Path.Combine(sourceRoot, "measurements.json"), Path.Combine(target, "measurements.json"), overwrite: false);
        File.Copy(Path.Combine(sourceRoot, "chart.json"), Path.Combine(target, "chart.json"), overwrite: false);
        plan["configuration"]!["profilePath"] = "measured/profile.json";
        plan["responseMode"] = data["disCalibration"]?["experimental"]?.GetValue<bool>() == true
            ? "dvd-experimental-measured-43533" : "dvd-self-measured-43533";
        plan["sourceMedia"] = data["disCalibration"]!["media"]!.DeepClone();
        plan["calibration"] = data["disCalibration"]!.DeepClone();
    }

    internal static string Resolve(JsonObject plan, string directory)
    {
        string relative = plan["configuration"]!["profilePath"]!.GetValue<string>();
        string path = Path.GetFullPath(Path.Combine(directory, relative));
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("色表路径必须在不可变计划目录内。");
        return path;
    }
}
