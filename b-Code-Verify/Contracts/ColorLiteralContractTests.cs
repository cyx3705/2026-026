using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 颜色只在两份令牌文件里定义（1.26.0，REQ-UI-128）。
///
/// 风格是一整套令牌（<c>AuroraTokens.xaml</c> / <c>AuroraTokens.Dark.xaml</c>），别处一律按键引用。
/// 散落在代码里的颜色有两种坏法：只写了一套、深色下露馅；或者与令牌各说各话、改一次品牌色漏一处。
/// 1.26.0 之前仓里有四处：控制台行的级别色板、停靠覆盖层的蓝色回退、面板线兜底色、量字宽用的黑画刷。
///
/// 放行的只有 <c>Brushes.Transparent</c>：它是命中测试与"本来就该透明"的结构值，不是配色。
/// XAML 注释里提到色值（记录历史取值）不算。
/// </summary>
public sealed class ColorLiteralContractTests
{
    private static readonly string[] TokenFiles = ["AuroraTokens.xaml", "AuroraTokens.Dark.xaml"];

    private static readonly Regex CodeColor = new(
        @"Color\.From(A?Rgb|ScRgb)\s*\(|\bColors\.[A-Z]|\bBrushes\.(?!Transparent\b)[A-Z]|new\s+SolidColorBrush\s*\(\s*(?!\))|ColorConverter\.ConvertFromString|""#[0-9A-Fa-f]{3,8}""",
        RegexOptions.Compiled);

    private static readonly Regex XamlColor = new(
        @"=""\s*#[0-9A-Fa-f]{3,8}\s*""|=""(White|Black|Red|Green|Blue|Gray|Grey|Yellow|Orange|LightGray|DarkGray|Silver)""",
        RegexOptions.Compiled);

    private static readonly Regex XamlComment = new("<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void ColorsAreDefinedOnlyInTheTokenDictionaries()
    {
        var root = Path.Combine(RepositoryRoot(), "b-Code-Studio");
        var hits = new List<string>();

        foreach (var file in Sources(root, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var code = StripLineComment(lines[index]);
                if (CodeColor.IsMatch(code))
                    hits.Add($"{Path.GetRelativePath(root, file)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        foreach (var file in Sources(root, "*.xaml"))
        {
            if (TokenFiles.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase))
                continue;

            var text = XamlComment.Replace(File.ReadAllText(file), match => new string('\n', match.Value.Count(c => c == '\n')));
            var lines = text.Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (XamlColor.IsMatch(lines[index]))
                    hits.Add($"{Path.GetRelativePath(root, file)}:{index + 1}: {lines[index].Trim()}");
            }
        }

        Assert.True(hits.Count == 0, "令牌文件之外出现写死的颜色，改成引用 Aurora.Brush.*：\n" + string.Join("\n", hits));
    }

    private static IEnumerable<string> Sources(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"));

    /// <summary>去掉 <c>//</c> 行注释；字符串里的 <c>//</c>（如 URI）按引号配对跳过。</summary>
    private static string StripLineComment(string line)
    {
        var inString = false;
        for (var index = 0; index < line.Length - 1; index++)
        {
            if (line[index] == '"' && (index == 0 || line[index - 1] != '\\'))
                inString = !inString;
            else if (!inString && line[index] == '/' && line[index + 1] == '/')
                return line[..index];
        }

        return line;
    }

    private static string RepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "project.manifest.json")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("未找到 HistoryAurora 仓库根目录");
    }
}
