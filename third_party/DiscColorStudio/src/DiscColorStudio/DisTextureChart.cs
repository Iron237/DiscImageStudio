using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DvdImageSolver.Solver;

namespace DiscColorStudio;

/// <summary>Known physical motifs through the same DVD payload/EFMPlus pipeline.</summary>
internal sealed class DisTextureChart
{
    internal const string Schema = "dis-dvd-texture-chart-v1";
    private sealed record Patch(int[] Ids, double[] Cdf);
    private sealed record Marker(double X, double Y, double Size, double Quiet, string[] Bits,
        double LabelX, double LabelY, double LabelCell, string[] LabelBits);
    private readonly byte[][] _motifs;
    private readonly Dictionary<(int, int), Patch> _patches = new();
    private readonly Marker[] _markers;
    private readonly double _pitch, _half;
    private readonly uint _seed;
    internal JsonObject Plan { get; }
    internal Geometry Geometry { get; }

    internal DisTextureChart(string path)
    {
        Plan = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        if (Plan["schema"]?.GetValue<string>() != Schema) throw new InvalidDataException("不是 DVD 纹理标定盘。");
        Geometry = Plan["geometry"]!.Deserialize<Geometry>(Design.Json)!;
        if (Geometry.Sectors != 2297888 || Geometry.InnerRadiusMm <= 0 || Geometry.OuterRadiusMm <= Geometry.InnerRadiusMm || Geometry.OuterRadiusMm > 60)
            throw new InvalidDataException("标定盘的 43533 几何无效。");
        _ = DiscImageStudio.Core.DvdTrackGeometry.Create(Geometry.InnerRadiusMm, Geometry.OuterRadiusMm,
            Geometry.Sectors * 38688 * .0001333, Geometry.PitchLinear, Geometry.PitchQuadratic, Geometry.PitchCubic);
        _pitch = Plan["pitchMm"]!.GetValue<double>(); _half = Plan["patchSizeMm"]!.GetValue<double>() / 2;
        if (!double.IsFinite(_pitch) || !double.IsFinite(_half) || _pitch is < 1 or > 5 || _half <= 0 || _half * 2 >= _pitch)
            throw new InvalidDataException("纹理格尺寸无效。");
        _seed = Plan["ditherSeed"]!.GetValue<uint>();
        var textures = Plan["textures"]!.AsArray();
        var original = JsonNode.Parse(File.ReadAllText(ContinuousColorMap.DefaultPath))!["textures"]!.AsArray();
        if (textures.Count != 1154) throw new InvalidDataException("纹理清单必须包含 1154 种已知纹理。");
        _motifs = textures.Select((t, i) =>
        {
            if (t!["index"]!.GetValue<int>() != i || t["motif"]!.GetValue<string>() != original[i]!["motif"]!.GetValue<string>())
                throw new InvalidDataException("标定纹理与 DCS 原始字节序列不一致。");
            return Convert.FromHexString(t["motif"]!.GetValue<string>());
        }).ToArray();
        foreach (JsonNode? patch in Plan["patches"]!.AsArray())
        {
            int gx = patch!["gridX"]!.GetValue<int>(), gy = patch["gridY"]!.GetValue<int>();
            if (Math.Abs(patch["x"]!.GetValue<double>() - gx * _pitch) > 1e-8
                || Math.Abs(patch["y"]!.GetValue<double>() - gy * _pitch) > 1e-8)
                throw new InvalidDataException("纹理清单坐标与写入网格不一致。");
            foreach (double dx in new[] { -_half, _half }) foreach (double dy in new[] { -_half, _half })
            {
                double radius = Math.Sqrt(Math.Pow(gx * _pitch + dx, 2) + Math.Pow(gy * _pitch + dy, 2));
                if (radius < Geometry.InnerRadiusMm || radius > Geometry.OuterRadiusMm) throw new InvalidDataException("纹理格超出绘图环带。");
            }
            var components = patch["components"]!.AsArray();
            int[] ids = components.Select(c => c!["texture"]!.GetValue<int>()).ToArray();
            double[] weights = components.Select(c => c!["weight"]!.GetValue<double>()).ToArray();
            if (ids.Length is < 1 or > 16 || ids.Any(i => i is < 0 or >= 1154)
                || weights.Any(w => !double.IsFinite(w) || w <= 0) || Math.Abs(weights.Sum() - 1) > 1e-8)
                throw new InvalidDataException("纹理格的混色权重无效。");
            double sum = 0;
            _patches.Add((gx, gy), new(ids, weights.Select(w => sum += w).ToArray()));
        }
        _markers = Plan["markers"]!.AsArray().Select(m => new Marker(m!["x"]!.GetValue<double>(), m["y"]!.GetValue<double>(),
            m["size"]!.GetValue<double>(), m["quiet"]!.GetValue<double>(), m["bits"]!.AsArray().Select(b => b!.GetValue<string>()).ToArray(),
            m["labelX"]?.GetValue<double>() ?? 0, m["labelY"]?.GetValue<double>() ?? 0, m["labelCellMm"]?.GetValue<double>() ?? .5,
            m["labelBits"]?.AsArray().Select(b => b!.GetValue<string>()).ToArray() ?? [])).ToArray();
        if (_markers.Length != 4 || _markers.Any(m => !double.IsFinite(m.X + m.Y + m.Size + m.Quiet)
            || m.Size is < 2 or > 8 || m.Quiet is < .2 or > 2 || m.Bits.Length != 6
            || m.Bits.Any(b => b.Length != 6 || b.Any(c => c is not ('0' or '1')))
            || !double.IsFinite(m.LabelX + m.LabelY + m.LabelCell) || m.LabelCell is < .25 or > 1
            || (m.LabelBits.Length != 0 && (m.LabelBits.Length != 7 || m.LabelBits.Any(b => b.Length != 5 || b.Any(c => c is not ('0' or '1')))))))
            throw new InvalidDataException("标定盘定位码无效。");
    }

