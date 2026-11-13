using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DiscImageStudio.Core;

namespace DiscImageStudio.Services;

/// <summary>File-only bridge to the isolated experimental BD color engine. The shell owns
/// progress/cancellation; the Python process never opens a recorder.</summary>
internal sealed class FullColorEngine
{
    internal static string EngineDirectory => Path.Combine(AppContext.BaseDirectory, "FullColorEngine");
    internal static string DefaultPythonPath => Environment.GetEnvironmentVariable("DIS_FULLCOLOR_PYTHON")
        ?? Path.Combine(AppContext.BaseDirectory, "FullColorRuntime", "Scripts", "python.exe");

    internal DiscImageResult Run(string pythonPath, JsonObject request, string expectedOutput,
        IProgress<DiscJobProgress> progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(pythonPath))
            throw new FileNotFoundException("Full Color Python runtime is missing. Run tools/setup-fullcolor.ps1 or select an installed runtime.", pythonPath);
        string worker = Path.Combine(EngineDirectory, "worker.py");
        if (!File.Exists(worker)) throw new FileNotFoundException("Full Color engine files are missing.", worker);
        string root = request["workRoot"]!.GetValue<string>();
        Directory.CreateDirectory(root);
        string requestPath = Path.Combine(root, "request-" + Guid.NewGuid().ToString("N") + ".json");
        string colorPath = requestPath + ".color.png", binaryPath = requestPath + ".binary.png";
        try
        {
            if (request["operation"]?.GetValue<string>() == "preview")
            {
                JsonNode t = request["configuration"]!["tone"]!;
                ToneProfile tone = new(t["ditherStrength"]!.GetValue<double>(), ToneProfile.TwoLevels,
                    t["brightness"]!.GetValue<double>(), t["srgbLinearization"]!.GetValue<bool>());
                progress.Report(new("full-color", "generate", "DIS: design → white alpha → native tone/dither", null));
                FullColorDesignPreprocessor.Prepare(request["inputPath"]!.GetValue<string>(),
                    colorPath, binaryPath, tone, cancellationToken);
                request["toneInputPath"] = colorPath;
                request["nativeDitherPath"] = binaryPath;
            }
            File.WriteAllText(requestPath, request.ToJsonString(), new UTF8Encoding(false));
            ProcessStartInfo start = new(pythonPath)
            {
                WorkingDirectory = EngineDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add("-u");
            start.ArgumentList.Add(worker);
            start.ArgumentList.Add(requestPath);
            start.Environment["PYTHONIOENCODING"] = "utf-8";
            start.Environment["OPENBLAS_NUM_THREADS"] = "1";
            StringBuilder errors = new();
            Queue<string> lines = new();
            using Process process = new() { StartInfo = start };
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Start();
                using CancellationTokenRegistration registration = cancellationToken.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                });
                Task<string> errorRead = process.StandardError.ReadToEndAsync();
                while (process.StandardOutput.ReadLine() is { } line)
                {
                    using JsonDocument message = JsonDocument.Parse(line);
                    string text = message.RootElement.GetProperty("message").GetString() ?? string.Empty;
                    JsonElement fraction = message.RootElement.GetProperty("fraction");
                    progress.Report(new("full-color", "generate", text,
                        fraction.ValueKind == JsonValueKind.Number ? fraction.GetDouble() : null));
                    lines.Enqueue(text);
                    if (lines.Count > 80) lines.Dequeue();
                }
                process.WaitForExit();
                errors.Append(errorRead.GetAwaiter().GetResult());
                cancellationToken.ThrowIfCancellationRequested();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(string.Join(Environment.NewLine, lines) + Environment.NewLine + errors);
                if (!File.Exists(expectedOutput)) throw new FileNotFoundException("The color engine did not produce its output.", expectedOutput);
                return new(expectedOutput, string.Join(Environment.NewLine, lines));
            }
            finally
            {
                try { if (process.Id != 0 && !process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                File.Delete(requestPath);
            }
        }
        finally
        {
            File.Delete(colorPath);
            File.Delete(binaryPath);
        }
    }
}
