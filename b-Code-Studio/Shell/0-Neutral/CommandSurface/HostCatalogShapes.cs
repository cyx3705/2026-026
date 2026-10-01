namespace HistoryAurora.Shell.Neutral.CommandSurface;

// 宿主目录与模块列表的 JSON 形状（宿主模块API「载荷形状」一节，C7）在本仓的映像（1.29.0）。
//
// 宿主 6.0.0 起 vulcan.command.list / show、vulcan.module.list 的 Data 是 JsonElement，
// 契约是写明的字段名，不是宿主的 C# 类。这里按同名字段声明记录，CommandResultData.TryRead 按名反序列化；
// 宿主只增字段，这边忽略不认识的字段。此前直接用宿主实现程序集里的同名类型，宿主一改就连带改界面。

/// <summary><c>vulcan.command.list</c> 的一行。</summary>
public sealed record CommandCatalogRow(
    string CommandName,
    string Domain,
    string Summary,
    string? Example,
    int ParameterCount,
    string Source,
    string? SourceDetail,
    bool Dangerous,
    bool RequiresUiThread,
    bool Readonly,
    string? HiddenReason)
{
    public string CommandClass { get; init; } = "core";

    public string Method { get; init; } = "";

    public bool RequiresConfirmation { get; init; }

    public bool AllowUnspecifiedParameters { get; init; }

    public IReadOnlyList<CommandParameterInfo> Parameters { get; init; } = [];

    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>一条指令的单个参数。</summary>
public sealed record CommandParameterInfo(
    string Name,
    string Type,
    bool Required,
    string? Default,
    int? Position,
    IReadOnlyList<string> AllowedValues,
    string Description);

/// <summary><c>vulcan.command.show</c> 的结果。</summary>
public sealed record CommandCatalogDetail(
    CommandCatalogRow Command,
    IReadOnlyList<CommandParameterInfo> Parameters)
{
    public IReadOnlyDictionary<string, string> Annotations { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <summary><c>vulcan.module.list</c> 的一项。</summary>
public sealed record ModuleMeta(
    string ModuleName,
    string Description,
    string Author,
    string Version,
    bool Open,
    string AssemblyFile,
    int CommandCount,
    string Slot = "",
    bool Ui = false)
{
    public string InstanceId { get; init; } = "";

    public string? SourcePath { get; init; }

    public string? ManifestPath { get; init; }

    public IReadOnlyList<string> AttachFailures { get; init; } = [];

    public string DataDirectory { get; init; } = "";

    public bool Attached => AttachFailures.Count == 0;
}
