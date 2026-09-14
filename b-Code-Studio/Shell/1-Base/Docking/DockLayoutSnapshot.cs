using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HistoryAurora.Shell.Base.Docking;

internal enum DockLayoutNodeKind
{
    Panel,
    DocumentPane,
    AnchorablePane,
    DocumentPaneGroup,
    AnchorablePaneGroup,
}

internal enum DockLengthUnit
{
    Auto,
    Pixel,
    Star,
}

internal sealed class DockLayoutSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DockLayoutNodeSnapshot? Root { get; init; }

    public List<DockAutoHideGroupSnapshot> AutoHideGroups { get; init; } = [];

    public Dictionary<string, DockPlacementSnapshot> Placements { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public string? ActiveContentId { get; init; }
}

internal sealed class DockLayoutNodeSnapshot
{
    public DockLayoutNodeKind Kind { get; init; }

    public string? Orientation { get; init; }

    public DockLengthSnapshot DockWidth { get; init; } = DockLengthSnapshot.StarOne;

    public DockLengthSnapshot DockHeight { get; init; } = DockLengthSnapshot.StarOne;

    public double DockMinWidth { get; init; }

    public double DockMinHeight { get; init; }

    public bool ShowHeader { get; init; } = true;

    public string? SelectedContentId { get; init; }

    public List<DockLayoutNodeSnapshot> Children { get; init; } = [];

    public List<DockContentSnapshot> Contents { get; init; } = [];
}

/// <summary>
/// 一格里的一页。1.22（REQ-UI-120）起没有独立浮窗：旧文件里的 <c>floatingWindows</c>、
/// <c>floatingLeft/Top/Width/Height</c> 与 <c>isMaximized</c> 反序列化时直接忽略，原先浮着的页按「快照里缺的页」处理。
/// </summary>
internal sealed class DockContentSnapshot
{
    public required string Id { get; init; }
}

internal sealed class DockAutoHideGroupSnapshot
{
    public required DockSide Side { get; init; }

    public List<DockContentSnapshot> Contents { get; init; } = [];
}

internal sealed record DockPlacementSnapshot(
    DockSide Side,
    double Ratio,
    bool Hidden,
    string? TabTarget,
    int? CenterIndex = null,
    bool? Selected = null);

internal sealed record DockLengthSnapshot(double Value, DockLengthUnit Unit)
{
    public static DockLengthSnapshot StarOne { get; } = new(1, DockLengthUnit.Star);
}

internal static class DockLayoutSnapshotCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static string Serialize(DockLayoutSnapshot snapshot)
        => JsonSerializer.Serialize(snapshot, Options);

    internal static DockLayoutSnapshot Deserialize(string payload)
    {
        var snapshot = JsonSerializer.Deserialize<DockLayoutSnapshot>(payload, Options)
                       ?? throw new InvalidDataException("布局快照为空");
        if (snapshot.SchemaVersion != DockLayoutSnapshot.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"不支持的布局快照版本: {snapshot.SchemaVersion}");
        }

        if (snapshot.Root == null)
            throw new InvalidDataException("布局快照缺少主布局树");
        return snapshot;
    }
}