    internal byte Sample(DvdCodewordSample sample)
    {
        ulong logical = DvdImageEncoder.Logical(sample.GlobalCodewordIndex);
        int texture = 1152;
        foreach (Marker marker in _markers)
        {
            double x = sample.Xmm - marker.X, y = sample.Ymm - marker.Y;
            if (marker.LabelBits.Length == 7)
            {
                int lx = (int)Math.Floor((sample.Xmm - marker.LabelX) / marker.LabelCell + 2.5);
                int ly = (int)Math.Floor((sample.Ymm - marker.LabelY) / marker.LabelCell + 3.5);
                if (lx is >= 0 and < 5 && ly is >= 0 and < 7)
                {
                    int id = marker.LabelBits[ly][lx] == '1' ? 1152 : 1153;
                    return _motifs[id][logical % (ulong)_motifs[id].Length];
                }
            }
            if (Math.Abs(x) > marker.Size / 2 + marker.Quiet || Math.Abs(y) > marker.Size / 2 + marker.Quiet) continue;
            if (Math.Abs(x) >= marker.Size / 2 || Math.Abs(y) >= marker.Size / 2) texture = 1153;
            else
            {
                int col = Math.Clamp((int)((x / marker.Size + .5) * 6), 0, 5);
                int row = Math.Clamp((int)((y / marker.Size + .5) * 6), 0, 5);
                texture = marker.Bits[row][col] == '1' ? 1153 : 1152;
            }
            return _motifs[texture][logical % (ulong)_motifs[texture].Length];
        }
        int gx = (int)Math.Round(sample.Xmm / _pitch), gy = (int)Math.Round(sample.Ymm / _pitch);
        if (Math.Abs(sample.Xmm - gx * _pitch) <= _half && Math.Abs(sample.Ymm - gy * _pitch) <= _half
            && _patches.TryGetValue((gx, gy), out Patch? patch))
        {
            ulong turn = checked((ulong)Math.Floor(sample.TrackAngleRadians / (2 * Math.PI)));
            double u = ((unchecked((uint)turn * 40503u + _seed) & 65535u) + .5) / 65536;
            int index = 0;
            while (index < patch.Cdf.Length - 1 && u >= patch.Cdf[index]) index++;
            texture = patch.Ids[index];
        }
        byte[] motif = _motifs[texture];
        return motif[logical % (ulong)motif.Length];
    }

    internal void Write(Stream stream, uint start, uint count, IProgress<EncodingProgress>? progress, int workers)
    {
        var g = Geometry;
        var mapping = new ImageMappingOptions(checked((uint)g.Sectors), g.InnerRadiusMm, g.OuterRadiusMm, 133.3, 0, true,
            128, 1, PitchLinear: g.PitchLinear, PitchQuadratic: g.PitchQuadratic, PitchCubic: g.PitchCubic);
        var options = new MultiBlockSolveOptions(start, count, 0x30000, 1, 4, 1, 64, .25, false, mapping,
            Algorithm: SolverAlgorithm.Dispersion, FastParallelism: Math.Clamp(workers, 1, 64));
        long last = 0;
        FastDispersionImageWriter.WriteScrambledCodewordPatternStream(Sample, stream, options, p =>
        {
            long now = Environment.TickCount64;
            if (now - last < 250 && p.CompletedBlocks != p.TotalBlocks) return;
            last = now; progress?.Report(new("生成 DVD 纹理标定镜像 " + p.Fraction.ToString("P1"), .97 * p.Fraction));
        });
    }

    internal EncodedImage Generate(string path, IProgress<EncodingProgress> progress, int workers)
    {
        long bytes = checked(Geometry.Sectors * 2048);
        if (new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace < bytes + 128L * 1024 * 1024)
            throw new IOException("标定镜像所在磁盘空间不足。需要完整 DVD 容量和临时提交空间。");
        string temporary = path + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20))
            {
                Write(output, 0, checked((uint)Geometry.Sectors), progress, workers);
                output.Flush(true);
                if (output.Length != bytes) throw new InvalidDataException("标定镜像长度不完整。");
            }
            progress.Report(new("检查完整标定镜像 SHA-256", .98));
            string digest = DisWorker.Hash(temporary);
            File.Move(temporary, path);
            return new(path, bytes, digest);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
