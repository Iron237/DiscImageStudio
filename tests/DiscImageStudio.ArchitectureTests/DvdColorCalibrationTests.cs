using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class DvdColorCalibrationTests
{
    internal static void ExportChart(string project, string chart, string iso) => WpfTestHost.Run(() =>
    {
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoRecorder(), new DiscImageStudio.SnapshotOpticalDriveProbe("none"));
        try
        {
            if (File.Exists(iso)) throw new IOException("The offline chart helper never overwrites an existing ISO.");
            shell.OpenProjectForSnapshot(project);
            shell.FullColor.OutputRoot = Path.GetDirectoryName(Path.GetFullPath(chart))!;
            shell.FullColor.DvdCalibration.LoadChart(chart);
            shell.State.DvdOutputPath = Path.GetFullPath(iso);
            FullColorWorkflowTests.Pump(shell.Export.GenerateCommand.ExecuteAsync(null), 1800);
            Require(shell.FullColor.RequireCurrentIso() == Path.GetFullPath(iso), "native Export generated the complete calibration image");
            Task<bool> verify = shell.FullColor.UseExistingIsoAsync(iso);
            FullColorWorkflowTests.Pump(verify, 180);
            Require(verify.Result, "native ISO reuse verifies the complete calibration SHA-256");
            Console.WriteLine("dvd-calibration-chart-iso: " + iso);
        }
        finally
        {
            if (shell.IsBusy) { shell.CancelJobCommand.Execute(null); FullColorWorkflowTests.Pump(FullColorWorkflowTests.WaitForIdle(shell), 60); }
            shell.LivePreview.OnWindowClosed(); shell.Drive.OnWindowClosed();
        }
    });

    internal static void Run()
    {
        string? fixture = Environment.GetEnvironmentVariable("DIS_DVD_CAL_FIXTURE_ROOT");
        if (string.IsNullOrEmpty(fixture) || !Directory.Exists(fixture))
        {
            Console.WriteLine("dvd-color-calibration: photo fixture not selected; run Python calibration tests with DIS_DVD_CAL_FIXTURE_ROOT for native integration");
            return;
        }
        WpfTestHost.Run(() => RunCore(fixture));
    }

    private static void RunCore(string fixture)
    {
        string root = Path.Combine(Path.GetTempPath(), "dis-dvd-cal-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoRecorder(), new DiscImageStudio.SnapshotOpticalDriveProbe("none"));
        try
        {
            string png = Path.Combine(root, "source.png");
            RasterCodec.SavePng(png, 16, 16, Enumerable.Repeat(new byte[] { 80, 140, 190, 255 }, 256).SelectMany(x => x).ToArray(), false);
            ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "43533-measurements", "dvd",
                new(23.991460627731737, 57.964893657925366, 2297888,
                    -3.9256854319227684e-5, 6.964795915429256e-5, -4.670918950137523e-5, 2297888),
                [new("a", "tiny color", png, true, 40, 0, 0, .005)],
                new(Path.Combine(root, "output.iso"), false, string.Empty, string.Empty, false), null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            ProjectSession.ApplyToState(document, shell.State);
            shell.Design.SetDiscFamily(OpticalDiscFamily.Dvd); shell.Design.LoadLayers(document.Layers); shell.IsProjectOpen = true;
            var parent = shell.FullColor; var calibration = parent.DvdCalibration;
            parent.OutputRoot = root;
            string? python = Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_PYTHON");
            if (!string.IsNullOrEmpty(python))
            {
                parent.PythonPath = python;
                FullColorWorkflowTests.Pump(calibration.CreateChartAsync(), 120);
                Require(calibration.HasChart && parent.IsDvdChart && parent.IsPreviewCurrent && parent.UseForExport,
                    "the native chart command publishes a full-motif chart through the installed runtime");
            }
            string chart = Path.Combine(fixture, "chart", "chart.json");
            calibration.LoadChart(chart);
            Require(calibration.ChartPreview is not null && parent.UseExistingIsoCommand.CanExecute(null),
                "chart layout preview and completed-ISO reuse are available without using the 95058 trial checkbox");
            if (!string.IsNullOrEmpty(python))
            {
                FullColorWorkflowTests.Pump(calibration.ImportPhotosAsync([Path.Combine(fixture, "full", "000deg.png"),
                    Path.Combine(fixture, "full", "005deg.png")]), 180);
                Require(calibration.Frames.Count == 2 && calibration.PhotoPreview is not null,
                    "the native batch photo command decodes and registers originals through the configured runtime");
                calibration.WindowWidth = "5";
                FullColorWorkflowTests.Pump(calibration.BuildProfileAsync(), 180);
                Require(string.IsNullOrEmpty(calibration.BuiltProfilePath) && calibration.QualityText.Contains("needs-data"),
                    "insufficient-angle photo sessions show their quality report without exposing an applicable profile");
            }
            DvdColorEngine engine = new();
            foreach ((string name, int workers) in new[] { ("chart-serial.bin", 1), ("chart-parallel.bin", 4) })
            {
                string file = Path.Combine(root, name);
                engine.Run(new JsonObject { ["operation"] = "encode-fragment", ["workRoot"] = root,
                    ["planPath"] = chart, ["outputPath"] = file, ["workers"] = workers,
                    ["startSector"] = 1_000_000u, ["sectors"] = 64u }, file, new QuietProgress(), CancellationToken.None);
            }
            Require(File.ReadAllBytes(Path.Combine(root, "chart-serial.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "chart-parallel.bin"))),
                "known-motif calibration uses byte-identical serial/parallel DVD encoding");
            JsonNode broken = JsonNode.Parse(File.ReadAllText(chart))!;
            broken["textures"]![0]!["motif"] = "01";
            string corrupted = Path.Combine(root, "corrupt-chart.json"); File.WriteAllText(corrupted, broken.ToJsonString());
            bool rejected = false;
            try
            {
                engine.Run(new JsonObject { ["operation"] = "encode-fragment", ["workRoot"] = root,
                    ["planPath"] = corrupted, ["outputPath"] = Path.Combine(root, "bad.bin"), ["workers"] = 1,
                    ["startSector"] = 0u, ["sectors"] = 16u }, Path.Combine(root, "bad.bin"), new QuietProgress(), CancellationToken.None);
            }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected, "mutated manifest motifs cannot be silently burned as the known chart");
            string session = Path.Combine(fixture, "full", "session", "session.json");
            calibration.LoadSession(session);
            Require(calibration.Frames.Count == 72 && calibration.PhotoPreview is not null,
                "a saved capture session reopens its source previews, measured poses and registration states");
            string profile = Path.Combine(fixture, "full", "session", "profiles", "test-profile", "profile.json");
            foreach ((string source, string name) in new[] { (profile, "v3-parity.json"),
                (Path.Combine(DvdColorEngine.WorkerDirectory, "data", "continuous-color-map.json"), "v5-parity.json") })
            {
                string output = Path.Combine(root, name);
                engine.Run(new JsonObject { ["operation"] = "test-profile-recipes", ["workRoot"] = root,
                    ["profilePath"] = source, ["outputPath"] = output }, output, new QuietProgress(), CancellationToken.None);
                Require(JsonNode.Parse(File.ReadAllText(output))!["passed"]!.GetValue<bool>(), "fast recipes match the original scalar DCS resolver");
            }
            FullColorWorkflowTests.Pump(calibration.ApplyProfileAsync(profile), 180);
            Require(parent.DvdProfilePath == Path.GetFullPath(profile) && parent.DvdProfileSynthetic && !parent.IsPreviewCurrent && !parent.IsDvdChart,
                "profile import checks geometry/evidence and invalidates the calibration plan");
            Require(shell.State.DvdOutputPath == Path.Combine(root, "output.iso"), "leaving chart mode restores the image output path instead of overwriting the calibration ISO");
            FullColorWorkflowTests.Pump(parent.PreviewCommand.ExecuteAsync(null), 180);
            Require(parent.IsPreviewCurrent && parent.Prediction is not null, "self-measured v3 LUTs render through the same DCS image pipeline");
            string plan = Directory.GetFiles(root, "plan.json", SearchOption.AllDirectories).Single();
            JsonNode metadata = JsonNode.Parse(File.ReadAllText(plan))!;
            Require(metadata["responseMode"]!.GetValue<string>() == "dvd-self-measured-43533"
                && File.Exists(Path.Combine(Path.GetDirectoryName(plan)!, "measured", "measurements.json")),
                "the immutable image plan embeds the custom LUT and its measurement evidence");
            var spiral = DvdTrackGeometry.Create(23.991460627731737, 57.964893657925366,
                2297888L * 38688 * .0001333, -3.9256854319227684e-5, 6.964795915429256e-5, -4.670918950137523e-5);
            uint imageSector = (uint)(spiral.ArcLengthAtRadius(40) / (38688 * .0001333)) / 16 * 16;
            foreach ((string name, int workers) in new[] { ("profile-serial.bin", 1), ("profile-parallel.bin", 4) })
            {
                string file = Path.Combine(root, name);
                engine.Run(new JsonObject { ["operation"] = "encode-fragment", ["workRoot"] = root,
                    ["planPath"] = plan, ["outputPath"] = file, ["workers"] = workers,
                    ["startSector"] = imageSector, ["sectors"] = 64u }, file, new QuietProgress(), CancellationToken.None);
            }
            Require(File.ReadAllBytes(Path.Combine(root, "profile-serial.bin")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "profile-parallel.bin"))),
                "measured v3 LUTs preserve serial/parallel byte identity in the fast encoder");
            RasterCodec.SavePng(png, 16, 16, Enumerable.Repeat(new byte[] { 220, 50, 50, 255 }, 256).SelectMany(x => x).ToArray(), false);
            shell.Design.LoadLayers(document.Layers);
            FullColorWorkflowTests.Pump(parent.PreviewCommand.ExecuteAsync(null), 180);
            string changedPlan = Directory.GetFiles(root, "plan.json", SearchOption.AllDirectories).OrderBy(File.GetLastWriteTimeUtc).Last();
            string changedFragment = Path.Combine(root, "profile-red.bin");
            engine.Run(new JsonObject { ["operation"] = "encode-fragment", ["workRoot"] = root,
                ["planPath"] = changedPlan, ["outputPath"] = changedFragment, ["workers"] = 2,
                ["startSector"] = imageSector, ["sectors"] = 64u }, changedFragment, new QuietProgress(), CancellationToken.None);
            Require(!File.ReadAllBytes(changedFragment).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "profile-serial.bin"))),
                "the calibrated fragment crosses the foreground image and responds to input color changes");
            bool testDataBlocked = false;
            try { parent.ExportToAsync(Path.Combine(root, "must-not-burn.iso")).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { testDataBlocked = true; }
            Require(testDataBlocked && !File.Exists(Path.Combine(root, "must-not-burn.iso")), "synthetic camera fixtures cannot produce physical ISO images");
            parent.ClearDvdProfile();
            Require(!parent.IsPreviewCurrent && string.IsNullOrEmpty(parent.DvdProfilePath), "returning to 95058 invalidates the self-measured plan");
        }
        finally
        {
            if (shell.IsBusy) { shell.CancelJobCommand.Execute(null); FullColorWorkflowTests.Pump(FullColorWorkflowTests.WaitForIdle(shell), 60); }
            shell.LivePreview.OnWindowClosed(); shell.Drive.OnWindowClosed();
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("dvd-color-calibration: chart, photo session, v3 profile, immutable evidence, parallel encoding and synthetic-export guard passed; no recorder");
    }

    private sealed class QuietProgress : IProgress<DiscJobProgress> { public void Report(DiscJobProgress value) { } }
    private sealed class NoRecorder : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new Exception("No recorder.");
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new Exception("No recorder.");
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default) => throw new Exception("No physical write.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException("DVD calibration: " + message); }
}
