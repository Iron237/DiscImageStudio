using DiscImageStudio.Core;
using DiscImageStudio.ImageIO;

namespace DiscImageStudio.Services;

/// <summary>Uses DIS's native tone curve before color quantization. The monochrome
/// reference runs the actual BD loader; RGB channels retain color for the optical fit.</summary>
internal static class FullColorDesignPreprocessor
{
    internal static void Prepare(string source, string colorPath, string binaryPath,
        ToneProfile tone, CancellationToken cancellationToken)
    {
        tone.Validate();
        BgraRaster frame = RasterCodec.LoadBgra32(source, 256L * 1024 * 1024, 64L * 1024 * 1024);
        byte[] pixels = frame.Pixels;
        byte[] curve = Enumerable.Range(0, 256).Select(i => tone.AdjustedGray((byte)i)).ToArray();
        for (int pixel = 0; pixel < frame.Width * frame.Height; pixel++)
        {
            if ((pixel & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            int offset = pixel * 4, alpha = pixels[offset + 3];
            for (int channel = 0; channel < 3; channel++)
                pixels[offset + channel] = curve[(pixels[offset + channel] * alpha + 255 * (255 - alpha) + 127) / 255];
            pixels[offset + 3] = 255;
        }
        RasterCodec.SavePng(colorPath, frame.Width, frame.Height, pixels, grayscale: false);
        var gray = BluRayImageLoader.Load(source, tone, cancellationToken);
        byte[] binary = gray.Pixels.ToArray();
        // Legacy BD defers its final hard threshold to the payload mapping.
        if (tone.IsLegacyBinary)
            for (int i = 0; i < binary.Length; i++) binary[i] = binary[i] >= 128 ? (byte)255 : (byte)0;
        RasterCodec.SavePng(binaryPath, gray.Width, gray.Height, binary, grayscale: true);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
