using System.IO;
using HistoryVulcan.Services;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>当前布局为 layout/layout.v1.json，命名方案为 layout/&lt;名称&gt;.layout.v1.json。</summary>
internal sealed class FileLayoutStore : ILayoutStore
{
    private const string Extension = ".layout.v1.json";
    private const string CurrentName = "layout";

    private readonly string _dir;

    public FileLayoutStore(AppPaths paths) => _dir = paths.LayoutDir;

    public FileLayoutStore(string layoutDirectory)
    {
        _dir = layoutDirectory;
        Directory.CreateDirectory(_dir);
    }

    public string? ReadCurrent() => ReadNamed(CurrentName);

    public void WriteCurrent(string payload) => WriteNamed(CurrentName, payload);

    public void DeleteCurrent()
    {
        var path = PathOf(CurrentName);
        if (File.Exists(path))
            File.Delete(path);
    }

    public string? ReadNamed(string name)
    {
        var path = PathOf(name);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public void WriteNamed(string name, string payload)
    {
        var path = PathOf(name);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
                File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public IReadOnlyList<string> ListNamed()
        => Directory.EnumerateFiles(_dir, "*" + Extension)
            .Select(file => Path.GetFileName(file)[..^Extension.Length])
            .Where(name => !string.Equals(name, CurrentName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private string PathOf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"非法布局名: {name}", nameof(name));

        Directory.CreateDirectory(_dir);
        return Path.Combine(
            _dir,
            string.Equals(name, CurrentName, StringComparison.OrdinalIgnoreCase)
                ? "layout.v1.json"
                : name + Extension);
    }
}
