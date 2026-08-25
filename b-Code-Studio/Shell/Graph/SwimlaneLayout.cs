namespace HistoryAurora.Shell.Graph;

/// <summary>
/// 泳道布局（REQ-UI-010）。X 由父边决定（左祖右孙），Y 优先贴主线并按水平占用复用空行。
/// **纯计算：不引用 WPF，不发指令**，因此可以脱离界面直接断言。
///
/// 算法承自 HistoryJanus 的 <c>GraphLayout</c>，但输入换成了与 Git 无关的泳道描述：
/// 「首父」「合并父」这些概念在描述协议里是 <see cref="SwimlaneNode.Parents"/> 的下标，
/// 布局不需要知道它们来自提交图谱还是别的什么。
/// </summary>
public static class SwimlaneLayout
{
    public const double NodeWidth = 148;

    public const double NodeHeight = 40;

    public const double LaneHeight = 88;

    public const double PaddingX = 20;

    public const double PaddingY = 16;

    public const double MinGap = 18;

    /// <summary>左侧泳道标题栏宽度。由组件决定，描述里传不进来。</summary>
    public const double LaneTitleWidth = 104;

    /// <summary>一条物理泳道行。</summary>
    public sealed record LaneRow(int Index, string Title, bool Open);

    /// <summary>落好位置的节点。<paramref name="IsTip"/> 表示该节点是未合并泳道的末端。</summary>
    public sealed record PlacedNode(SwimlaneNode Node, int Lane, bool IsTip, double X, double Y);

    public sealed record PlacedEdge(SwimlaneEdge Edge, double X1, double Y1, double X2, double Y2);

    public sealed record Result(
        IReadOnlyList<PlacedNode> Nodes,
        IReadOnlyList<PlacedEdge> Edges,
        IReadOnlyList<LaneRow> Lanes,
        double Width,
        double Height);

    public static double LaneTop(int index) => PaddingY + (index * LaneHeight);

    public static double LaneCenterY(int index) => LaneTop(index) + (LaneHeight / 2);

    /// <summary>矩形是否与视口相交。视口裁剪用；节点上千时不做裁剪会把画布整棵建出来。</summary>
    public static bool Intersects(
        double x, double y, double width, double height,
        double viewX, double viewY, double viewWidth, double viewHeight)
        => x < viewX + viewWidth && x + width > viewX
           && y < viewY + viewHeight && y + height > viewY;

