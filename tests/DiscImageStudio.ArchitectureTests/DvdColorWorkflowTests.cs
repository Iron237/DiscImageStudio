using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using DiscImageStudio.Burning;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;
using DiscImageStudio.ViewModels;

internal static class DvdColorWorkflowTests
{
    internal static void Run() => WpfTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "dis-dvd-color-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher, new NoRecorder(), new DiscImageStudio.SnapshotOpticalDriveProbe("none"));
        try
        {
            string image = Path.Combine(directory, "彩色.png");
            byte[] pixels = Enumerable.Range(0, 32 * 32).SelectMany(i => new byte[]
                { (byte)(i % 32 * 8), (byte)(i / 32 * 8), 210, (byte)(i % 32 < 2 ? 0 : 255) }).ToArray();
            RasterCodec.SavePng(image, 32, 32, pixels, grayscale: false);
            ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "43533-color", "dvd",
                new(23.991460627731737, 57.964893657925366, 2297888,
                    -3.9256854319227684E-05, 6.964795915429256E-05, -4.670918950137523E-05, 2297888),
                [new("a", "left", image, true, -40, 0, 15, .05), new("b", "right", image, true, 40, 0, -12, .05)],
                new(Path.Combine(directory, "fullcolor.iso"), false, string.Empty, string.Empty, false), null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            ProjectSession.ApplyToState(document, shell.State);
            shell.Design.SetDiscFamily(OpticalDiscFamily.Dvd);
            shell.Design.LoadLayers(document.Layers);
            shell.IsProjectOpen = true;
            FullColorViewModel color = shell.FullColor;
            color.OutputRoot = directory;
            Require(color.IsDvdProject && color.HasDesignSource && color.PreviewCommand.CanExecute(null), "DVD designs can enter the existing Full Color workspace");
            Require(!color.CalibrationCommand.CanExecute(null), "the BD calibration operation cannot run on DVD media");
            Require(color.MediaBadge.Contains("43533") && color.ColorWarning.Contains("95058"), "target disc and source-table media are disclosed separately");
            Require(color.BuildConfiguration()["geometry"]!["sectors"]!.GetValue<long>() == 2297888,
                "the worker consumes the calibrated project's complete geometry");
            FullColorWorkflowTests.Pump(color.PreviewCommand.ExecuteAsync(null), 300);
            Require(color.IsPreviewCurrent && color.Prediction is not null && color.TargetPreview is not null && color.UseForExport,
                "the packaged DCS worker generates preview images and a current native plan");
            string plan = Directory.GetFiles(directory, "plan.json", SearchOption.AllDirectories).Single();
            JsonNode data = JsonNode.Parse(File.ReadAllText(plan))!;
            Require(data["layers"]!.AsArray().Count == 2 && data["layers"]![0]!["x"]!.GetValue<double>() == -40,
                "visible design layers retain bottom-first stacking and physical positions");
            Require(data["viewing"]!.AsArray().Count == 2 && color.DvdViewingSummary.Contains("left"), "per-image viewing orientations are available in the GUI");
            Require(shell.Export.SourceSummary.Contains("全彩") || shell.Export.SourceSummary.Contains("Full Color"), "the Export workspace consumes the DVD color plan");
            bool accepted = false;
            try { color.ExportToAsync(Path.Combine(directory, "unapproved.iso")).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { accepted = true; }
            Require(accepted, "cross-media color-table transfer requires the existing trial-export acknowledgement");
            DvdColorEngine engine = new();
            string serial = Path.Combine(directory, "serial.bin"), parallel = Path.Combine(directory, "parallel.bin");
            foreach (var pair in new[] { (serial, 1), (parallel, 4) })
            {
                JsonObject request = new() { ["operation"] = "encode-fragment", ["workRoot"] = directory,
                    ["planPath"] = plan, ["outputPath"] = pair.Item1, ["workers"] = pair.Item2,
                    ["startSector"] = 1_000_000u, ["sectors"] = 64u };
                engine.Run(request, pair.Item1, new QuietProgress(), CancellationToken.None);
            }
            Require(File.ReadAllBytes(serial).SequenceEqual(File.ReadAllBytes(parallel)) && new FileInfo(serial).Length == 64 * 2048,
                "serial and parallel DCS encoding produce byte-identical physical samples");
            var burn = BurnViewModel.CreateFullColorBurnRequest("fake", serial, bluRay: false, new(2705, false));
            using MemoryStream copied = new();
            burn.ProduceContent(copied, CancellationToken.None);
            Require(burn.MediaKind == OpticalBurnMediaKind.DvdData && burn.RequireDvdR && copied.ToArray().SequenceEqual(File.ReadAllBytes(serial)),
                "burning streams the exact exported bytes with a DVD-R-only requirement");
            WindowsImapiBurner.ValidateRequiredMedia(burn, 9);
            foreach (int wrong in new[] { 0, 6, 7, 8, 10, 18 })
            {
                bool rejected = false;
                try { WindowsImapiBurner.ValidateRequiredMedia(burn, wrong); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "other physical media cannot receive a DVD-R color image");
            }
            if (Environment.GetEnvironmentVariable("DIS_TEST_DVD_COLOR_FULL_ISO") == "1")
            {
                color.AllowUnmeasured = true;
                shell.State.DvdOutputPath = Path.Combine(directory, "fullcolor.iso");
                FullColorWorkflowTests.Pump(shell.Export.GenerateCommand.ExecuteAsync(null), 600);
                Require(color.RequireCurrentIso() == shell.State.DvdOutputPath && new FileInfo(color.RequireCurrentIso()).Length == 2297888L * 2048,
                    "the native Export workspace produces a full, unchanged DVD geometry");
                Task<bool> verify = color.UseExistingIsoAsync(shell.State.DvdOutputPath);
                FullColorWorkflowTests.Pump(verify, 180);
                Require(verify.Result, "a complete DVD image can be verified and rebound using its full SHA-256");
                shell.ConfirmHandler = (_, _, _) => Task.FromResult(false);
                Task<bool> declined = color.ExportToAsync(shell.State.DvdOutputPath);
                FullColorWorkflowTests.Pump(declined);
                Require(!declined.Result, "declining same-name replacement leaves the existing ISO usable");
                shell.ConfirmHandler = (_, _, _) => Task.FromResult(true);
                using (FileStream busyMetadata = new(shell.State.DvdOutputPath + ".json", FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Task<bool> blocked = color.ExportToAsync(shell.State.DvdOutputPath);
                    FullColorWorkflowTests.Pump(blocked, 600);
                    Require(!blocked.Result && File.Exists(shell.State.DvdOutputPath + ".pending-dcs.json"),
                        "a blocked metadata commit preserves completed image data and a recovery record");
                }
                DateTime completedImageTime = File.GetLastWriteTimeUtc(shell.State.DvdOutputPath);
                Task<bool> recovered = color.ExportToAsync(shell.State.DvdOutputPath);
                FullColorWorkflowTests.Pump(recovered, 180);
                Require(recovered.Result && !File.Exists(shell.State.DvdOutputPath + ".pending-dcs.json")
                    && File.GetLastWriteTimeUtc(shell.State.DvdOutputPath) == completedImageTime,
                    "the same confirmed path recovers publication without recalculating or rewriting the DVD");
                shell.ConfirmHandler = null;
            }
            FullColorField exposure = color.DvdGroups.SelectMany(g => g.Fields).Single(f => f.Path == "exposure");
            exposure.Value = "110";
            Require(!color.IsPreviewCurrent && !color.ExportCommand.CanExecute(null), "DVD color parameter edits invalidate the old plan");
            exposure.Value = "100";
            color.LoadPlan(plan, applyConfiguration: true);
            shell.Design.Layers[0].TranslateXMm += 1;
            Require(!color.IsPreviewCurrent, "design edits invalidate DVD color as well as ordinary export");
            bool stale = false;
            try { color.LoadPlan(plan, applyConfiguration: true); } catch (ArgumentException) { stale = true; }
            Require(stale, "a plan from an earlier design cannot be loaded as current");
        }
        finally
        {
            if (shell.IsBusy)
            {
                shell.CancelJobCommand.Execute(null);
                FullColorWorkflowTests.Pump(FullColorWorkflowTests.WaitForIdle(shell), 60);
            }
            shell.LivePreview.OnWindowClosed(); shell.Drive.OnWindowClosed();
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("dvd-fullcolor-workflow: passed (43533 geometry, inherited layers, DCS measured preview, per-image viewing, byte-identical parallel encoding, native export/burn wiring, media and stale-plan guards; no recorder)");
    });

    private sealed class QuietProgress : IProgress<DiscJobProgress> { public void Report(DiscJobProgress value) { } }
    private sealed class NoRecorder : IOpticalDiscBurner
    {
        public Task<IReadOnlyList<OpticalBurnDevice>> GetDevicesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<OpticalBurnDevice>>([]);
        public Task<long> GetMediaSectorCountAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new Exception("No recorder access in this test.");
        public Task<IReadOnlyList<OpticalWriteSpeed>> GetSupportedWriteSpeedsAsync(string deviceId, OpticalBurnMediaKind mediaKind, CancellationToken cancellationToken = default) => throw new Exception("No recorder access in this test.");
        public Task<OpticalBurnResult> BurnAsync(OpticalBurnRequest request, IProgress<OpticalBurnProgress>? progress = null, CancellationToken cancellationToken = default) => throw new Exception("No physical write in this test.");
    }
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("DVD Full Color: " + message); }
}
