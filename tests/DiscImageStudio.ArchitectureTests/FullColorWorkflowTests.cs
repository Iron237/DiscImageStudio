using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;
using DiscImageStudio.Services;
using DiscImageStudio.ImageIO;

internal static class FullColorWorkflowTests
{
    // Read-only project replay for local diagnosis; never saves the source project
    // or invokes a recorder. The normal regression suite uses synthetic fixtures.
    internal static void PreviewProject(string project, string output) => WpfTestHost.Run(() =>
    {
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        try
        {
            shell.OpenProjectForSnapshot(project);
            var vm = shell.FullColor;
            vm.PythonPath = Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_PYTHON")
                ?? throw new ArgumentException("Set DIS_TEST_FULLCOLOR_PYTHON first.");
            vm.OutputRoot = Path.GetFullPath(output);
            if (Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_MACRO") is { } macro)
                vm.Groups.SelectMany(g => g.Fields).Single(f => f.Path == "patch.macrocellMm").Value = macro;
            Pump(vm.PreviewCommand.ExecuteAsync(null), 600);
            Require(vm.IsPreviewCurrent && vm.Prediction is not null, "project replay produced a current prediction");
            Console.WriteLine("fullcolor-project-plan: " + Directory.GetFiles(vm.OutputRoot, "plan.json", SearchOption.AllDirectories).Order().Last());
        }
        finally
        {
            if (shell.IsBusy) { shell.CancelJobCommand.Execute(null); Pump(WaitForIdle(shell), 60); }
            shell.LivePreview.OnWindowClosed();
        }
    });

    public static void Run() => WpfTestHost.Run(() =>
    {
        ShellViewModel shell = new(Dispatcher.CurrentDispatcher);
        string directory = Path.Combine(Path.GetTempPath(), "dis-fullcolor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            FullColorViewModel vm = shell.FullColor;
            Require(!vm.PreviewCommand.CanExecute(null), "a Full Color image requires an open design project");
            string imagePath = Path.Combine(directory, "layer.png");
            byte[] pixels = Enumerable.Range(0, 16 * 16).SelectMany(_ => new byte[] { 30, 60, 220, 255 }).ToArray();
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(16, 16, 96, 96, PixelFormats.Bgra32, null, pixels, 64)));
            using (FileStream stream = File.Create(imagePath)) encoder.Save(stream);
            string background = Path.Combine(directory, "black-background.png");
            RasterCodec.SavePng(background, 16, 16,
                Enumerable.Range(0, 256).SelectMany(_ => new byte[] { 0, 0, 0, 255 }).ToArray(), grayscale: false);
            BluRayDiscParameters geometry = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
            ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "fullcolor-fixture", "bd",
                new(geometry.InnerRadiusMm, geometry.OuterRadiusMm, geometry.TotalSectors, UsableSectors: geometry.OutputSectors),
                [new("background", "background", background, true, 0, 0, 0, 1),
                 new("layer", "layer", imagePath, true, 0, 43, 12, .01)],
                new(Path.Combine(directory, "result.iso"), false, string.Empty, string.Empty, false), null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
            document = document with { Tone = new ToneProfile(1, 2, 0) };
            typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.CurrentProject))!.SetValue(shell, document);
            ProjectSession.ApplyToState(document, shell.State);
            shell.Design.SetDiscFamily(OpticalDiscFamily.BluRay);
            shell.Design.LoadLayers(document.Layers);
            shell.IsProjectOpen = true;
            Require(vm.HasDesignSource && vm.PreviewCommand.CanExecute(null), "design composition is inherited without picking another image");
            JsonObject config = vm.BuildConfiguration();
            BluRayDiscParameters disc = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
            Require(config["geometry"]!["innerRadiusMm"]!.GetValue<double>() == disc.InnerRadiusMm
                && config["geometry"]!["outerRadiusMm"]!.GetValue<double>() == disc.OuterRadiusMm,
                "Full Color reads the official DIS 64099 geometry");
            Require(!vm.ExportCommand.CanExecute(null), "no export before a plan exists");
            string plan = Path.Combine(directory, "plan.json");
            File.WriteAllText(plan, new JsonObject { ["schema"] = "bdcolor-plan-v1", ["configuration"] = config,
                ["kind"] = "image", ["responseMode"] = "unmeasured-17pp-1d-surrogate",
                ["designSource"] = new JsonObject { ["fingerprint"] = vm.DesignFingerprint() } }.ToJsonString());
            vm.LoadPlan(plan, applyConfiguration: true);
            Require(vm.IsPreviewCurrent && vm.ExportCommand.CanExecute(null), "an explicitly loaded plan can be exported");
            shell.State.Relocalize();
            Require(vm.IsPreviewCurrent, "rewording tone labels does not invalidate an unchanged plan");
            shell.State.ToneBrightness = 10;
            Require(!vm.IsPreviewCurrent, "the inherited native tone settings invalidate the color result");
            shell.State.ToneBrightness = 0;
            vm.LoadPlan(plan, applyConfiguration: true);
            long[] indices = [0, 1, 2, 255, 65536, 2147483647, 4294967301, -1];
            uint[] phases = [0, 1834104592, 2889141741, 3967754812, 3153350005, 4259742747, 1557653252, 2175737881];
            for (int i = 0; i < indices.Length; i++)
                Require(ToneDither.Phase(indices[i]) * 4294967296.0 == phases[i], "native/Python dither phase parity");
            string alpha = Path.Combine(directory, "alpha.png"), toned = Path.Combine(directory, "tone.png"), binary = Path.Combine(directory, "binary.png");
            RasterCodec.SavePng(alpha, 2, 1, [0, 0, 0, 0, 50, 100, 150, 128], grayscale: false);
            ToneProfile testTone = new(.75, 2, 10, true);
            FullColorDesignPreprocessor.Prepare(alpha, toned, binary, testTone, CancellationToken.None);
            byte[] actual = RasterCodec.LoadBgra32(toned).Pixels;
            Require(actual[0] == 255 && actual[1] == 255 && actual[2] == 255
                && actual[4] == testTone.AdjustedGray(152) && actual[5] == testTone.AdjustedGray(177)
                && actual[6] == testTone.AdjustedGray(202), "native white-alpha and tone curve preserve color channels");
            byte[] binaryPixels = RasterCodec.LoadBgra32(binary).Pixels;
            var native = BluRayImageLoader.Load(alpha, testTone);
            Require(binaryPixels[0] == native.Pixels[0] && binaryPixels[4] == native.Pixels[1],
                "the comparison raster uses the original BD loader");
            shell.Design.Layers[0].RotationDeg = 25;
            Require(!vm.IsPreviewCurrent && !vm.ExportCommand.CanExecute(null), "design transformations invalidate Full Color");
            shell.Design.Layers[0].RotationDeg = 12;
            vm.LoadPlan(plan, applyConfiguration: true);
            var width = vm.Groups.SelectMany(g => g.Fields).Single(f => f.Path == "patch.widthMm");
            width.Value = "18";
            Require(!vm.IsPreviewCurrent && !vm.ExportCommand.CanExecute(null), "editing parameters invalidates the previous export");
            width.Value = "NaN";
            bool rejected = false;
            try { vm.BuildConfiguration(); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "nonfinite edited parameters cannot reach the engine");
            width.Value = "20";
            vm.LoadPlan(plan, applyConfiguration: true);
            vm.MeasurementsPath = "changed-measurements.json";
            Require(!vm.IsPreviewCurrent, "changing measured response invalidates the previous plan");
            shell.NavigateTo(Workspace.FullColor);
            Require(shell.CurrentWorkspace == Workspace.FullColor, "Full Color is a native shell workspace");
            shell.SetBusy(true);
            Require(!vm.CanEdit && !vm.PreviewCommand.CanExecute(null) && !vm.CalibrationCommand.CanExecute(null),
                "shell job occupancy disables editing and competing generation");
            shell.SetBusy(false);
            vm.LoadPlan(plan, applyConfiguration: true);
            shell.State.BdDataDirectory = Path.Combine(directory, "disc-files");
            bool filesRejected = false;
            try { vm.RequireCurrentIso(); } catch (ArgumentException) { filesRejected = true; }
            Require(filesRejected, "the drawing-only color workflow cannot silently discard project files");
            shell.State.BdDataDirectory = string.Empty;
            if (Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_PYTHON") is { Length: > 0 } python)
            {
                vm.PythonPath = python;
                vm.OutputRoot = Path.Combine(directory, "native-output");
                foreach (var field in vm.Groups.SelectMany(g => g.Fields))
                    if (field.Path is "patch.widthMm" or "patch.heightMm") field.Value = "1";
                vm.MeasurementsPath = string.Empty;
                Task preview = vm.PreviewCommand.ExecuteAsync(null);
                Pump(preview);
                Require(vm.IsPreviewCurrent && vm.Prediction is not null && vm.UseForExport,
                    "native shell runs the color engine using its inherited composite");
                string generated = Directory.GetFiles(vm.OutputRoot, "plan.json", SearchOption.AllDirectories).Single();
                JsonNode result = JsonNode.Parse(File.ReadAllText(generated))!;
                Require(result["configuration"]!["imageLayout"]!.GetValue<string>() == "disc-annulus"
                    && result["metrics"]!["coloredMacrocellCount"]!.GetValue<int>() > 0,
                    "art below the old upper-window ROI survives full-annulus sampling");
                Require(vm.NativeDitherPreview is not null && vm.TexturePreview is not null,
                    "native dithering and the exported texture-dot grid can be inspected");
                Require(result["designSource"]!["fingerprint"]!.GetValue<string>() == vm.DesignFingerprint(),
                    "generated plan records its exact design source");
                if (Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_FULL_ISO") == "1")
                {
                vm.AllowUnmeasured = true;
                shell.State.BdOutputPath = Path.Combine(directory, "design-fullcolor.iso");
                Pump(shell.Export.GenerateCommand.ExecuteAsync(null), 600);
                Require(File.Exists(shell.State.BdOutputPath) && new FileInfo(shell.State.BdOutputPath).Length == 25025314816L,
                    "the native Export workspace consumes the current Full Color plan");
                Require(vm.RequireCurrentIso() == shell.State.BdOutputPath && !shell.CanPreviewLastOutput,
                    "burning consumes that full ISO and never sends it to ordinary grayscale readback");
                }
                shell.Design.Layers[0].TranslateXMm += 2;
                Require(!vm.IsPreviewCurrent && !vm.ExportCommand.CanExecute(null), "moving a design layer invalidates the generated result");
                bool burnRejected = false;
                try { vm.RequireCurrentIso(); } catch (InvalidOperationException) { burnRejected = true; }
                Require(burnRejected, "an ISO from an earlier design cannot reach the burner");
            }
        }
        finally
        {
            // A timed-out process must release its .partial before fixture cleanup.
            if (shell.IsBusy)
            {
                shell.CancelJobCommand.Execute(null);
                Pump(WaitForIdle(shell), 60);
            }
            shell.LivePreview.OnWindowClosed();
            Directory.Delete(directory, recursive: true);
        }
        Console.WriteLine("fullcolor-workflow: passed (design inheritance, physical sampling, stale plans, native engine and export guards)");
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Full Color: " + message);
    }
    private static async Task WaitForIdle(ShellViewModel shell)
    {
        while (shell.IsBusy) await Task.Delay(50);
    }
    private static void Pump(Task task, int seconds = 180)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => frame.Continue = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Full Color preview timed out.");
        task.GetAwaiter().GetResult();
    }
}