    public static Result Arrange(SwimlaneDescription description)
    {
        ArgumentNullException.ThrowIfNull(description);

        var nodes = description.Nodes;
        var byId = new Dictionary<string, SwimlaneNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!string.IsNullOrWhiteSpace(node.Id))
                byId[node.Id] = node;
        }

        var laneRefs = description.Lanes.Count > 0
            ? description.Lanes
            : [new SwimlaneRef { Id = "main", Title = "主线", Open = true }];

        if (byId.Count == 0)
        {
            var emptyRows = laneRefs
                .Select((lane, index) => new LaneRow(index, LaneTitle(lane, index), lane.Open))
                .ToList();
            return new Result(
                [], [], emptyRows,
                PaddingX * 2 + 240,
                PaddingY * 2 + (LaneHeight * Math.Max(1, emptyRows.Count)));
        }

        var (laneOf, laneMeta) = AssignLanes(nodes, byId, laneRefs);
        var columns = AssignColumns(nodes, byId, laneOf);
        var (physicalLane, physicalMeta) = PackTowardMainline(laneOf, laneMeta, columns);

        var placed = new Dictionary<string, PlacedNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            var lane = physicalLane.GetValueOrDefault(node.Id);
            placed[node.Id] = new PlacedNode(
                node,
                lane,
                IsTip: false,
                columns.GetValueOrDefault(node.Id, PaddingX),
                LaneTop(lane) + ((LaneHeight - NodeHeight) / 2));
        }

        // 未合并泳道的末端画端点。主线不画：主线永远是开的，画了等于每张图都多一个无信息的点。
        foreach (var meta in laneMeta.Where(m => m.Index > 0 && m.Open))
        {
            if (string.IsNullOrWhiteSpace(meta.Tip) || !placed.TryGetValue(meta.Tip, out var tip))
                continue;
            placed[meta.Tip] = tip with { IsTip = true };
        }

        var edges = new List<PlacedEdge>();
        foreach (var edge in ResolveEdges(description, byId))
        {
            if (!placed.TryGetValue(edge.From, out var from) || !placed.TryGetValue(edge.To, out var to))
                continue;
            edges.Add(new PlacedEdge(
                edge,
                from.X + NodeWidth, from.Y + (NodeHeight / 2),
                to.X, to.Y + (NodeHeight / 2)));
        }

        var width = PaddingX;
        foreach (var item in placed.Values)
            width = Math.Max(width, item.X + NodeWidth + PaddingX);

        var rows = physicalMeta
            .OrderBy(pair => pair.Key)
            .Select(pair => new LaneRow(pair.Key, pair.Value.Title, pair.Value.Open))
            .ToList();
        var rowCount = physicalMeta.Count == 0 ? 1 : physicalMeta.Keys.Max() + 1;

        return new Result(
            placed.Values.ToList(),
            edges,
            rows,
            width,
            (PaddingY * 2) + (LaneHeight * Math.Max(1, rowCount)));
    }

    /// <summary>显式边优先；模块没给就按父子关系推导，非首父画虚线。</summary>
    private static IEnumerable<SwimlaneEdge> ResolveEdges(
        SwimlaneDescription description,
        IReadOnlyDictionary<string, SwimlaneNode> byId)
    {
        if (description.Edges is { Count: > 0 })
            return description.Edges;

        var derived = new List<SwimlaneEdge>();
        foreach (var node in description.Nodes)
        {
            var parents = node.Parents ?? [];
            for (var i = 0; i < parents.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(parents[i]) || !byId.ContainsKey(parents[i]))
                    continue;
                derived.Add(new SwimlaneEdge { From = parents[i], To = node.Id, Dashed = i > 0 });
            }
        }

        return derived;
    }

    /// <summary>
    /// 逻辑泳道认领。顺序刻意如此：
    /// ① 节点自报的泳道最硬；② 主线从末端沿首父回溯；③ 其余泳道同样回溯，撞到已认领的就停；
    /// ④ 合并进来的第二父自成一条历史泳道；⑤ 剩下的归主线。
    /// </summary>
    private static (Dictionary<string, int> LaneOf, List<LaneMeta> Meta) AssignLanes(
        IReadOnlyList<SwimlaneNode> nodes,
        IReadOnlyDictionary<string, SwimlaneNode> byId,
        IReadOnlyList<SwimlaneRef> laneRefs)
    {
        var laneOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var meta = new List<LaneMeta>
        {
            new(0, LaneTitle(laneRefs[0], 0), laneRefs[0].Open, laneRefs[0].Tip),
        };

        var indexByLaneId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(laneRefs[0].Id))
            indexByLaneId[laneRefs[0].Id] = 0;

        // ① 节点自报：先把显式声明的占住，回溯就不会再把它们抢走。
        for (var i = 1; i < laneRefs.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(laneRefs[i].Id))
                indexByLaneId.TryAdd(laneRefs[i].Id, i);
        }

        var explicitLanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.Lane is { Length: > 0 } laneId && indexByLaneId.TryGetValue(laneId, out var declared))
                explicitLanes[node.Id] = declared;
        }

        // ② 主线：从声明的末端回溯；没声明就取次序最大的节点。
        var mainTip = ResolveTip(nodes, laneRefs[0]) ?? nodes.OrderBy(OrderKey, StringComparer.Ordinal).Last();
        foreach (var id in WalkFirstParent(mainTip.Id, byId))
        {
            if (explicitLanes.TryGetValue(id, out var declared) && declared != 0)
                break;
            laneOf.TryAdd(id, 0);
        }

        // ③ 其余声明泳道。
        for (var i = 1; i < laneRefs.Count; i++)
        {
            var lane = laneRefs[i];
            var laneIndex = meta.Count;
            var claimed = false;

            foreach (var id in explicitLanes.Where(pair => pair.Value == i).Select(pair => pair.Key))
            {
                if (laneOf.TryAdd(id, laneIndex))
                    claimed = true;
            }

            var tip = ResolveTip(nodes, lane);
            if (tip != null)
            {
                foreach (var id in WalkFirstParent(tip.Id, byId))
                {
                    if (laneOf.ContainsKey(id))
                        break;
                    laneOf[id] = laneIndex;
                    claimed = true;
                }
            }

            if (!claimed)
                continue;
            meta.Add(new LaneMeta(laneIndex, LaneTitle(lane, i), lane.Open, lane.Tip));
        }

        // ④ 合并进来的第二父：它们代表一段已经并回主线的历史，保留成独立行才看得出分叉。
        foreach (var node in nodes)
        {
            var parents = node.Parents ?? [];
            for (var i = 1; i < parents.Count; i++)
            {
                var start = parents[i];
                if (string.IsNullOrWhiteSpace(start) || !byId.ContainsKey(start))
                    continue;
                if (laneOf.TryGetValue(start, out var existing) && existing != 0)
                    continue;

                var laneIndex = meta.Count;
                var claimed = false;
                foreach (var id in WalkFirstParent(start, byId))
                {
                    if (laneOf.ContainsKey(id))
                        break;
                    laneOf[id] = laneIndex;
                    claimed = true;
                }

                if (!claimed)
                    continue;
                meta.Add(new LaneMeta(laneIndex, Shorten(start), Open: false, start));
            }
        }

        // ⑤ 兜底。
        foreach (var node in nodes)
            laneOf.TryAdd(node.Id, 0);

        return (laneOf, meta);
    }

    /// <summary>
    /// 每个节点紧挨其最右的父，同时不与同泳道的前一个重叠。
    /// **次序键从不决定 X**：按时间平移整条泳道会让更长的历史行冲过分叉点。
    /// </summary>
    private static Dictionary<string, double> AssignColumns(
        IReadOnlyList<SwimlaneNode> nodes,
        IReadOnlyDictionary<string, SwimlaneNode> byId,
        IReadOnlyDictionary<string, int> laneOf)
    {
        const double step = NodeWidth + MinGap;
        var children = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var remaining = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            children[node.Id] = [];
            remaining[node.Id] = 0;
        }

        foreach (var node in nodes)
        {
            foreach (var parent in node.Parents ?? [])
            {
                if (string.IsNullOrWhiteSpace(parent) || !byId.ContainsKey(parent))
                    continue;
                remaining[node.Id]++;
                children[parent].Add(node.Id);
            }
        }

        var ready = nodes.Where(node => remaining[node.Id] == 0).ToList();
        ready.Sort(CompareOrder);

        var x = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var lastOnLane = new Dictionary<int, double>();
        while (ready.Count > 0)
        {
            var node = ready[0];
            ready.RemoveAt(0);

            var lane = laneOf.GetValueOrDefault(node.Id);
            var nodeX = PaddingX;
            if (lastOnLane.TryGetValue(lane, out var previous))
                nodeX = Math.Max(nodeX, previous + step);
            foreach (var parent in node.Parents ?? [])
            {
                if (x.TryGetValue(parent, out var parentX))
                    nodeX = Math.Max(nodeX, parentX + step);
            }

            x[node.Id] = nodeX;
            lastOnLane[lane] = nodeX;

            var added = false;
            foreach (var childId in children[node.Id])
            {
                remaining[childId]--;
                if (remaining[childId] != 0)
                    continue;
                ready.Add(byId[childId]);
                added = true;
            }

            if (added)
                ready.Sort(CompareOrder);
        }

        // 有环时会有节点始终排不进 ready。给它们一个位置而不是丢掉：
        // 画错一个位置能看出来，整条历史凭空消失看不出来。
        foreach (var node in nodes)
            x.TryAdd(node.Id, PaddingX);
        return x;
    }

    /// <summary>
    /// 尽量复用靠近主线的行：只有当后来的泳道与该行已有泳道的 X 区间重叠时才继续下移。
    /// </summary>
    private static (Dictionary<string, int> LaneOf, Dictionary<int, LaneMeta> Meta) PackTowardMainline(
        Dictionary<string, int> logicalLane,
        List<LaneMeta> logicalMeta,
        IReadOnlyDictionary<string, double> columns)
    {
        var byLogical = new Dictionary<int, List<string>>();
        foreach (var pair in logicalLane)
        {
            if (!byLogical.TryGetValue(pair.Value, out var ids))
            {
                ids = [];
                byLogical[pair.Value] = ids;
            }

            ids.Add(pair.Key);
        }

        var physical = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in byLogical.GetValueOrDefault(0) ?? [])
            physical[id] = 0;

        var packed = new Dictionary<int, LaneMeta> { [0] = logicalMeta[0] with { Index = 0 } };
        var taken = new Dictionary<int, List<(double Start, double End)>>();

        var branches = new List<(LaneMeta Meta, List<string> Ids, double MinX, double MaxX)>();
        foreach (var meta in logicalMeta)
        {
            if (meta.Index == 0)
                continue;
            var ids = byLogical.GetValueOrDefault(meta.Index) ?? [];
            var minX = PaddingX;
            var maxX = PaddingX;
            var first = true;
            foreach (var id in ids)
            {
                var x = columns.GetValueOrDefault(id, PaddingX);
                if (first)
                {
                    minX = x;
                    maxX = x + NodeWidth;
                    first = false;
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x + NodeWidth);
            }

            branches.Add((meta, ids, minX, maxX));
        }

        branches.Sort((left, right) =>
        {
            var byStart = left.MinX.CompareTo(right.MinX);
            return byStart != 0 ? byStart : left.Meta.Index.CompareTo(right.Meta.Index);
        });

        foreach (var branch in branches)
        {
            var row = 1;
            while (taken.TryGetValue(row, out var spans)
                   && spans.Any(span => span.Start < branch.MaxX && branch.MinX < span.End))
                row++;

            if (!taken.TryGetValue(row, out var bucket))
            {
                bucket = [];
                taken[row] = bucket;
            }

            bucket.Add((branch.MinX, branch.MaxX));
            foreach (var id in branch.Ids)
                physical[id] = row;
            packed.TryAdd(row, branch.Meta with { Index = row });
        }

        foreach (var pair in logicalLane)
            physical.TryAdd(pair.Key, 0);
        return (physical, packed);
    }

    private static SwimlaneNode? ResolveTip(IReadOnlyList<SwimlaneNode> nodes, SwimlaneRef lane)
    {
        if (string.IsNullOrWhiteSpace(lane.Tip))
            return null;
        foreach (var node in nodes)
        {
            if (node.Id.Equals(lane.Tip, StringComparison.OrdinalIgnoreCase))
                return node;
        }

        return null;
    }

    private static IEnumerable<string> WalkFirstParent(
        string tipId,
        IReadOnlyDictionary<string, SwimlaneNode> byId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = tipId;
        while (!string.IsNullOrWhiteSpace(current) && seen.Add(current))
        {
            yield return current;
            if (!byId.TryGetValue(current, out var node))
                yield break;
            var parents = node.Parents ?? [];
            if (parents.Count == 0)
                yield break;
            current = parents[0];
        }
    }

    private static int CompareOrder(SwimlaneNode left, SwimlaneNode right)
    {
        var byOrder = string.CompareOrdinal(OrderKey(left), OrderKey(right));
        return byOrder != 0
            ? byOrder
            : StringComparer.OrdinalIgnoreCase.Compare(left.Id, right.Id);
    }

    private static string OrderKey(SwimlaneNode node)
        => string.IsNullOrWhiteSpace(node.Order) ? node.Id : node.Order;

    private static string LaneTitle(SwimlaneRef lane, int index)
    {
        if (!string.IsNullOrWhiteSpace(lane.Title))
            return lane.Title;
        if (!string.IsNullOrWhiteSpace(lane.Id))
            return lane.Id;
        return index == 0 ? "主线" : "泳道 " + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Shorten(string value)
        => value.Length <= 10 ? value : value[..10];

    private sealed record LaneMeta(int Index, string Title, bool Open, string? Tip);
}
