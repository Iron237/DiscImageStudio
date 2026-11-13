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
    private readonly DvdColorEngine _dvdEngine = new();
    private const string DvdPlanSchema = "dis-dvd-fullcolor-plan-v1";
    public ObservableCollection<FullColorGroup> DvdGroups { get; } = [];
    private IEnumerable<FullColorField> DvdFields => DvdGroups.SelectMany(g => g.Fields);
    public IEnumerable<FullColorGroup> ActiveGroups => IsDvdProject ? DvdGroups : Groups;
    public bool IsDvdProject => _shell.IsProjectOpen && _shell.CurrentProject?.IsDvd == true;
    public bool IsSupportedColorProject => IsBluRayProject || IsDvdProject;
    public string MediaBadge => IsDvdProject ? "DVD-R · 43533" : "BD-R · 64099";
    public string ColorWarning => L.T(IsDvdProject ? "FullColor.DvdWarning" : "FullColor.Warning");
    public string ColorGeometryHint => L.T(IsDvdProject ? "FullColor.DvdGeometry" : "FullColor.GeometryOnly");
    public string ColorAcceptLabel => L.T(IsDvdProject ? "FullColor.DvdAccept" : "FullColor.Accept");
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
            nameof(ColorOutputHint), nameof(ColorDesignHint), nameof(ColorToneSummary) }) OnPropertyChanged(property);
    }

    private JsonObject BuildDvdConfiguration()
    {
        var state = _shell.State;
        double Read(string text) => ParameterParser.ParseDouble(text, "DVD geometry");
        JsonObject configuration = new()
        {
            ["autoViewing"] = DvdAutoViewing, ["preferDark"] = DvdPreferDark,
            ["geometry"] = new JsonObject { ["innerRadiusMm"] = Read(state.DvdInnerRadius),
                ["outerRadiusMm"] = Read(state.DvdOuterRadius),
                ["sectors"] = ParameterParser.ParsePositiveLong(state.DvdTotalSectors, "sectors"),
                ["pitchLinear"] = Read(state.DvdPitchLinear), ["pitchQuadratic"] = Read(state.DvdPitchQuadratic),
                ["pitchCubic"] = Read(state.DvdPitchCubic) },
        };
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

    private void ApplyDvdConfiguration(JsonNode configuration)
    {
        foreach (FullColorField field in DvdFields)
            field.Value = configuration[field.Path]!.GetValue<double>().ToString("G", CultureInfo.InvariantCulture);
        DvdAutoViewing = configuration["autoViewing"]!.GetValue<bool>();
        DvdPreferDark = configuration["preferDark"]!.GetValue<bool>();
    }

    private void RequireDvdGeometry()
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
        RequireDvdGeometry();
        JsonObject plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (plan["schema"]?.GetValue<string>() != DvdPlanSchema
            || plan["designSource"]?["fingerprint"]?.GetValue<string>() != DesignFingerprint())
            throw new ArgumentException(L.T("FullColor.WrongDesign"));
        if (applyConfiguration) ApplyDvdConfiguration(plan["configuration"]!);
        _planPath = Path.GetFullPath(path); _calibration = false;
        string directory = Path.GetDirectoryName(_planPath)!;
        TargetPreview = ReadBitmap(Path.Combine(directory, "target.png"));
        InputPreview ??= TargetPreview;
        Prediction = ReadBitmap(Path.Combine(directory, "predicted.png"));
        ErrorPreview = NativeDitherPreview = TexturePreview = null;
        DvdViewingSummary = string.Join(Environment.NewLine, plan["viewing"]!.AsArray().Select(v =>
            v!["name"]!.GetValue<string>() + " · " + L.T("FullColor.DvdViewingRotation") + " "
            + v["viewingRotationDeg"]!.GetValue<double>().ToString("F1", CultureInfo.InvariantCulture) + "°"));
        ResultSummary = "DCS · 95058 → 43533 · " + directory;
        _isoPath = _isoSignature = null;
        _planSignature = Signature(); IsPreviewCurrent = true; UseForExport = true; NotifyCommands();
    }

    private async Task<bool> ExportDvdAsync(string output, bool overwriteConfirmed)
    {
        RequireDvdGeometry();
        if (!IsPreviewCurrent || _planPath is null || _planSignature != Signature()) throw new InvalidOperationException(L.T("FullColor.Stale"));
        if (!AllowUnmeasured) throw new InvalidOperationException(L.T("FullColor.DvdAcceptRequired"));
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
}
