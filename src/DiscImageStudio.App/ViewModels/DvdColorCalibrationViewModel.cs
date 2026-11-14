using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiscImageStudio.Localization;
using DiscImageStudio.Services;
using DiscImageStudio.Views;

namespace DiscImageStudio.ViewModels;

public sealed class DvdCalibrationFrame(JsonNode frame)
{
    public string Id { get; } = frame["id"]!.GetValue<string>();
    public string Path { get; } = frame["path"]!.GetValue<string>();
    public string Name => System.IO.Path.GetFileName(Path);
    public string PreviewPath { get; } = frame["previewPath"]!.GetValue<string>();
    public string Status { get; } = frame["status"]?.GetValue<string>() ?? L.T("DvdCal.NeedsRegistration");
    public string Rotation { get; } = (Math.Round(frame["rotationDeg"]?.GetValue<double>() ?? 0, 2) % 360).ToString("F2", CultureInfo.InvariantCulture) + "°";
    public int ValidPatches { get; } = frame["validPatches"]?.GetValue<int>() ?? 0;
}

/// <summary>The file-only one-disc measurement workflow. Physical writing uses the normal DIS burn page.</summary>
public partial class DvdColorCalibrationViewModel : ObservableObject
{
    private readonly FullColorViewModel _parent;
    private readonly ShellViewModel _shell;
    private readonly FullColorEngine _python = new();
    private readonly DvdColorEngine _dvd = new();
    public ObservableCollection<DvdCalibrationFrame> Frames { get; } = [];
    [ObservableProperty] private string _chartPath = string.Empty;
    [ObservableProperty] private string _sessionPath = string.Empty;
    [ObservableProperty] private string _builtProfilePath = string.Empty;
    [ObservableProperty] private string _captureNotes = string.Empty;
    [ObservableProperty] private string _windowWidth = "60";
    [ObservableProperty] private string _minimumCoverage = "90";
    [ObservableProperty] private string _summary = string.Empty;
    [ObservableProperty] private string _qualityText = string.Empty;
    [ObservableProperty] private BitmapSource? _chartPreview;
    [ObservableProperty] private BitmapSource? _photoPreview;
    [ObservableProperty] private BitmapSource? _coveragePreview;
    [ObservableProperty] private DvdCalibrationFrame? _selectedFrame;
    [ObservableProperty] private bool _isExpanded;
    public bool CanEdit => _parent.CanEdit && _parent.IsDvdProject;
    public bool HasChart => File.Exists(ChartPath);
    public bool HasFrames => Frames.Count > 0;
    public bool HasPhoto => PhotoPreview is not null;
    public bool HasReport => !string.IsNullOrWhiteSpace(QualityText);
    public bool HasCoverage => CoveragePreview is not null;
    public string ActiveProfileSummary => _parent.DvdProfileSummary;
    public string PythonPath { get => _parent.PythonPath; set => _parent.PythonPath = value; }
    public IRelayCommand BrowsePythonCommand => _parent.BrowsePythonCommand;
    public IAsyncRelayCommand CreateChartCommand { get; }
    public IRelayCommand OpenChartCommand { get; }
    public IAsyncRelayCommand ExportChartCommand { get; }
    public IAsyncRelayCommand ImportPhotosCommand { get; }
    public IRelayCommand OpenSessionCommand { get; }
    public IAsyncRelayCommand RegisterPhotoCommand { get; }
    public IAsyncRelayCommand BuildProfileCommand { get; }
    public IAsyncRelayCommand ApplyProfileCommand { get; }
    public IAsyncRelayCommand BrowseProfileCommand { get; }
    public IRelayCommand BuiltinProfileCommand { get; }

