using System.IO;
using HistoryVulcan.Services;

namespace HistoryAurora.Shell.Docking;

/// <summary>当前布局为 layout/current.layout.xml，命名方案为 layout/&lt;名称&gt;.layout.xml。</summary>
public sealed class FileLayoutStore : ILayoutStore
{
    private const string Extension = ".layout.xml";
    private const string CurrentName = "current";

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
        => File.WriteAllText(PathOf(name), payload);

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
        return Path.Combine(_dir, name + Extension);
    }
}
