using System.Text.Json;
using StbImageSharp;
using StbImageWriteSharp;

namespace Reaper.Core;

internal sealed class SpritePngExporter
{
    private readonly Dictionary<string, ImageResult> textures = [];
    private long cacheBytes;

    // AssetRipper 2.0.0's Sprite image endpoint returns its decoded texture, not a cropped sprite.
    // SpriteProcessor resolves SpriteAtlas references into m_RD before this JSON is read.
    public async Task WriteAsync(HttpClient http, string query, Stream output, CancellationToken token)
    {
        string json = await http.GetStringAsync("/Assets/Json" + query, token);
        using var document = JsonDocument.Parse(json);
        var rd = document.RootElement.GetProperty("m_RD");
        var rect = rd.GetProperty("m_TextureRect");
        int x = Coordinate(rect, "m_X"), y = Coordinate(rect, "m_Y");
        int width = Coordinate(rect, "m_Width"), height = Coordinate(rect, "m_Height");
        using var assetPath = JsonDocument.Parse(Uri.UnescapeDataString(query[6..])); // ?Path=
        string textureKey = assetPath.RootElement.GetProperty("C").GetRawText() + rd.GetProperty("m_Texture").GetRawText();
        if (!textures.TryGetValue(textureKey, out var image))
        {
            using var response = await http.GetAsync("/Assets/Image" + query + "&Extension=png", HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, token);
            buffer.Position = 0;
            image = ImageResult.FromStream(buffer, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            // Bounded per-job cache avoids decoding/downloading the same atlas for every sprite.
            const long limit = 64L * 1024 * 1024;
            if (image.Data.LongLength <= limit)
            {
                if (cacheBytes + image.Data.LongLength > limit) { textures.Clear(); cacheBytes = 0; }
                textures.Add(textureKey, image);
                cacheBytes += image.Data.LongLength;
            }
        }
        if (x < 0 || y < 0 || width <= 0 || height <= 0 || (long)x + width > image.Width || (long)y + height > image.Height)
            throw new IOException("Sprite rectangle is outside the decoded texture.");
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int row = 0; row < height; row++)
        {
            token.ThrowIfCancellationRequested();
            // Unity rects have a bottom-left origin; decoded PNGs have a top-left origin.
            int source = ((image.Height - y - height + row) * image.Width + x) * 4;
            image.Data.AsSpan(source, width * 4).CopyTo(pixels.AsSpan(row * width * 4));
        }
        uint settings = rd.TryGetProperty("m_SettingsRaw", out var flags) ? flags.GetUInt32() : 0;
        int rotation = (int)(settings >> 2 & 15);
        if ((settings & 1) != 0 && rotation != 0)
        {
            int resultWidth = rotation == 4 ? height : width, resultHeight = rotation == 4 ? width : height;
            byte[] unpacked = new byte[pixels.Length];
            for (int row = 0; row < resultHeight; row++) for (int col = 0; col < resultWidth; col++)
            {
                (int sx, int sy) = rotation switch
                {
                    1 => (width - 1 - col, row),
                    2 => (col, height - 1 - row),
                    3 => (width - 1 - col, height - 1 - row),
                    4 => (width - 1 - row, col),
                    _ => throw new IOException($"Unsupported sprite packing rotation: {rotation}.")
                };
                pixels.AsSpan((sy * width + sx) * 4, 4).CopyTo(unpacked.AsSpan((row * resultWidth + col) * 4));
            }
            (pixels, width, height) = (unpacked, resultWidth, resultHeight);
        }
        token.ThrowIfCancellationRequested();
        new ImageWriter().WritePng(pixels, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, output);
    }

    private static int Coordinate(JsonElement rectangle, string name)
    {
        double value = rectangle.GetProperty(name).GetDouble();
        if (!double.IsFinite(value) || Math.Abs(value - Math.Round(value)) > .01 || value < 0 || value > int.MaxValue)
            throw new IOException("Sprite has invalid or fractional texture coordinates.");
        return checked((int)Math.Round(value));
    }
}
