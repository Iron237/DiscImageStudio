using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.BluRay;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public sealed partial class FullColorField(string path, string label, double initial) : ObservableObject
{
    public string Path { get; } = path;
    private string LabelKey { get; } = label;
    public string Label => L.T(LabelKey);
    [ObservableProperty] private string _value = initial.ToString("G", CultureInfo.InvariantCulture);
    internal void Relocalize() => OnPropertyChanged(nameof(Label));
}

public sealed class FullColorGroup(string key, IReadOnlyList<FullColorField> fields) : ObservableObject
{
    public string Title => L.T(key);
    public IReadOnlyList<FullColorField> Fields { get; } = fields;
    internal void Relocalize() => OnPropertyChanged(nameof(Title));
}

/// <summary>Colors the active design composite. Immutable plans bind layout, color parameters
/// and exported sectors; design edits invalidate the downstream preview and ISO.</summary>
public partial class FullColorViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly FullColorEngine _engine = new();
    private string? _planPath;
    private string? _planSignature;
    private bool _calibration;
    private bool _preparing;
    private string? _sourceProjectPath;
    private string? _isoPath;
    private string? _isoSignature;
    public ObservableCollection<FullColorGroup> Groups { get; } = [];

    public FullColorViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Groups.Add(new("FullColor.Layout", [
            new("patch.macrocellMm", "FullColor.Macro", .25), new("patch.microcellMm", "FullColor.Micro", .05)]));
        Groups.Add(new("FullColor.CalibrationRegion", [
            new("patch.centerMm.0", "FullColor.CenterX", 0), new("patch.centerMm.1", "FullColor.CenterY", 43),
            new("patch.widthMm", "FullColor.Width", 20), new("patch.heightMm", "FullColor.Height", 12)]));
        Groups.Add(new("FullColor.Light", [
            new("optics.lightPositionMm.0", "FullColor.X", 453.55736761107266),
            new("optics.lightPositionMm.1", "FullColor.Y", 43), new("optics.lightPositionMm.2", "FullColor.Z", 400)]));
        Groups.Add(new("FullColor.Viewer", [
            new("optics.viewerPositionMm.0", "FullColor.X", 0), new("optics.viewerPositionMm.1", "FullColor.Y", 43),
            new("optics.viewerPositionMm.2", "FullColor.Z", 400)]));
        Groups.Add(new("FullColor.Model", [
            new("optics.targetExposure", "FullColor.Exposure", .28),
            new("optics.spectralBlurNm", "FullColor.Blur", 9), new("optics.backgroundLevel", "FullColor.Background", .008)]));
        foreach (FullColorField field in Fields) field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(FullColorField.Value)) InvalidatePreview();
        };
        BrowseMeasurementsCommand = new RelayCommand(() => PickMeasurements(), () => CanEdit);
        ClearMeasurementsCommand = new RelayCommand(() => MeasurementsPath = string.Empty, () => CanEdit);
        BrowsePythonCommand = new RelayCommand(() => PickPython(), () => CanEdit);
        BrowseOutputCommand = new RelayCommand(() => PickOutput(), () => CanEdit);
        PreviewCommand = new AsyncRelayCommand(() => PreviewAsync(false), () => CanEdit && HasDesignSource);
        CalibrationCommand = new AsyncRelayCommand(() => PreviewAsync(true), () => CanEdit && IsBluRayProject);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanEdit && IsPreviewCurrent && _planPath is not null && IsBluRayProject);
        SaveRecipeCommand = new RelayCommand(SaveRecipe, () => CanEdit);
        LoadRecipeCommand = new RelayCommand(LoadRecipe, () => CanEdit);
        OpenPlanCommand = new RelayCommand(OpenPlan, () => CanEdit);
        ResetCommand = new RelayCommand(Reset, () => CanEdit);
        _shell.Design.DesignEdited += () => { InvalidatePreview(); RefreshDesignSource(); };
        _shell.Design.CanvasChanged += RefreshDesignSource;
        _shell.Design.CompositeRendered += path => { InputPreview = ReadBitmap(path ?? string.Empty); RefreshDesignSource(); };
        _shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ShellViewModel.IsProjectOpen) or nameof(ShellViewModel.CurrentProject))
            {
                if (_sourceProjectPath != _shell.ProjectFilePath)
                {
                    _sourceProjectPath = _shell.ProjectFilePath;
                    _planPath = _planSignature = _isoPath = _isoSignature = null;
                    Prediction = TargetPreview = ErrorPreview = InputPreview = NativeDitherPreview = TexturePreview = null;
                    UseForExport = false;
                    InvalidatePreview();
                }
                RefreshDesignSource();
            }
        };
        _shell.State.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(DiscParametersState.ToneDitherStrength)
                or nameof(DiscParametersState.ToneBrightness) or nameof(DiscParametersState.ToneLevels)
                or nameof(DiscParametersState.ToneSrgbLinearization))
            {
                InvalidatePreview();
                OnPropertyChanged(nameof(ToneSummary));
            }
            else if (e.PropertyName == nameof(DiscParametersState.ToneSummary)) OnPropertyChanged(nameof(ToneSummary));
            if (e.PropertyName?.StartsWith("Bd", StringComparison.Ordinal) == true && e.PropertyName != nameof(DiscParametersState.BdImagePath))
                RefreshDesignSource();
        };
        _shell.BusyChanged += NotifyCommands;
    }

    private IEnumerable<FullColorField> Fields => Groups.SelectMany(group => group.Fields);
    public bool CanEdit => !_shell.IsBusy && !_preparing;
    public bool IsBluRayProject => _shell.IsProjectOpen && _shell.CurrentProject?.IsBluRay == true;
    public bool HasDesignSource => IsBluRayProject && _shell.Design.HasLayers;
    public string SourceSummary => !IsBluRayProject ? L.T("FullColor.NeedProject")
        : !HasDesignSource ? L.T("请先在设计页导入图片。")
        : L.F($"设计页合成图源（{DiscImageStudio.Imaging.RingImageQuality.GenerationSize} px）") + " · " + _shell.CurrentProject!.Name;
    public string ToneSummary => _shell.State.ToneSummary;
    public IAsyncRelayCommand BackHomeCommand => _shell.BackHomeCommand;
    public IRelayCommand<Workspace> NavigateCommand => _shell.NavigateCommand;
    public RelayCommand BrowseMeasurementsCommand { get; }
    public RelayCommand ClearMeasurementsCommand { get; }
    public RelayCommand BrowsePythonCommand { get; }
    public RelayCommand BrowseOutputCommand { get; }
    public RelayCommand SaveRecipeCommand { get; }
    public RelayCommand LoadRecipeCommand { get; }
    public RelayCommand OpenPlanCommand { get; }
    public RelayCommand ResetCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand CalibrationCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }

    [ObservableProperty] private string _measurementsPath = string.Empty;
    [ObservableProperty] private string _pythonPath = FullColorEngine.DefaultPythonPath;
    [ObservableProperty] private string _outputRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "DiscImageStudio-FullColor");
    [ObservableProperty] private bool _allowUnmeasured;
    [ObservableProperty] private bool _isPreviewCurrent;
    [ObservableProperty] private bool _useForExport;
    [ObservableProperty] private BitmapSource? _inputPreview;
    [ObservableProperty] private BitmapSource? _targetPreview;
    [ObservableProperty] private BitmapSource? _prediction;
    [ObservableProperty] private BitmapSource? _errorPreview;
    [ObservableProperty] private BitmapSource? _nativeDitherPreview;
    [ObservableProperty] private BitmapSource? _texturePreview;
    [ObservableProperty] private string _resultSummary = L.T("FullColor.Empty");
    public string PreviewState => L.T(IsPreviewCurrent ? "FullColor.Current" : "FullColor.Stale");

    partial void OnMeasurementsPathChanged(string value) => InvalidatePreview();
    partial void OnIsPreviewCurrentChanged(bool value)
    {
        ExportCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PreviewState));
    }
    private void InvalidatePreview()
    {
        IsPreviewCurrent = false;
        _isoSignature = null;
    }
    private void RefreshDesignSource()
    {
        OnPropertyChanged(nameof(HasDesignSource));
        OnPropertyChanged(nameof(IsBluRayProject));
        OnPropertyChanged(nameof(SourceSummary));
        if (IsPreviewCurrent)
        {
            try { if (_planSignature != Signature() || !IsBluRayProject) InvalidatePreview(); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException) { InvalidatePreview(); }
        }
        NotifyCommands();
    }
    internal void Relocalize()
    {
        OnPropertyChanged(nameof(PreviewState));
        OnPropertyChanged(nameof(SourceSummary));
        foreach (FullColorField field in Fields) field.Relocalize();
        foreach (FullColorGroup group in Groups) group.Relocalize();
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit));
        foreach (IRelayCommand command in new IRelayCommand[] { BrowseMeasurementsCommand,
            ClearMeasurementsCommand, BrowsePythonCommand, BrowseOutputCommand, SaveRecipeCommand, LoadRecipeCommand,
            OpenPlanCommand, ResetCommand, PreviewCommand, CalibrationCommand, ExportCommand }) command.NotifyCanExecuteChanged();
    }

    private void PickMeasurements() { if (_shell.Dialogs?.PickOpen(L.T("FullColor.Measurements"), "JSON|*.json") is { } p) MeasurementsPath = p; }
    private void PickPython() { if (_shell.Dialogs?.PickOpen("Python", "Python|python.exe") is { } p) PythonPath = p; }
    private void PickOutput() { if (_shell.Dialogs?.PickFolder(L.T("FullColor.Output")) is { } p) OutputRoot = p; }
    private void OpenPlan()
    {
        if (_shell.Dialogs?.PickOpen(L.T("FullColor.OpenPlan"), "BDColorLab plan|plan.json") is not { } p) return;
        try { LoadPlan(p, applyConfiguration: true); }
        catch (Exception e) { ShowError(e); }
    }

    internal JsonObject BuildConfiguration()
    {
        JsonObject cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(FullColorEngine.EngineDirectory,
            "configs", "verbatim_64099.json")))!.AsObject();
        foreach (FullColorField field in Fields)
        {
            if (!double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
                throw new ArgumentException(field.Label + ": " + L.T("FullColor.InvalidNumber"));
            SetNode(cfg, field.Path, JsonValue.Create(value)!);
        }
        // Read the actual DIS preset instead of copying its geometry into GUI code.
        BluRayDiscParameters disc = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
        JsonObject geometry = cfg["geometry"]!.AsObject();
        geometry["totalSectors"] = disc.TotalSectors; geometry["usableSectors"] = disc.OutputSectors;
        geometry["innerRadiusMm"] = disc.InnerRadiusMm; geometry["outerRadiusMm"] = disc.OuterRadiusMm;
        cfg["imageLayout"] = "disc-annulus";
        var tone = _shell.State.BluRayTone;
        cfg["tone"] = new JsonObject { ["ditherStrength"] = tone.DitherStrength,
            ["brightness"] = tone.Brightness, ["srgbLinearization"] = tone.SrgbLinearization };
        return cfg;
    }
    private static JsonNode GetNode(JsonNode node, string path)
    {
        foreach (string key in path.Split('.')) node = node is JsonArray array ? array[int.Parse(key, CultureInfo.InvariantCulture)]! : node[key]!;
        return node;
    }
    private static void SetNode(JsonNode root, string path, JsonNode value)
    {
        string[] keys = path.Split('.');
        JsonNode parent = root;
        foreach (string key in keys[..^1]) parent = parent is JsonArray a ? a[int.Parse(key, CultureInfo.InvariantCulture)]! : parent[key]!;
        if (parent is JsonArray array) array[int.Parse(keys[^1], CultureInfo.InvariantCulture)] = value;
        else parent[keys[^1]] = value;
    }
    private void ApplyConfiguration(JsonNode config)
    {
        // Full-color v0.1 only supports the full-capacity constant-pitch 64099 preset.
        JsonNode g = config["geometry"]!;
        BluRayDiscParameters disc = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
        if (g["totalSectors"]!.GetValue<uint>() != disc.TotalSectors || g["usableSectors"]!.GetValue<uint>() != disc.OutputSectors
            || g["innerRadiusMm"]!.GetValue<double>() != disc.InnerRadiusMm || g["outerRadiusMm"]!.GetValue<double>() != disc.OuterRadiusMm
            || new[] { "pitchLinear", "pitchQuadratic", "pitchCubic", "startAngleDegrees" }.Any(k => (g[k]?.GetValue<double>() ?? 0) != 0))
            throw new ArgumentException(L.T("FullColor.GeometryOnly"));
        // Validate every field before applying any of them.
        string[] values = Fields.Select(f => GetNode(config, f.Path).GetValue<double>().ToString("G", CultureInfo.InvariantCulture)).ToArray();
        int index = 0;
        foreach (FullColorField field in Fields) field.Value = values[index++];
    }
    private string Signature()
    {
        string Stamp(string path) => File.Exists(path) ? path + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : path;
        return string.Join('|', Fields.Select(f => f.Value)) + "|" + DesignFingerprint() + "|" + Stamp(MeasurementsPath);
    }
    internal string DesignFingerprint()
    {
        string Stamp(string path) => File.Exists(path) ? path + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : path;
        string source = JsonSerializer.Serialize(new
        {
            Project = _shell.ProjectFilePath, Family = _shell.Design.Family,
            DataDirectory = _shell.State.BdDataDirectory,
            Radius = _shell.Design.CanvasOuterRadiusMm,
            Pipeline = "dis-bd-color-v2", Tone = _shell.State.BluRayTone,
            Geometry = new[] { _shell.State.BdInnerRadius, _shell.State.BdOuterRadius, _shell.State.BdTotalSectors,
                _shell.State.BdUsableSectors, _shell.State.BdPitchLinear, _shell.State.BdPitchQuadratic, _shell.State.BdPitchCubic },
            Layers = _shell.Design.CaptureLayers().Select(layer => new { Image = Stamp(layer.ImagePath), layer.Visible,
                layer.TranslateXMm, layer.TranslateYMm, layer.RotationDeg, layer.Scale }),
        });
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(source)));
    }
    private void RequireProjectGeometry()
    {
        if (!IsBluRayProject) throw new InvalidOperationException(L.T("FullColor.NeedProject"));
        BluRayDiscParameters preset = BluRayDiscPreset.Find("bd-r-verbatim-64099").CreateParameters();
        DiscParametersState s = _shell.State;
        if (!string.IsNullOrWhiteSpace(s.BdDataDirectory)) throw new ArgumentException(L.T("FullColor.NoFiles"));
        if (ParameterParser.ParseDouble(s.BdInnerRadius, "r0") != preset.InnerRadiusMm
            || ParameterParser.ParseDouble(s.BdOuterRadius, "r1") != preset.OuterRadiusMm
            || ParameterParser.ParsePositiveLong(s.BdTotalSectors, "sectors") != preset.TotalSectors
            || s.GetUsableSectors(DiscImageStudio.Core.OpticalDiscFamily.BluRay) != preset.OutputSectors
            || new[] { s.BdPitchLinear, s.BdPitchQuadratic, s.BdPitchCubic }.Any(v => ParameterParser.ParseDouble(v, "pitch") != 0))
            throw new ArgumentException(L.T("FullColor.GeometryOnly"));
    }

    private async Task PreviewAsync(bool calibration)
    {
        try
        {
            RequireProjectGeometry();
            if (calibration) UseForExport = false;
            _preparing = true;
            NotifyCommands();
            JsonObject cfg = BuildConfiguration();
            string root = Path.GetFullPath(OutputRoot);
            string directory = Path.Combine(root, (calibration ? "palette-" : "image-") + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff"));
            string plan = Path.Combine(directory, "plan.json");
            string signature = Signature();
            string fingerprint = DesignFingerprint();
            string composite = calibration ? string.Empty : await _shell.Design.EnsureExportCompositeAsync();
            if (signature != Signature()) throw new InvalidOperationException(L.T("FullColor.Stale"));
            if (!calibration) InputPreview = ReadBitmap(composite);
            JsonObject request = new() { ["operation"] = calibration ? "calibration" : "preview", ["directory"] = directory,
                ["workRoot"] = root, ["spectra"] = Path.Combine(root, "texture_spectra.npz"), ["configuration"] = cfg,
                ["inputPath"] = composite, ["inputLayout"] = "disc-composite", ["designFingerprint"] = fingerprint,
                ["canvasOuterRadiusMm"] = _shell.Design.CanvasOuterRadiusMm, ["measurementsPath"] = MeasurementsPath };
            InvalidatePreview();
            Prediction = TargetPreview = ErrorPreview = NativeDitherPreview = TexturePreview = null;
            bool success = await _shell.RunImageJobAsync(L.T(calibration ? "FullColor.Calibration" : "FullColor.Generate"), plan,
                (progress, token) => _engine.Run(PythonPath, request, plan, progress, token), canPreviewOutput: false);
            if (success)
            {
                LoadPlan(plan, applyConfiguration: false);
                _planSignature = signature;
                IsPreviewCurrent = Signature() == signature;
                if (!calibration && IsPreviewCurrent) UseForExport = true;
            }
        }
        catch (Exception e) { ShowError(e); }
        finally { _preparing = false; NotifyCommands(); }
    }

    internal void LoadPlan(string path, bool applyConfiguration)
    {
        JsonObject plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (plan["schema"]?.GetValue<string>() != "bdcolor-plan-v1") throw new ArgumentException("Unsupported full-color plan.");
        if (!IsBluRayProject || plan["designSource"]?["fingerprint"]?.GetValue<string>() != DesignFingerprint())
            throw new ArgumentException(L.T("FullColor.WrongDesign"));
        if (applyConfiguration) ApplyConfiguration(plan["configuration"]!);
        _planPath = Path.GetFullPath(path);
        _calibration = plan["kind"]?.GetValue<string>() == "calibration";
        string directory = Path.GetDirectoryName(_planPath)!;
        TargetPreview = ReadBitmap(Path.Combine(directory, "target.png"));
        Prediction = ReadBitmap(Path.Combine(directory, _calibration ? "patch_key.png" : "predicted.png"));
        ErrorPreview = ReadBitmap(Path.Combine(directory, "gamut_error.png"));
        NativeDitherPreview = ReadBitmap(Path.Combine(directory, "native-dither.png"));
        TexturePreview = ReadBitmap(Path.Combine(directory, "texture_ids_false_color.png"));
        InputPreview = ReadBitmap(Path.Combine(directory, "design-composite.png"));
        ResultSummary = plan["responseMode"]!.GetValue<string>() + " · " + directory;
        if (plan["metrics"] is JsonObject metrics && metrics["activeMacrocellCount"] is { } count)
            ResultSummary = L.T("FullColor.FullDisc") + " · " + count.ToJsonString() + " · " + ResultSummary;
        _planSignature = Signature();
        IsPreviewCurrent = true;
        UseForExport = !_calibration;
    }

    private async Task ExportAsync()
    {
        try
        {
            string? output = _shell.Dialogs?.PickSave("BD-R ISO|*.iso", ".iso", _calibration ? "palette-64099.iso" : "fullcolor-64099.iso");
            if (output is null) return;
            await ExportToAsync(output);
        }
        catch (Exception e) { ShowError(e); }
    }

    internal async Task<bool> ExportToAsync(string output)
    {
            RequireProjectGeometry();
            if (!IsPreviewCurrent || _planPath is null || _planSignature != Signature())
            {
                InvalidatePreview();
                throw new InvalidOperationException(L.T("FullColor.Stale"));
            }
            JsonNode plan = JsonNode.Parse(File.ReadAllText(_planPath))!;
            if (plan["responseMode"]!.GetValue<string>().StartsWith("unmeasured", StringComparison.Ordinal) && !AllowUnmeasured)
                throw new InvalidOperationException(L.T("FullColor.AcceptRequired"));
            if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException(L.T("请选择蓝光 ISO 输出位置。"));
            bool resume = File.Exists(output + ".partial.resume.json");
            if (resume && !await _shell.ConfirmAsync(L.T("FullColor.Resume"), L.T("FullColor.ResumeHint"), L.T("FullColor.Resume"))) return false;
            string root = Path.GetFullPath(OutputRoot);
            JsonObject request = new() { ["operation"] = "export", ["directory"] = Path.GetDirectoryName(_planPath),
                ["workRoot"] = root, ["planPath"] = _planPath, ["outputPath"] = output,
                ["allowUnmeasured"] = AllowUnmeasured, ["resume"] = resume };
            bool success = await _shell.RunImageJobAsync(L.T("FullColor.Export"), output,
                (progress, token) => _engine.Run(PythonPath, request, output, progress, token), canPreviewOutput: false);
            if (success && !_calibration) { _isoPath = output; _isoSignature = Signature(); }
            return success;
    }

    internal string RequireCurrentIso()
    {
        RequireProjectGeometry();
        if (!IsPreviewCurrent || _isoSignature != Signature() || _isoPath is null || !File.Exists(_isoPath)
            || new FileInfo(_isoPath).Length != 25025314816L)
            throw new InvalidOperationException(L.T("FullColor.ExportBeforeBurn"));
        return _isoPath;
    }

    private void SaveRecipe()
    {
        try
        {
            JsonObject recipe = new() { ["schema"] = "dis-fullcolor-workflow-v1", ["configuration"] = BuildConfiguration(),
                ["measurementsPath"] = MeasurementsPath, ["outputRoot"] = OutputRoot,
                ["pythonPath"] = PythonPath };
            if (_shell.Dialogs?.PickSave("Full Color JSON|*.bdcolor.json", ".bdcolor.json", "fullcolor.bdcolor.json") is { } p)
                File.WriteAllText(p, recipe.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { ShowError(e); }
    }
    private void LoadRecipe()
    {
        if (_shell.Dialogs?.PickOpen(L.T("FullColor.Load"), "Full Color JSON|*.json") is not { } p) return;
        try
        {
            JsonNode recipe = JsonNode.Parse(File.ReadAllText(p))!;
            if (recipe["schema"]?.GetValue<string>() != "dis-fullcolor-workflow-v1") throw new ArgumentException("Unsupported full-color recipe.");
            ApplyConfiguration(recipe["configuration"]!);
            MeasurementsPath = recipe["measurementsPath"]?.GetValue<string>() ?? string.Empty;
            OutputRoot = recipe["outputRoot"]?.GetValue<string>() ?? OutputRoot;
            // Loading a recipe never selects an executable from that document.
            InvalidatePreview();
        }
        catch (Exception e) { ShowError(e); }
    }
    private void Reset()
    {
        try { ApplyConfiguration(JsonNode.Parse(File.ReadAllText(Path.Combine(FullColorEngine.EngineDirectory, "configs", "verbatim_64099.json")))!); }
        catch (Exception e) { ShowError(e); }
    }
    private void ShowError(Exception e) => _shell.ShowToast(L.T("FullColor.Title"), e.Message, ToastKind.Warning);
    private static BitmapSource? ReadBitmap(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            BitmapImage image = new(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 1600; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException) { return null; }
    }
}
