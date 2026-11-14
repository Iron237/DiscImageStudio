using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using DiscImageStudio.Core;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;

namespace DiscImageStudio.ViewModels;

public partial class FullColorViewModel
{
    public DvdColorCalibrationViewModel DvdCalibration { get; }
    private string? _dvdChartPath;
    private string? _dvdPriorImageOutputPath;
    public bool IsDvdChart => IsDvdProject && _calibration && _dvdChartPath is not null;
    public bool ShowDesignColor => !IsDvdChart;
    public string ActiveOutputTitle => L.T(IsDvdChart ? "DvdCal.Title" : "FullColor.Title");
    [ObservableProperty] private string _dvdProfilePath = string.Empty;
    [ObservableProperty] private bool _dvdProfileSynthetic;
    [ObservableProperty] private bool _dvdProfileExperimental;
    public string DvdProfileSummary => string.IsNullOrWhiteSpace(DvdProfilePath) ? L.T("DvdCal.BuiltinProfile")
        : L.T(DvdProfileSynthetic ? "DvdCal.SyntheticProfile" : DvdProfileExperimental ? "DvdCal.ExperimentalProfile" : "DvdCal.SelfProfile") + " · " + DvdProfilePath;
    partial void OnDvdProfilePathChanged(string value) { InvalidatePreview(); RefreshDvdBindings(); OnPropertyChanged(nameof(DvdProfileSummary)); }
    partial void OnDvdProfileSyntheticChanged(bool value) { RefreshDvdBindings(); OnPropertyChanged(nameof(DvdProfileSummary)); }
    partial void OnDvdProfileExperimentalChanged(bool value) { RefreshDvdBindings(); OnPropertyChanged(nameof(DvdProfileSummary)); }
    private string ColorAcceptRequired => L.T(DvdProfileExperimental ? "DvdCal.ExperimentalAcceptRequired" : string.IsNullOrWhiteSpace(DvdProfilePath) ? "FullColor.DvdAcceptRequired" : "DvdCal.AcceptRequired");
    private readonly DvdColorEngine _dvdEngine = new();
    private const string DvdPlanSchema = "dis-dvd-fullcolor-plan-v1";
    public ObservableCollection<FullColorGroup> DvdGroups { get; } = [];
    private IEnumerable<FullColorField> DvdFields => DvdGroups.SelectMany(g => g.Fields);
    public IEnumerable<FullColorGroup> ActiveGroups => IsDvdProject ? DvdGroups : Groups;
    public bool IsDvdProject => _shell.IsProjectOpen && _shell.CurrentProject?.IsDvd == true;
    public bool IsSupportedColorProject => IsBluRayProject || IsDvdProject;
    public string MediaBadge => IsDvdProject ? "DVD-R · 43533" : "BD-R · 64099";
    public string ColorWarning => L.T(IsDvdChart ? "DvdCal.ChartWarning" : IsDvdProject
        ? (DvdProfileSynthetic ? "DvdCal.SyntheticProfile" : DvdProfileExperimental ? "DvdCal.ExperimentalWarning" : string.IsNullOrWhiteSpace(DvdProfilePath) ? "FullColor.DvdWarning" : "DvdCal.SelfWarning") : "FullColor.Warning");
    public string ColorGeometryHint => L.T(IsDvdProject ? "FullColor.DvdGeometry" : "FullColor.GeometryOnly");
    public string ColorAcceptLabel => L.T(IsDvdProject ? (DvdProfileExperimental ? "DvdCal.ExperimentalAccept" : string.IsNullOrWhiteSpace(DvdProfilePath) ? "FullColor.DvdAccept" : "DvdCal.Accept") : "FullColor.Accept");
    public string ColorOutputHint => L.T(IsDvdProject ? "FullColor.DvdOutputHint" : "FullColor.OutputHint");
    public string ColorDesignHint => L.T(IsDvdProject ? "FullColor.DvdDesignHint" : "FullColor.DesignInputHint");
    public string ColorToneSummary => IsDvdProject ? L.T("FullColor.DvdTone") : ToneSummary;
    [ObservableProperty] private bool _dvdAutoViewing = true;
    [ObservableProperty] private bool _dvdPreferDark;
    [ObservableProperty] private string _dvdViewingSummary = string.Empty;
    partial void OnDvdAutoViewingChanged(bool value) => InvalidatePreview();
    partial void OnDvdPreferDarkChanged(bool value) => InvalidatePreview();