    internal DvdColorCalibrationViewModel(FullColorViewModel parent, ShellViewModel shell)
    {
        _parent = parent; _shell = shell;
        Frames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasFrames));
        parent.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FullColorViewModel.PythonPath)) OnPropertyChanged(nameof(PythonPath));
            if (args.PropertyName == nameof(FullColorViewModel.DvdProfileSummary)) OnPropertyChanged(nameof(ActiveProfileSummary));
        };
        CreateChartCommand = new AsyncRelayCommand(CreateChartAsync, () => CanEdit);
        OpenChartCommand = new RelayCommand(() => PickChart(), () => CanEdit);
        ExportChartCommand = new AsyncRelayCommand(ExportChartAsync, () => CanEdit && HasChart);
        ImportPhotosCommand = new AsyncRelayCommand(PickPhotosAsync, () => CanEdit && HasChart);
        OpenSessionCommand = new RelayCommand(() => PickSession(), () => CanEdit);
        RegisterPhotoCommand = new AsyncRelayCommand(RegisterSelectedAsync, () => CanEdit && SelectedFrame is not null && File.Exists(SessionPath));
        BuildProfileCommand = new AsyncRelayCommand(BuildProfileAsync, () => CanEdit && Frames.Count > 0 && File.Exists(SessionPath));
        ApplyProfileCommand = new AsyncRelayCommand(() => ApplyProfileAsync(BuiltProfilePath), () => CanEdit && File.Exists(BuiltProfilePath));
        BrowseProfileCommand = new AsyncRelayCommand(BrowseProfileAsync, () => CanEdit);
        BuiltinProfileCommand = new RelayCommand(() => { _parent.ClearDvdProfile(); Refresh(); }, () => CanEdit);
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(HasChart)); OnPropertyChanged(nameof(ActiveProfileSummary));
        foreach (IRelayCommand command in new IRelayCommand[] { CreateChartCommand, OpenChartCommand, ExportChartCommand,
            ImportPhotosCommand, OpenSessionCommand, RegisterPhotoCommand, BuildProfileCommand, ApplyProfileCommand,
            BrowseProfileCommand, BuiltinProfileCommand }) command.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFrameChanged(DvdCalibrationFrame? value)
    {
        PhotoPreview = FullColorViewModel.ReadBitmap(value?.PreviewPath ?? string.Empty);
        Refresh();
    }
    partial void OnPhotoPreviewChanged(BitmapSource? value) => OnPropertyChanged(nameof(HasPhoto));
    partial void OnQualityTextChanged(string value) => OnPropertyChanged(nameof(HasReport));
    partial void OnCoveragePreviewChanged(BitmapSource? value) => OnPropertyChanged(nameof(HasCoverage));

    private void Fail(Exception error) { Summary = error.Message; Refresh(); }
    private static string NewName(string prefix) => prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
    private JsonObject Capture() => new() { ["media"] = "Verbatim DVD-R 43533", ["intendedWriteSpeed"] = "4x", ["notes"] = CaptureNotes };

    internal async Task CreateChartAsync()
    {
        try
        {
            _parent.RequireDvdGeometry();
            string directory = System.IO.Path.Combine(System.IO.Path.GetFullPath(_parent.OutputRoot), NewName("dvdcal-43533"));
            JsonObject request = new() { ["operation"] = "dvdcal-create", ["workRoot"] = _parent.OutputRoot,
                ["directory"] = directory, ["geometry"] = _parent.BuildDvdGeometry(), ["capture"] = Capture(),
                ["sourceProfilePath"] = System.IO.Path.Combine(DvdColorEngine.WorkerDirectory, "data", "continuous-color-map.json") };
            string expected = System.IO.Path.Combine(directory, "chart.json");
            if (await PythonJob(L.T("DvdCal.CreateChart"), request, expected)) LoadChart(expected);
        }
        catch (Exception error) { Fail(error); }
    }

    private Task<bool> PythonJob(string title, JsonObject request, string expected)
        => _shell.RunImageJobAsync(title, expected, (progress, token) => _python.Run(_parent.PythonPath, request, expected, progress, token), canPreviewOutput: false);

    private void PickChart()
    {
        if (_shell.Dialogs?.PickOpen(L.T("DvdCal.OpenChart"), "DVD calibration chart|chart.json") is not { } path) return;
        try { LoadChart(path); } catch (Exception error) { Fail(error); }
    }

    internal void LoadChart(string path)
    {
        _parent.SelectDvdChart(path);
        string full = System.IO.Path.GetFullPath(path);
        if (!string.Equals(ChartPath, full, StringComparison.OrdinalIgnoreCase))
        {
            SessionPath = BuiltProfilePath = QualityText = string.Empty;
            Frames.Clear(); SelectedFrame = null; CoveragePreview = null;
        }
        ChartPath = full;
        IsExpanded = true;
        ChartPreview = FullColorViewModel.ReadBitmap(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(full)!, "layout.png"));
        Summary = L.T("DvdCal.ChartReady"); Refresh();
    }

    private async Task ExportChartAsync()
    {
        try
        {
            _parent.SelectDvdChart(ChartPath);
            string? output = _shell.Dialogs?.PickSave("DVD calibration ISO|*.iso", ".iso", "calibration-43533.iso");
            if (output is not null) await _parent.ExportToAsync(output, overwriteConfirmed: File.Exists(output));
        }
        catch (Exception error) { Fail(error); }
    }

    private async Task PickPhotosAsync()
    {
        string[]? files = _shell.Dialogs?.PickFiles(L.T("DvdCal.ImportPhotos"),
            "Original photos|*.dng;*.nef;*.cr2;*.cr3;*.arw;*.rw2;*.orf;*.raf;*.pef;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp");
        if (files is not null) await ImportPhotosAsync(files);
    }

    internal async Task ImportPhotosAsync(IReadOnlyList<string> files)
    {
        try
        {
            _parent.RequireDvdGeometry();
            string directory = File.Exists(SessionPath) ? System.IO.Path.GetDirectoryName(SessionPath)!
                : System.IO.Path.Combine(System.IO.Path.GetDirectoryName(ChartPath)!, NewName("measurements"));
            JsonObject request = new() { ["operation"] = "dvdcal-import", ["workRoot"] = _parent.OutputRoot,
                ["directory"] = directory, ["chartPath"] = ChartPath, ["capture"] = Capture(),
                ["photos"] = new JsonArray(files.Select(p => JsonValue.Create(p)).ToArray()) };
            string expected = System.IO.Path.Combine(directory, "session.json");
            if (await PythonJob(L.T("DvdCal.ImportPhotos"), request, expected)) LoadSession(expected);
        }
        catch (Exception error) { Fail(error); }
    }

    private void PickSession()
    {
        if (_shell.Dialogs?.PickOpen(L.T("DvdCal.OpenSession"), "DVD measurement session|session.json") is not { } path) return;
        try { LoadSession(path); } catch (Exception error) { Fail(error); }
    }

    internal void LoadSession(string path)
    {
        JsonNode session = JsonNode.Parse(File.ReadAllText(path))!;
        if (session["schema"]?.GetValue<string>() != "dis-dvd-color-session-v1") throw new ArgumentException(L.T("DvdCal.WrongSession"));
        string? previous = SelectedFrame?.Id;
        if (!string.Equals(ChartPath, session["chartPath"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
            LoadChart(session["chartPath"]!.GetValue<string>());
        SessionPath = System.IO.Path.GetFullPath(path);
        IsExpanded = true;
        Frames.Clear();
        foreach (JsonNode? frame in session["frames"]!.AsArray()) Frames.Add(new(frame!));
        SelectedFrame = Frames.FirstOrDefault(f => f.Id == previous) ?? Frames.FirstOrDefault();
        CaptureNotes = session["capture"]?["notes"]?.GetValue<string>() ?? CaptureNotes;
        Summary = session["summary"]?.GetValue<string>() ?? string.Empty;
        if (session["synthetic"]?.GetValue<bool>() == true) Summary = L.T("DvdCal.SyntheticSession") + " · " + Summary;
        string profiles = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SessionPath)!, "profiles");
        if (Directory.Exists(profiles))
        {
            string? latest = Directory.EnumerateFiles(profiles, "profile.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (latest is not null) { BuiltProfilePath = latest; LoadReport(System.IO.Path.GetDirectoryName(latest)!); }
        }
        Refresh();
    }

    private async Task RegisterSelectedAsync()
    {
        if (SelectedFrame is null) return;
        try
        {
            DvdPhotoRegistrationWindow dialog = new(SelectedFrame.PreviewPath);
            if (Application.Current?.MainWindow is { IsVisible: true } owner) dialog.Owner = owner;
            if (dialog.ShowDialog() != true) return;
            await RegisterPhotoAsync(SelectedFrame.Id, dialog.Points);
        }
        catch (Exception error) { Fail(error); }
    }

    internal async Task RegisterPhotoAsync(string id, IReadOnlyList<Point> points)
    {
        try
        {
            JsonObject request = new() { ["operation"] = "dvdcal-register", ["workRoot"] = _parent.OutputRoot,
                ["sessionPath"] = SessionPath, ["frameId"] = id,
                ["points"] = new JsonArray(points.Select(p => (JsonNode)new JsonArray(p.X, p.Y)).ToArray()) };
            if (await PythonJob(L.T("DvdCal.RegisterPhoto"), request, SessionPath)) LoadSession(SessionPath);
        }
        catch (Exception error) { Fail(error); }
    }

    internal async Task BuildProfileAsync()
    {
        try
        {
            int width = int.Parse(WindowWidth, CultureInfo.InvariantCulture);
            double coverage = double.Parse(MinimumCoverage, CultureInfo.InvariantCulture) / 100;
            if (width is < 5 or > 120 || !double.IsFinite(coverage) || coverage is < .5 or > 1) throw new ArgumentException(L.T("DvdCal.InvalidBuild"));
            string directory = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SessionPath)!, "profiles", NewName("profile"));
            JsonObject request = new() { ["operation"] = "dvdcal-build", ["workRoot"] = _parent.OutputRoot,
                ["sessionPath"] = SessionPath, ["directory"] = directory, ["windowWidthDeg"] = width,
                ["minimumCoverage"] = coverage, ["capture"] = Capture() };
            string expected = System.IO.Path.Combine(directory, "profile.json");
            bool success = await PythonJob(L.T("DvdCal.BuildProfile"), request, expected);
            BuiltProfilePath = success ? expected : string.Empty;
            LoadReport(directory); Refresh();
        }
        catch (Exception error) { Fail(error); }
    }

    private void LoadReport(string directory)
    {
        string text = System.IO.Path.Combine(directory, "quality.txt");
        QualityText = File.Exists(text) ? File.ReadAllText(text) : string.Empty;
        CoveragePreview = FullColorViewModel.ReadBitmap(System.IO.Path.Combine(directory, "coverage.png"));
    }

    private async Task BrowseProfileAsync()
    {
        if (_shell.Dialogs?.PickOpen(L.T("DvdCal.ImportProfile"), "Measured DVD profile|profile.json") is { } path)
            await ApplyProfileAsync(path);
    }

    internal async Task ApplyProfileAsync(string path)
    {
        try
        {
            _parent.RequireDvdGeometry();
            string output = System.IO.Path.Combine(System.IO.Path.GetFullPath(_parent.OutputRoot), NewName("profile-validation") + ".json");
            JsonObject request = new() { ["operation"] = "validate-profile", ["workRoot"] = _parent.OutputRoot,
                ["profilePath"] = System.IO.Path.GetFullPath(path), ["geometry"] = _parent.BuildDvdGeometry(), ["outputPath"] = output };
            if (await _shell.RunImageJobAsync(L.T("DvdCal.ApplyProfile"), output,
                (progress, token) => _dvd.Run(request, output, progress, token), canPreviewOutput: false))
            {
                _parent.ApplyDvdProfile(path, JsonNode.Parse(File.ReadAllText(output))!);
                IsExpanded = false;
                BuiltProfilePath = System.IO.Path.GetFullPath(path);
                Summary = L.T("DvdCal.Applied"); Refresh();
            }
        }
        catch (Exception error) { Fail(error); }
    }
}
