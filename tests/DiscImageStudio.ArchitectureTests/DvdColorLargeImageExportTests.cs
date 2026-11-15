using System.IO;
using System.Text.Json.Nodes;
using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;
using DiscImageStudio.Services;

internal static class DvdColorLargeImageExportTests
{
    internal static void Run() => WpfTestHost.Run(() =>
    {
        string directory = Path.Combine(Path.GetTempPath(), "dis-dvd-large-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(directory, "layers"));
        try
        {
            // A photo enlarged beyond the disc and rotated slightly: the same trigger
            // as the 32768-grid export failure, without bundling the user's artwork.
            const int width = 4096, height = 2295;
            byte[] pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int at = (y * width + x) * 4;
                    pixels[at] = (byte)(x / 16); pixels[at + 1] = (byte)(y / 9);
                    pixels[at + 2] = (byte)(x / 17 + y / 13); pixels[at + 3] = 255;
                }
            RasterCodec.SavePng(Path.Combine(directory, "layers", "photo.png"), width, height, pixels, grayscale: false);
            DvdColorEngine engine = new();
            string plan = Path.Combine(directory, "plan.json");
            JsonObject configuration = new()
            {
                ["autoViewing"] = false, ["centerBeta"] = 94, ["preferDark"] = false,
                ["sampleSize"] = 32768, ["exposure"] = 100, ["brightness"] = 0, ["contrast"] = 100,
                ["smoothing"] = 100, ["mixing"] = 100,
                ["geometry"] = new JsonObject
                {
                    ["innerRadiusMm"] = 23.991460627731737, ["outerRadiusMm"] = 57.964893657925366,
                    ["sectors"] = 2297888, ["pitchLinear"] = -3.9256854319227684E-05,
                    ["pitchQuadratic"] = 6.964795915429256E-05, ["pitchCubic"] = -4.670918950137523E-05,
                },
            };
            JsonObject preview = new()
            {
                ["operation"] = "preview", ["workRoot"] = directory, ["directory"] = directory,
                ["configuration"] = configuration, ["designSource"] = new JsonObject { ["fingerprint"] = "large-image-fixture" },
                ["layers"] = new JsonArray(new JsonObject
                {
                    ["id"] = "photo", ["name"] = "enlarged rotated photo", ["file"] = "layers/photo.png",
                    ["x"] = -50.70983314479631, ["y"] = -8.268750000000004,
                    ["width"] = 214.75163100573405, ["height"] = 120.32592606400381, ["rotation"] = 11.9,
                }),
            };
            engine.Run(preview, plan, new QuietProgress(), CancellationToken.None);
            JsonObject snapshot = JsonNode.Parse(File.ReadAllText(plan))!.AsObject();
            foreach (int size in new[] { 32768, 16384, 65536 })
            {
                snapshot["configuration"]!["sampleSize"] = size;
                string sizedPlan = Path.Combine(directory, $"raster-{size}.json");
                File.WriteAllText(sizedPlan, snapshot.ToJsonString());
                string serial = Path.Combine(directory, $"serial-{size}.bin"), parallel = Path.Combine(directory, $"parallel-{size}.bin");
                foreach (var output in new[] { (Path: serial, Workers: 1), (Path: parallel, Workers: 24) })
                {
                    JsonObject request = new()
                    {
                        ["operation"] = "encode-fragment", ["workRoot"] = directory, ["planPath"] = sizedPlan,
                        ["outputPath"] = output.Path, ["workers"] = output.Workers, ["startSector"] = 0u, ["sectors"] = 64u,
                    };
                    engine.Run(request, output.Path, new QuietProgress(), CancellationToken.None);
                }
                if (new FileInfo(serial).Length != 64 * 2048 || !File.ReadAllBytes(serial).SequenceEqual(File.ReadAllBytes(parallel)))
                    throw new InvalidOperationException($"Enlarged rotated photo has incomplete or nondeterministic output at {size}.");
            }
        }
        finally
        {
            string full = Path.GetFullPath(directory);
            if (!full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(full).StartsWith("dis-dvd-large-image-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected fixture cleanup directory.");
            Directory.Delete(full, recursive: true);
        }
        Console.WriteLine("dvd-large-image-export: passed (enlarged rotated 4096x2295 photo, 16384/32768/65536 grids, byte-identical 1/24-worker fragments; no recorder)");
    });

    private sealed class QuietProgress : IProgress<DiscJobProgress> { public void Report(DiscJobProgress value) { } }
}
