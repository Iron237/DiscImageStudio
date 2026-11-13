using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DiscImageStudio.BluRay;
using DiscImageStudio.Core;
using DiscImageStudio.ViewModels;

internal static class FullColorWorkflowTests
{
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
            BluRayDiscParameters geometry = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
            ProjectDocument document = new(ProjectJsonStore.CurrentSchemaVersion, "fullcolor-fixture", "bd",
                new(geometry.InnerRadiusMm, geometry.OuterRadiusMm, geometry.TotalSectors, UsableSectors: geometry.OutputSectors),
                [new("layer", "layer", imagePath, true, 0, -43, 12, .1)],
                new(Path.Combine(directory, "result.iso"), false, string.Empty, string.Empty, false), null, null,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
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
                Require(result["designSource"]!["fingerprint"]!.GetValue<string>() == vm.DesignFingerprint(),
                    "generated plan records its exact design source");
                if (Environment.GetEnvironmentVariable("DIS_TEST_FULLCOLOR_FULL_ISO") == "1")
                {
                vm.AllowUnmeasured = true;
                shell.State.BdOutputPath = Path.Combine(directory, "design-fullcolor.iso");
                Pump(shell.Export.GenerateCommand.ExecuteAsync(null));
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
        finally { shell.LivePreview.OnWindowClosed(); Directory.Delete(directory, recursive: true); }
    });

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Full Color: " + message);
    }
    private static void Pump(Task task)
    {
        DispatcherFrame frame = new();
        DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(90) };
        timer.Tick += (_, _) => frame.Continue = false;
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
        timer.Start();
        try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if (!task.IsCompleted) throw new TimeoutException("Full Color preview timed out.");
        task.GetAwaiter().GetResult();
    }
}
