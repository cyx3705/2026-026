namespace HistoryAurora.Shell.Components.Table;

/// <summary>
/// 一列的声明。**这是宿主唯一能决定的表格外观**：标题、取值键与列宽。
/// 颜色、圆角、行高、字号、分隔线、选中态全部由 <see cref="AuroraTable"/> 自己决定
/// （REQ-UI-007）——宿主传不进来，也不该传。
/// </summary>
/// <param name="Key">取值键，对应行字典里的键名。</param>
/// <param name="Title">表头文字。</param>
/// <param name="Width">
/// **权重**，不是像素（REQ-UI-039，1.8.15 起）。表格永远铺满可用宽度，各列按权重分摊：
/// <c>"150"</c> 与 <c>"70"</c> 的比例仍是 150:70，但两列一起随页面宽度缩放。
/// <c>"*"</c> 相当于权重 200；<c>null</c> 相当于 120。
/// 解析不出数字时按未声明处理，不抛——一列宽度写错不该让整张表消失。
/// </param>
public sealed record AuroraTableColumn(
    string Key,
    string Title,
    string? Width = null,
    AuroraCellAction? CellAction = null)
{
    /// <summary>占满剩余宽度的记号。</summary>
    public const string Star = "*";

    public bool IsStar => string.Equals(Width?.Trim(), Star, StringComparison.Ordinal);

    /// <summary>固定像素宽；未声明、非法或为 <c>*</c> 时返回 null（按内容自适应或占满剩余）。</summary>
    public double? FixedWidth =>
        !IsStar
        && double.TryParse(Width, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var px)
        && px > 0
            ? px
            : null;
}

/// <summary>一列的单元格动作。外观与触发方式由 <see cref="AuroraTable"/> 统一提供。</summary>
public sealed record AuroraCellAction(string Id, string? Summary = null, bool Danger = false);

/// <summary>单元格动作触发事实：动作、列键与被操作行始终来自同一次点击。</summary>
public sealed class AuroraCellActionEventArgs(
    AuroraCellAction action,
    string columnKey,
    IReadOnlyDictionary<string, string> row) : EventArgs
{
    public AuroraCellAction Action { get; } = action;

    public string ColumnKey { get; } = columnKey;

    public IReadOnlyDictionary<string, string> Row { get; } = row;

    /// <summary>异步消费者设置命令完成任务，组件据此显示运行态并防止重复触发。</summary>
    public Task<bool>? Completion { get; set; }
}

/// <summary>
/// 表格的全部输入：列声明 + 行数组。行是字符串字典，**行数与列数都由数据自己决定**，
/// 调用方不另外传计数——两处计数一旦能不一致，就一定会不一致。
/// </summary>
public sealed class AuroraTableData
{
    /// <summary>没有列也没有行；用于清空而不是把 null 传进组件。</summary>
    public static readonly AuroraTableData Empty = new([], []);

    private AuroraTableData(
        IReadOnlyList<AuroraTableColumn> columns,
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public IReadOnlyList<AuroraTableColumn> Columns { get; }

    public IReadOnlyList<IReadOnlyDictionary<string, string>> Rows { get; }

    public int RowCount => Rows.Count;

    public int ColumnCount => Columns.Count;

    /// <summary>列声明齐全时直接组装。列为空则退回 <see cref="FromRows"/> 的推断。</summary>
    public static AuroraTableData Create(
        IReadOnlyList<AuroraTableColumn>? columns,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? rows)
    {
        var safeRows = Normalize(rows);
        return columns is { Count: > 0 }
            ? new AuroraTableData(columns.ToList(), safeRows)
            : FromRows(safeRows);
    }

    /// <summary>
    /// 只给行、不给列：列**从数据推断**——按各行键的首次出现顺序取并集，标题即键名。
    /// 「根据数据提供行列数」正是这条：模块给什么就画什么，不必再维护一份列清单。
    /// </summary>
    public static AuroraTableData FromRows(IReadOnlyList<IReadOnlyDictionary<string, string>>? rows)
    {
        var safeRows = Normalize(rows);
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in safeRows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                    keys.Add(key);
            }
        }

        return new AuroraTableData(
            keys.Select(key => new AuroraTableColumn(key, key)).ToList(),
            safeRows);
    }

    /// <summary>
    /// 从强类型集合投影。列用委托取值而不是反射：反射的列顺序依赖成员声明顺序，
    /// 改一次字段顺序就悄悄换了列序，编译期毫无提示。
    /// </summary>
    public static AuroraTableData FromItems<T>(
        IEnumerable<T> items,
        params (string Title, string? Width, Func<T, string> Cell)[] columns)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(columns);

        // 键由列序号生成：调用方不需要为「字典键」再想一遍名字，标题重名也不会互相覆盖。
        var declared = columns
            .Select((column, index) => new AuroraTableColumn(
                "c" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                column.Title,
                column.Width))
            .ToList();

        var rows = new List<IReadOnlyDictionary<string, string>>();
        foreach (var item in items)
        {
            var row = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < columns.Length; i++)
                row[declared[i].Key] = columns[i].Cell(item) ?? "";
            rows.Add(row);
        }

        return new AuroraTableData(declared, rows);
    }

    private static IReadOnlyList<IReadOnlyDictionary<string, string>> Normalize(
        IReadOnlyList<IReadOnlyDictionary<string, string>>? rows)
        => rows == null
            ? []
            : rows.Where(row => row != null).ToList();
}