    private void InitializeDvdColor()
    {
        DvdGroups.Add(new("FullColor.DvdTones", [
            new("exposure", "FullColor.DvdExposure", 100), new("brightness", "FullColor.DvdBrightness", 0),
            new("contrast", "FullColor.DvdContrast", 100)]));
        DvdGroups.Add(new("FullColor.DvdTexture", [
            new("smoothing", "FullColor.DvdSmoothing", 100), new("mixing", "FullColor.DvdMixing", 100),
            new("sampleSize", "FullColor.DvdSamples", 32768), new("centerBeta", "FullColor.DvdBeta", 100)]));
        foreach (FullColorField field in DvdFields) field.PropertyChanged += (_, args) =>
        { if (args.PropertyName == nameof(FullColorField.Value)) InvalidatePreview(); };
    }

    private void RefreshDvdBindings()
    {
        foreach (string property in new[] { nameof(IsDvdProject), nameof(IsSupportedColorProject), nameof(ActiveGroups),
            nameof(MediaBadge), nameof(ColorWarning), nameof(ColorGeometryHint), nameof(ColorAcceptLabel),
            nameof(ColorOutputHint), nameof(ColorDesignHint), nameof(ColorToneSummary), nameof(IsDvdChart), nameof(DvdProfileSummary),
            nameof(ShowDesignColor), nameof(ActiveOutputTitle) }) OnPropertyChanged(property);
    }

