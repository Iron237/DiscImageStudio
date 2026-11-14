// DIS adapter for Neo-626/DiscColorStudio ef44b13694e9873fc47ea9e0040ec5af502995ae.
// GPL-3.0. The measured map, recipes and DVD encoder remain upstream code.
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DiscColorStudio;

internal static class DisWorker
{
    private const string PlanSchema = "dis-dvd-fullcolor-plan-v1";
    private const string ImageSchema = "dis-dvd-fullcolor-image-v1";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static void Emit(string message, double? fraction = null)
        => Console.WriteLine(JsonSerializer.Serialize(new { message, fraction }));

    internal static int Run(string path)
    {
        try
        {
            Ui.SetLanguage("zh-CN");
            JsonObject request = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            string operation = request["operation"]!.GetValue<string>();
            string result = operation switch
            {
                "preview" => Preview(request),
                "export" => Export(request),
                "verify-iso" => Verify(request),
                "encode-fragment" => Fragment(request),
                "validate-profile" => ValidateProfile(request),
                "test-profile-recipes" => TestProfileRecipes(request),
                _ => throw new ArgumentException("Unsupported DVD color operation."),
            };
            Emit("Completed: " + result, 1);
            return 0;
        }
        catch (Exception error)
        {
            Emit("ERROR: " + error.Message);
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    internal static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static double Number(JsonNode node, string key) => node[key]!.GetValue<double>();

    private static Design DesignFor(JsonObject plan, string directory, bool validateAssets)
    {
        JsonNode config = plan["configuration"]!;
        int samples = config["sampleSize"]!.GetValue<int>();
        if (!RasterGrid.Sizes.Contains(samples)) throw new InvalidDataException("Invalid DVD sample size.");
        var adjustments = new ImageAdjustments
        {
            Exposure = Number(config, "exposure") / 100, Brightness = Number(config, "brightness"),
            Contrast = Number(config, "contrast") / 100, BandingReduction = Number(config, "smoothing") / 100,
            AdaptiveMixing = Number(config, "mixing") / 100, RasterSize = samples,
            PreferDisDark = config["preferDark"]!.GetValue<bool>(),
        };
        adjustments.Validate();
        Design design = Design.Create(samples);
        design.Name = "DIS · DVD-R 43533 · DCS measured";
        design.ColourMethod = "measured";
        design.QuantizationMode = "adaptive";
        design.ColorModelVersion = 2;
        Geometry geometry = config["geometry"]!.Deserialize<Geometry>(Design.Json)!;
        CalibrationFiles.ApplyImport(design, geometry, "Current DIS DVD project geometry");
        if (!string.IsNullOrWhiteSpace(config["profilePath"]?.GetValue<string>()))
            DisMeasuredProfile.Attach(design, DisMeasuredProfile.Resolve(plan, directory));
        else AngleColorSettings.Attach(design);
        if (plan["profileSha256"] is { } profile && profile.GetValue<string>() != design.AngleColor!.ProfileSha256)
            throw new InvalidDataException("The plan uses another measured color profile. Regenerate the preview.");
        if (plan["lutSha256"] is { } lut && lut.GetValue<string>() != design.AngleColor!.LutSha256)
            throw new InvalidDataException("The plan uses another color LUT. Regenerate the preview.");
        string root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        foreach (JsonNode? node in plan["layers"]!.AsArray())
        {
            JsonNode layer = node!;
            string file = Path.GetFullPath(Path.Combine(directory, layer["file"]!.GetValue<string>()));
            if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Layer path escaped the immutable plan.");
            if (validateAssets && Hash(file) != layer["sha256"]!.GetValue<string>()) throw new InvalidDataException("An inherited design layer changed.");
            byte[] bytes = File.ReadAllBytes(file);
            using (var image = new MemoryStream(bytes))
            using (var bitmap = new Bitmap(image)) { if (bitmap.Width == 0) throw new InvalidDataException("Empty layer."); }
            string id = layer["id"]!.GetValue<string>();
            design.Assets.Add(id, bytes);
            Layer item = new()
            {
                Id = id, AssetId = id, Name = layer["name"]!.GetValue<string>(),
                X = Number(layer, "x"), Y = Number(layer, "y"), Width = Number(layer, "width"),
                Height = Number(layer, "height"), Rotation = Number(layer, "rotation"),
                AngleColorEnabled = true, QuantizationMode = "adaptive",
            };
            adjustments.Apply(item);
            if (!config["autoViewing"]!.GetValue<bool>()) item.AngleCenterBeta = Number(config, "centerBeta");
            design.Layers.Add(item);
        }
        if (design.Layers.Count == 0) throw new InvalidDataException("The design has no visible layers.");
        design.Validate();
        return design;
    }

    private static string Preview(JsonObject request)
    {
        string directory = Path.GetFullPath(request["directory"]!.GetValue<string>());
        Directory.CreateDirectory(directory);
        string destination = Path.Combine(directory, "plan.json");
        if (File.Exists(destination)) throw new IOException("A preview plan already exists here.");
        JsonObject plan = new()
        {
            ["schema"] = PlanSchema, ["kind"] = "image", ["responseMode"] = "dvd-measured-95058-trial-on-43533",
            ["configuration"] = request["configuration"]!.DeepClone(), ["layers"] = request["layers"]!.DeepClone(),
            ["designSource"] = request["designSource"]!.DeepClone(),
            ["sourceRevision"] = "ef44b13694e9873fc47ea9e0040ec5af502995ae",
            ["sourceMedia"] = "Verbatim 95058 DVD-R 16x, burned at 4x",
            ["targetMedia"] = "Verbatim 43533 DVD-R; color transfer not physically validated",
        };
        foreach (JsonNode? layer in plan["layers"]!.AsArray())
            layer!["sha256"] = Hash(Path.Combine(directory, layer["file"]!.GetValue<string>()));
        DisMeasuredProfile.Snapshot(plan, directory);
        Design design = DesignFor(plan, directory, true);
        plan["profileSha256"] = design.AngleColor!.ProfileSha256;
        plan["lutSha256"] = design.AngleColor.LutSha256;
        using Renderer renderer = new();
        using AngleColorEngine colors = new();
        Emit("DCS: measured angular LUT, continuous recipes and per-turn mixing", .1);
        var viewing = colors.Plans(design, renderer);
        plan["viewing"] = JsonSerializer.SerializeToNode(viewing, Design.Json);
        int size = 1024;
        using (Bitmap source = renderer.Composite(design, size)) source.Save(Path.Combine(directory, "target.png"), ImageFormat.Png);
        Emit("DCS: rendering the same recipe used by DVD encoding", .5);
        using (Bitmap prediction = colors.Render(design, renderer, size, new Rectangle(0, 0, size, size), 0))
            prediction.Save(Path.Combine(directory, "predicted.png"), ImageFormat.Png);
        SaveJson(destination, plan);
        return destination;
    }

    private static (JsonObject Plan, Design Design, string Path) LoadPlan(JsonObject request)
    {
        string path = Path.GetFullPath(request["planPath"]!.GetValue<string>());
        JsonObject plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (plan["schema"]?.GetValue<string>() != PlanSchema) throw new InvalidDataException("Not a DIS DVD full-color plan.");
        return (plan, DesignFor(plan, Path.GetDirectoryName(path)!, true), path);
    }

    private static string Export(JsonObject request)
    {
        string requestedPlan = Path.GetFullPath(request["planPath"]!.GetValue<string>());
        if (JsonNode.Parse(File.ReadAllText(requestedPlan))!["schema"]?.GetValue<string>() == DisTextureChart.Schema)
        {
            DisTextureChart chart = new(requestedPlan);
            int workers = Math.Clamp(request["workers"]?.GetValue<int>() ?? Environment.ProcessorCount, 1, 64);
            Emit("DVD chart CPU workers: " + workers, .01);
            return ExportFile(request, chart.Plan, requestedPlan, (path, progress) => chart.Generate(path, progress, workers));
        }
        if (request["allowMediaTransfer"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("Confirm using the 95058 color table for a 43533 trial first.");
        var (plan, design, planPath) = LoadPlan(request);
        if (plan["calibration"]?["synthetic"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("软件合成测试色表不能导出实盘镜像。请使用真实照片建立色表。");
        DvdImageEncoder.ParallelismOverride = Math.Clamp(request["workers"]?.GetValue<int>() is > 0 and var n ? n : Environment.ProcessorCount, 1, 64);
        Emit("DVD encoding CPU workers: " + DvdImageEncoder.ParallelismOverride, .01);
        return ExportFile(request, plan, planPath, (path, progress) => DvdImageEncoder.Generate(design, path, progress));
    }

    private static string ExportFile(JsonObject request, JsonObject plan, string planPath, Func<string, IProgress<EncodingProgress>, EncodedImage> generate)
    {
        string planHash = Hash(planPath);
        if (!JsonNode.DeepEquals(plan, JsonNode.Parse(File.ReadAllText(planPath))))
            throw new InvalidDataException("导出计划在读取过程中改变，请重新打开原计划。");
        string output = Path.GetFullPath(request["outputPath"]!.GetValue<string>());
        if (!output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("A complete DVD image must use .iso.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using FileStream owner = new(output + ".export.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        bool overwrite = request["overwrite"]?.GetValue<bool>() == true;
        if (File.Exists(output) && !overwrite) throw new IOException("Confirm replacement or choose another output name.");
        string pending = output + ".pending-dcs.json";
        if (File.Exists(pending))
        {
            JsonObject previous = JsonNode.Parse(File.ReadAllText(pending))!.AsObject();
            if (previous["planSha256"]?.GetValue<string>() != planHash)
                throw new InvalidDataException("A completed image for another plan is pending here. Choose another name.");
            Publish(output, previous, pending, overwrite);
            return output;
        }
        string staged = output + ".building-" + Guid.NewGuid().ToString("N") + ".iso";
        EncodedImage result = generate(staged, new CallbackProgress(p => Emit(p.Message, p.Fraction)));
        JsonObject metadata = new()
        {
            ["schema"] = ImageSchema, ["planSha256"] = planHash,
            ["imageSha256"] = result.Sha256, ["bytes"] = result.Bytes,
            ["designSource"] = plan["designSource"]?.DeepClone(),
            ["targetMedia"] = plan["targetMedia"]!.DeepClone(), ["physicalColorValidated"] = false,
            ["stagedFile"] = Path.GetFileName(staged), ["noFilesystem"] = true,
        };
        SaveJson(staged + ".json", metadata);
        SaveJson(pending, metadata);
        if (Hash(planPath) != planHash)
            throw new InvalidDataException("计划在导出期间改变。已完成数据已保留；恢复完全相同的原计划后可重试提交。");
        Publish(output, metadata, pending, overwrite);
        return output;
    }

    private static void Publish(string output, JsonObject metadata, string pending, bool overwrite)
    {
        string name = metadata["stagedFile"]!.GetValue<string>();
        if (Path.GetFileName(name) != name || !name.StartsWith(Path.GetFileName(output) + ".building-", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid staged image path.");
        string staged = Path.Combine(Path.GetDirectoryName(output)!, name);
        long bytes = metadata["bytes"]!.GetValue<long>();
        string hash = metadata["imageSha256"]!.GetValue<string>();
        try
        {
            if (File.Exists(staged))
            {
                if (new FileInfo(staged).Length != bytes || Hash(staged) != hash) throw new InvalidDataException("Staged image verification failed.");
                Retry(() => File.Move(staged, output, overwrite));
            }
            else if (!File.Exists(output) || new FileInfo(output).Length != bytes || Hash(output) != hash)
                throw new InvalidDataException("The pending completed image is unavailable.");
            SaveJson(output + ".json", metadata);
        }
        catch
        {
            Emit("Publication is blocked; completed data and the pending record are retained. Close applications using the ISO and export the identical plan to this path again.");
            throw;
        }
        try { File.Delete(pending); File.Delete(staged + ".json"); }
        catch (IOException) { Emit("Image and metadata are complete; an obsolete recovery record is still busy."); }
        catch (UnauthorizedAccessException) { Emit("Image and metadata are complete; an obsolete recovery record could not be removed."); }
    }

    private static string Verify(JsonObject request)
    {
        string planPath = Path.GetFullPath(request["planPath"]!.GetValue<string>());
        JsonObject plan = JsonNode.Parse(File.ReadAllText(planPath))!.AsObject();
        if (plan["calibration"]?["synthetic"]?.GetValue<bool>() == true)
            throw new InvalidOperationException("软件合成测试色表不能用于实盘镜像。");
        long sectors = plan["schema"]?.GetValue<string>() == DisTextureChart.Schema
            ? new DisTextureChart(planPath).Geometry.Sectors : LoadPlan(request).Design.Geometry.Sectors;
        string output = Path.GetFullPath(request["outputPath"]!.GetValue<string>());
        if (!output.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Select a completed ISO.");
        JsonNode meta = JsonNode.Parse(File.ReadAllText(output + ".json"))!;
        if (meta["schema"]?.GetValue<string>() != ImageSchema || meta["planSha256"]?.GetValue<string>() != Hash(planPath))
            throw new InvalidDataException("This DVD color image belongs to another plan.");
        long length = checked(sectors * 2048);
        if (meta["bytes"]!.GetValue<long>() != length || new FileInfo(output).Length != length)
            throw new InvalidDataException("The DVD image is incomplete.");
        Emit("Verifying the complete DVD image SHA-256", .1);
        if (Hash(output) != meta["imageSha256"]!.GetValue<string>()) throw new InvalidDataException("DVD image SHA-256 failed.");
        return output;
    }

    private static string Fragment(JsonObject request)
    {
        string planPath = Path.GetFullPath(request["planPath"]!.GetValue<string>());
        if (JsonNode.Parse(File.ReadAllText(planPath))!["schema"]?.GetValue<string>() == DisTextureChart.Schema)
        {
            string file = request["outputPath"]!.GetValue<string>();
            if (!file.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Diagnostic fragments must use .bin.");
            using FileStream target = new(file, FileMode.CreateNew);
            new DisTextureChart(planPath).Write(target, request["startSector"]!.GetValue<uint>(), request["sectors"]!.GetValue<uint>(),
                null, request["workers"]?.GetValue<int>() ?? 1);
            return file;
        }
        var (_, design, _) = LoadPlan(request);
        string output = request["outputPath"]!.GetValue<string>();
        if (!output.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Diagnostic fragments must use .bin and cannot be burned.");
        DvdImageEncoder.ParallelismOverride = request["workers"]?.GetValue<int>() ?? 1;
        using FileStream stream = new(output, FileMode.CreateNew);
        DvdImageEncoder.Write(design, stream, request["startSector"]!.GetValue<uint>(), request["sectors"]!.GetValue<uint>(), null, CancellationToken.None);
        return output;
    }

    private sealed class CallbackProgress(Action<EncodingProgress> report) : IProgress<EncodingProgress>
    { public void Report(EncodingProgress value) => report(value); }

    private static string ValidateProfile(JsonObject request)
    {
        string source = Path.GetFullPath(request["profilePath"]!.GetValue<string>());
        Geometry? geometry = request["geometry"]?.Deserialize<Geometry>(Design.Json);
        JsonObject profile = DisMeasuredProfile.Read(source, geometry);
        string output = request["outputPath"]!.GetValue<string>();
        SaveJson(output, new JsonObject { ["schema"] = "dis-dvd-profile-validation-v1",
            ["profileSha256"] = Hash(source), ["calibration"] = profile["disCalibration"]!.DeepClone(),
            ["windowStartDeg"] = profile["windowStartDeg"]!.DeepClone(), ["windowEndDeg"] = profile["windowEndDeg"]!.DeepClone() });
        return output;
    }

    // Diagnostic: compare optimized normalized v3/v5 recipes with the original
    // scalar DCS resolver across angular/RGB interpolation and motif phases.
    private static string TestProfileRecipes(JsonObject request)
    {
        var map = new ContinuousColorMap(request["profilePath"]!.GetValue<string>());
        var fast = new FastRecipes(map, map.FastEntries(), .7, .6);
        Random random = new(43533);
        const int count = 4096;
        for (int i = 0; i < count; i++)
        {
            Color color = Color.FromArgb(random.Next(256), random.Next(256), random.Next(256));
            double beta = map.Data.ModelStartDeg + random.NextDouble() * (map.Data.ModelEndDeg - map.Data.ModelStartDeg);
            ulong turn = (ulong)random.Next(60000), logical = (ulong)random.Next(20_000_000);
            byte expected = map.SampleByte(color, beta, turn, logical, bandingReduction: .7, adaptiveMixing: .6);
            if (fast.Sample(color.ToArgb(), beta, turn, logical) != expected)
                throw new InvalidDataException("Fast/scalar measured recipe mismatch at sample " + i);
        }
        string output = request["outputPath"]!.GetValue<string>();
        SaveJson(output, new JsonObject { ["samples"] = count, ["passed"] = true });
        return output;
    }

    private static void Retry(Action action)
    {
        long deadline = Environment.TickCount64 + 4000;
        while (true)
        {
            try { action(); return; }
            catch (IOException) when (Environment.TickCount64 < deadline) { Thread.Sleep(100); }
            catch (UnauthorizedAccessException) when (Environment.TickCount64 < deadline) { Thread.Sleep(100); }
        }
    }

    private static void SaveJson(string path, JsonNode value)
    {
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, value.ToJsonString(Json));
            Retry(() => File.Move(temporary, path, true));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
