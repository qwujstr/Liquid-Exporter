using System.Drawing.Imaging;

namespace Reaper.App;

internal static class GameIcon
{
    public static string? Read(string selectedPath)
    {
        try
        {
            string? executable = FindExecutable(selectedPath);
            if (executable is null) return null;
            // Extract the resource; the selected executable is never launched.
            using var icon = Icon.ExtractAssociatedIcon(executable);
            if (icon is null) return null;
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(png.ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { return null; }
    }

    private static string? FindExecutable(string path)
    {
        if (File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return path;
        string? root = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (root is null || !Directory.Exists(root)) return null;
        // Unity's data directory is a much stronger identifier than the first EXE.
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        int visited = 0;
        while (pending.Count > 0 && visited++ < 120)
        {
            var current = pending.Dequeue();
            foreach (string data in Directory.EnumerateDirectories(current.Path, "*_Data"))
            {
                string candidate = Path.Combine(current.Path, Path.GetFileName(data)[..^5] + ".exe");
                if (File.Exists(candidate)) return candidate;
            }
            if (current.Depth < 2)
                foreach (string child in Directory.EnumerateDirectories(current.Path))
                    if (!File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint)) pending.Enqueue((child, current.Depth + 1));
        }
        return null;
    }
}