    internal JsonObject BuildDvdGeometry()
    {
        var state = _shell.State;
        double Read(string text) => ParameterParser.ParseDouble(text, "DVD geometry");
        return new JsonObject { ["innerRadiusMm"] = Read(state.DvdInnerRadius),
                ["outerRadiusMm"] = Read(state.DvdOuterRadius),
                ["sectors"] = ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "sectors"),
                ["pitchLinear"] = Read(state.DvdPitchLinear), ["pitchQuadratic"] = Read(state.DvdPitchQuadratic),
                ["pitchCubic"] = Read(state.DvdPitchCubic) };
    }

    private static string FileStamp(string path) => File.Exists(path) ? Path.GetFullPath(path) + ":" + new FileInfo(path).Length + ":" + File.GetLastWriteTimeUtc(path).Ticks : path;

    private JsonObject BuildDvdConfiguration()
    {
        JsonObject configuration = new() { ["autoViewing"] = DvdAutoViewing, ["preferDark"] = DvdPreferDark,
            ["geometry"] = BuildDvdGeometry() };
        if (!string.IsNullOrWhiteSpace(DvdProfilePath))
        {
            JsonNode data = JsonNode.Parse(File.ReadAllText(DvdProfilePath))!;
            string lut = data["lutFile"]!.GetValue<string>();
            if (Path.GetFileName(lut) != lut) throw new ArgumentException("Invalid profile LUT path.");
            configuration["profilePath"] = Path.GetFullPath(DvdProfilePath);
            configuration["profileStamp"] = FileStamp(DvdProfilePath) + "|" + FileStamp(Path.Combine(Path.GetDirectoryName(DvdProfilePath)!, lut));
        }
        foreach (FullColorField field in DvdFields)
        {
            if (!double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
                throw new ArgumentException(field.Label + ": " + L.T("FullColor.InvalidNumber"));
            if (field.Path == "sampleSize")
            {
                if (number != Math.Floor(number) || number is < 4096 or > 262144 || ((int)number & ((int)number - 1)) != 0)
                    throw new ArgumentException(L.T("FullColor.DvdSampleRange"));
                configuration[field.Path] = (int)number;
            }
            else configuration[field.Path] = number;
        }
        return configuration;
    }

    private void ApplyDvdConfiguration(JsonNode configuration, string? directory = null)
    {
        LeaveDvdChart();
        string? profile = configuration["profilePath"]?.GetValue<string>();
        DvdProfilePath = string.IsNullOrWhiteSpace(profile) ? string.Empty : Path.GetFullPath(Path.Combine(directory ?? string.Empty, profile));
        DvdProfileSynthetic = !string.IsNullOrEmpty(DvdProfilePath) && JsonNode.Parse(File.ReadAllText(DvdProfilePath))?["disCalibration"]?["synthetic"]?.GetValue<bool>() == true;
        DvdProfileExperimental = !string.IsNullOrEmpty(DvdProfilePath) && JsonNode.Parse(File.ReadAllText(DvdProfilePath))?["disCalibration"]?["experimental"]?.GetValue<bool>() == true;
        foreach (FullColorField field in DvdFields)
            field.Value = configuration[field.Path]!.GetValue<double>().ToString("G", CultureInfo.InvariantCulture);
        DvdAutoViewing = configuration["autoViewing"]!.GetValue<bool>();
        DvdPreferDark = configuration["preferDark"]!.GetValue<bool>();
    }

    internal void RequireDvdGeometry()
    {
        var state = _shell.State;
        if (!IsDvdProject || state.GetUsableSectors(OpticalDiscFamily.Dvd)
            != ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "sectors"))
            throw new ArgumentException(L.T("FullColor.DvdGeometry"));
        if (!string.IsNullOrWhiteSpace(state.DvdDataDirectory)) throw new ArgumentException(L.T("FullColor.DvdNoFiles"));
        double inner = ParameterParser.ParseDouble(state.DvdInnerRadius, "r0"), outer = ParameterParser.ParseDouble(state.DvdOuterRadius, "r1");
        long sectors = ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "sectors");
        if (inner <= 0 || outer <= inner || outer > 60 || sectors != 2297888)
            throw new ArgumentException(L.T("FullColor.DvdGeometry"));
        DvdTrackGeometry.ValidatePitchCoefficients(ParameterParser.ParseDouble(state.DvdPitchLinear, "p1"),
            ParameterParser.ParseDouble(state.DvdPitchQuadratic, "p2"), ParameterParser.ParseDouble(state.DvdPitchCubic, "p3"));
    }

    private async Task PreviewDvdAsync()
    {
        try
        {
            RequireDvdGeometry();
            LeaveDvdChart(); RefreshDvdBindings();
            _preparing = true; NotifyCommands();
            string signature = Signature(), fingerprint = DesignFingerprint();
            string composite = await _shell.Design.EnsureExportCompositeAsync();
            if (signature != Signature()) throw new InvalidOperationException(L.T("FullColor.Stale"));
            InputPreview = ReadBitmap(composite);
            var layers = _shell.Design.Layers.Reverse().Where(l => l.IsVisible && l.HasImage)
                .Select(l => new DvdColorLayerSnapshot(l.Id, l.Name, new(l.Image!, l.BaseWidthMm, l.BaseHeightMm,
                    l.TranslateXMm, l.TranslateYMm, l.RotationDeg, l.Scale))).ToArray();
            string root = Path.GetFullPath(OutputRoot);
            string directory = Path.Combine(root, "dvd-43533-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff"));
            string plan = Path.Combine(directory, "plan.json");
            JsonObject request = new() { ["operation"] = "preview", ["workRoot"] = root, ["directory"] = directory,
                ["configuration"] = BuildDvdConfiguration(),
                ["designSource"] = new JsonObject { ["fingerprint"] = fingerprint } };
            InvalidatePreview();
            Prediction = TargetPreview = ErrorPreview = NativeDitherPreview = TexturePreview = null;
            bool success = await _shell.RunImageJobAsync(L.T("FullColor.Generate"), plan, (progress, token) =>
            {
                DvdColorEngine.WriteLayers(request, layers);
                return _dvdEngine.Run(request, plan, progress, token);
            }, canPreviewOutput: false);
            if (success && signature == Signature()) LoadDvdPlan(plan, applyConfiguration: false);
        }
        catch (Exception error) { ShowError(error); }
        finally { _preparing = false; NotifyCommands(); }
    }

    private void LoadDvdPlan(string path, bool applyConfiguration)
    {
        LeaveDvdChart();
        RequireDvdGeometry();
        JsonObject plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (plan["schema"]?.GetValue<string>() != DvdPlanSchema
            || plan["designSource"]?["fingerprint"]?.GetValue<string>() != DesignFingerprint())
            throw new ArgumentException(L.T("FullColor.WrongDesign"));
        if (applyConfiguration) ApplyDvdConfiguration(plan["configuration"]!, Path.GetDirectoryName(Path.GetFullPath(path)));
        _planPath = Path.GetFullPath(path); _calibration = false;
        string directory = Path.GetDirectoryName(_planPath)!;
        TargetPreview = ReadBitmap(Path.Combine(directory, "target.png"));
        InputPreview ??= TargetPreview;
        Prediction = ReadBitmap(Path.Combine(directory, "predicted.png"));
        ErrorPreview = NativeDitherPreview = TexturePreview = null;
        DvdViewingSummary = string.Join(Environment.NewLine, plan["viewing"]!.AsArray().Select(v =>
            v!["name"]!.GetValue<string>() + " · " + L.T("FullColor.DvdViewingRotation") + " "
            + v["viewingRotationDeg"]!.GetValue<double>().ToString("F1", CultureInfo.InvariantCulture) + "°"));
        ResultSummary = (string.IsNullOrWhiteSpace(DvdProfilePath) ? "DCS · 95058 → 43533 · " : DvdProfileExperimental ? "DIS · 43533 experimental · " : "DIS · 43533 measured · ") + directory;
        _isoPath = _isoSignature = null;
        _planSignature = Signature(); IsPreviewCurrent = true; UseForExport = true; RefreshDvdBindings(); NotifyCommands();
    }

    private async Task<bool> ExportDvdAsync(string output, bool overwriteConfirmed)
    {
        RequireDvdGeometry();
        if (!IsPreviewCurrent || _planPath is null || _planSignature != Signature()) throw new InvalidOperationException(L.T("FullColor.Stale"));
        if (!IsDvdChart && DvdProfileSynthetic) throw new InvalidOperationException(L.T("DvdCal.SyntheticProfile"));
        if (!IsDvdChart && !AllowUnmeasured) throw new InvalidOperationException(ColorAcceptRequired);
        bool overwrite = File.Exists(output);
        if (overwrite && !overwriteConfirmed && !await _shell.ConfirmAsync(L.T("FullColor.Replace"),
            L.T("FullColor.ReplaceHint") + Environment.NewLine + output, L.T("FullColor.Replace"))) return false;
        JsonObject request = new() { ["operation"] = "export", ["workRoot"] = Path.GetFullPath(OutputRoot),
            ["planPath"] = _planPath, ["outputPath"] = Path.GetFullPath(output),
            ["allowMediaTransfer"] = AllowUnmeasured, ["overwrite"] = overwrite };
        string signature = Signature();
        bool success = await _shell.RunImageJobAsync(L.T("FullColor.Export"), output,
            (progress, token) => _dvdEngine.Run(request, output, progress, token), canPreviewOutput: false);
        if (success && signature == Signature()) { _isoPath = Path.GetFullPath(output); _isoSignature = signature; }
        return success;
    }

    private long ExpectedColorIsoBytes => IsDvdProject
        ? checked(ParameterParser.ParsePositiveLong(_shell.State.DvdTotalSectors, "sectors") * 2048) : 25025314816L;

    private void ResetDvdSettings()
    {
        double[] values = [100, 0, 100, 100, 100, 32768, 100];
        int index = 0;
        foreach (FullColorField field in DvdFields) field.Value = values[index++].ToString("G", CultureInfo.InvariantCulture);
        DvdAutoViewing = true; DvdPreferDark = false; InvalidatePreview();
    }

    internal void SelectDvdChart(string path)
    {
        RequireDvdGeometry();
        JsonNode chart = JsonNode.Parse(File.ReadAllText(path))!;
        if (chart["schema"]?.GetValue<string>() != "dis-dvd-texture-chart-v1") throw new ArgumentException(L.T("DvdCal.WrongChart"));
        JsonObject current = BuildDvdGeometry();
        foreach (var pair in current)
            if (Math.Abs(double.Parse(pair.Value!.ToJsonString(), CultureInfo.InvariantCulture) - chart["geometry"]![pair.Key]!.GetValue<double>()) > 1e-8)
                throw new ArgumentException(L.T("DvdCal.WrongGeometry"));
        if (!IsDvdChart) _dvdPriorImageOutputPath = _shell.State.DvdOutputPath;
        _dvdChartPath = _planPath = Path.GetFullPath(path); _calibration = true;
        _shell.State.DvdOutputPath = Path.Combine(Path.GetDirectoryName(_dvdChartPath)!, "calibration-43533.iso");
        _isoPath = _isoSignature = null;
        Prediction = TargetPreview = ErrorPreview = NativeDitherPreview = TexturePreview = null;
        DvdViewingSummary = string.Empty;
        _planSignature = Signature(); IsPreviewCurrent = true; UseForExport = true;
        ResultSummary = L.T("DvdCal.ChartWarning") + " · " + path;
        RefreshDvdBindings(); NotifyCommands();
    }

    internal void ApplyDvdProfile(string path, JsonNode validation)
    {
        LeaveDvdChart();
        DvdProfilePath = Path.GetFullPath(path);
        DvdProfileSynthetic = validation["calibration"]?["synthetic"]?.GetValue<bool>() == true;
        DvdProfileExperimental = validation["calibration"]?["experimental"]?.GetValue<bool>() == true;
        double center = (validation["windowStartDeg"]!.GetValue<double>() + validation["windowEndDeg"]!.GetValue<double>()) / 2;
        DvdGroups.SelectMany(g => g.Fields).Single(f => f.Path == "centerBeta").Value = center.ToString("G", CultureInfo.InvariantCulture);
        AllowUnmeasured = false; InvalidatePreview(); RefreshDvdBindings(); NotifyCommands();
    }

    internal void ClearDvdProfile()
    {
        LeaveDvdChart(); DvdProfilePath = string.Empty; DvdProfileSynthetic = false; DvdProfileExperimental = false;
        AllowUnmeasured = false; InvalidatePreview(); RefreshDvdBindings(); NotifyCommands();
    }

    private void LeaveDvdChart()
    {
        bool chart = IsDvdChart;
        _dvdChartPath = null; _calibration = false;
        if (chart && _dvdPriorImageOutputPath is not null) _shell.State.DvdOutputPath = _dvdPriorImageOutputPath;
        _dvdPriorImageOutputPath = null;
    }
}
