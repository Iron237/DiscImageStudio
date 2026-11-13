using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media.Imaging;
using DiscImageStudio.Core;

namespace DiscImageStudio.Services;

internal sealed record DvdColorLayerSnapshot(string Id, string Name, DesignLayerRenderItem Render);

/// <summary>Runs the pinned DCS measured-color engine in its own process. Its old
/// DVD assemblies stay isolated from the current DIS assemblies. No recorder access.</summary>
internal sealed class DvdColorEngine
{
    internal static string WorkerDirectory => Path.Combine(AppContext.BaseDirectory, "DvdColorWorker");

    internal static void WriteLayers(JsonObject request, IReadOnlyList<DvdColorLayerSnapshot> layers)
    {
        string directory = request["directory"]!.GetValue<string>();
        Directory.CreateDirectory(Path.Combine(directory, "layers"));
        JsonArray files = [];
        foreach (DvdColorLayerSnapshot layer in layers)
        {
            if (!Guid.TryParseExact(layer.Id, "N", out _)) throw new ArgumentException("Invalid design layer identifier.");
            string relative = "layers/" + layer.Id + ".png";
            PngBitmapEncoder encoder = new();
            encoder.Frames.Add(BitmapFrame.Create(layer.Render.Image));
            using (FileStream file = File.Create(Path.Combine(directory, relative))) encoder.Save(file);
            var item = layer.Render;
            files.Add(new JsonObject { ["id"] = layer.Id, ["name"] = layer.Name, ["file"] = relative,
                ["x"] = item.TranslateXMm, ["y"] = item.TranslateYMm,
                ["width"] = item.BaseWidthMm * item.Scale, ["height"] = item.BaseHeightMm * item.Scale,
                ["rotation"] = item.RotationDeg });
        }
        request["layers"] = files;
    }

    internal DiscImageResult Run(JsonObject request, string expectedOutput,
        IProgress<DiscJobProgress> progress, CancellationToken cancellationToken)
    {
        string worker = Path.Combine(WorkerDirectory, "DiscColorStudio.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("The DVD measured-color worker is missing. Rebuild the development app directory.", worker);
        string root = request["workRoot"]!.GetValue<string>();
        Directory.CreateDirectory(root);
        string requestFile = Path.Combine(root, "dvd-request-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(requestFile, request.ToJsonString(), new UTF8Encoding(false));
        try
        {
            ProcessStartInfo start = new(worker)
            {
                WorkingDirectory = WorkerDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("--dis-worker"); start.ArgumentList.Add(requestFile);
            using Process process = new() { StartInfo = start };
            cancellationToken.ThrowIfCancellationRequested();
            process.Start();
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            Task<string> errorRead = process.StandardError.ReadToEndAsync();
            Queue<string> messages = new();
            try
            {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                using JsonDocument message = JsonDocument.Parse(line);
                string text = message.RootElement.GetProperty("message").GetString() ?? string.Empty;
                JsonElement fraction = message.RootElement.GetProperty("fraction");
                progress.Report(new("dvd-full-color", "generate", text,
                    fraction.ValueKind == JsonValueKind.Number ? fraction.GetDouble() : null));
                messages.Enqueue(text); if (messages.Count > 60) messages.Dequeue();
            }
            process.WaitForExit();
            string errors = errorRead.GetAwaiter().GetResult();
            cancellationToken.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, messages) + Environment.NewLine + errors);
            if (!File.Exists(expectedOutput)) throw new FileNotFoundException("DVD color output is missing.", expectedOutput);
            return new(expectedOutput, string.Join(Environment.NewLine, messages));
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
        finally { File.Delete(requestFile); }
    }
}
