using QRCoder;

namespace VarlatchKit;

/// <summary>
/// A QR code of a device sign-in's address, for a phone's camera. The code
/// is typed in by hand, so only the address goes in.
/// </summary>
public static class QrCode
{
    /// <summary>The modules, row by row, true for dark, without a quiet zone.</summary>
    public static bool[,] Modules(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        // QRCoder keeps a four-module quiet zone around the matrix.
        const int quiet = 4;
        var size = data.ModuleMatrix.Count - 2 * quiet;
        var modules = new bool[size, size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                modules[y, x] = data.ModuleMatrix[y + quiet][x + quiet];
        return modules;
    }

    /// <summary>
    /// Black modules on white, <paramref name="pixelsPerModule"/> pixels each,
    /// with a white quiet zone of <paramref name="quietModules"/>: 32-bit BGRA
    /// pixels, row by row, and the side in pixels.
    /// </summary>
    public static (byte[] Pixels, int Side) Bgra(string text, int pixelsPerModule, int quietModules = 0)
    {
        var modules = Modules(text);
        var count = modules.GetLength(0);
        var side = (count + 2 * quietModules) * pixelsPerModule;
        var pixels = new byte[side * side * 4];
        Array.Fill(pixels, (byte)255);
        for (var my = 0; my < count; my++)
        {
            for (var mx = 0; mx < count; mx++)
            {
                if (!modules[my, mx]) continue;
                for (var py = 0; py < pixelsPerModule; py++)
                {
                    var row = ((my + quietModules) * pixelsPerModule + py) * side;
                    for (var px = 0; px < pixelsPerModule; px++)
                    {
                        var i = (row + (mx + quietModules) * pixelsPerModule + px) * 4;
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                    }
                }
            }
        }
        return (pixels, side);
    }
}
