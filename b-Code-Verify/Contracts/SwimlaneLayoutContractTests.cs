using HistoryAurora.Shell.Graph;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 泳道布局的契约（REQ-UI-010）。这一轮把布局从 HistoryJanus 收进 Aurora，
/// 因此这里断言的是「描述里没有坐标」这条分工能否真的成立：
/// <list type="number">
///   <item>只给节点与父边，Aurora 就能排出泳道与列——模块不必再懂画布；</item>
///   <item>X 由父边决定，不由次序键决定——按时间平移整条泳道会冲过分叉点；</item>
///   <item>边可以不给，由父子关系推出来——同一件事说两遍就会有对不上的一天。</item>
/// </list>
/// 布局是纯计算，因此不需要 STA，也不需要建任何控件。
/// </summary>
public sealed class SwimlaneLayoutContractTests
{
    [Fact]
    public void Parse_RejectsUnsupportedSchemaVersion()
    {
        var parsed = SwimlaneReader.Read("""{ "schemaVersion": 7, "nodes": [] }""");

        Assert.False(parsed.Ok);
        Assert.Contains("schemaVersion=7", parsed.Error);
    }

    [Fact]
    public void Parse_RejectsDuplicateNodeIds()
    {
        var parsed = SwimlaneReader.Read("""
            {
              "schemaVersion": 1,
              "nodes": [ { "id": "a" }, { "id": "a" } ]
            }
            """);

        Assert.False(parsed.Ok);
        Assert.Contains("a", parsed.Error);
    }

    [Fact]
    public void Parse_AcceptsEmptyNodeSet()
    {
        // 「这个项目还没有提交」是合法状态，画一张空图即可，不是错误。
        var parsed = SwimlaneReader.Read("""{ "schemaVersion": 1, "nodes": [] }""");

        Assert.True(parsed.Ok, parsed.Error);
        var layout = SwimlaneLayout.Arrange(parsed.Value!);
        Assert.Empty(layout.Nodes);
        Assert.Single(layout.Lanes);
    }

    [Fact]
    public void Arrange_PlacesChildrenToTheRightOfParents()
    {
        var layout = SwimlaneLayout.Arrange(Linear());

        var x = layout.Nodes.ToDictionary(node => node.Node.Id, node => node.X);
        Assert.True(x["c1"] < x["c2"], "子提交必须落在父提交右侧");
        Assert.True(x["c2"] < x["c3"], "子提交必须落在父提交右侧");
        Assert.Equal(SwimlaneLayout.PaddingX, x["c1"]);
    }

    [Fact]
    public void Arrange_DerivesEdgesFromParentsWhenNoneDeclared()
    {
        var layout = SwimlaneLayout.Arrange(Linear());

        Assert.Equal(2, layout.Edges.Count);
        Assert.All(layout.Edges, edge => Assert.False(edge.Edge.Dashed));
        // 边从父的右缘连到子的左缘，节点间因此不会被线穿过。
        Assert.All(layout.Edges, edge => Assert.True(edge.X2 > edge.X1));
    }

    [Fact]
    public void Arrange_PutsBranchOnItsOwnLaneAndDashesMergeParent()
    {
        var parsed = SwimlaneReader.Read("""
            {
              "schemaVersion": 1,
              "lanes": [
                { "id": "main", "title": "主线", "tip": "m3" },
                { "id": "work", "title": "ai/work", "tip": "w1", "open": true }
              ],
              "nodes": [
                { "id": "m1", "title": "m1", "parents": [] },
                { "id": "m2", "title": "m2", "parents": [ "m1" ] },
                { "id": "w1", "title": "w1", "parents": [ "m1" ] },
                { "id": "m3", "title": "m3", "parents": [ "m2", "w1" ] }
              ]
            }
            """);
        Assert.True(parsed.Ok, parsed.Error);

        var layout = SwimlaneLayout.Arrange(parsed.Value!);
        var lane = layout.Nodes.ToDictionary(node => node.Node.Id, node => node.Lane);

        Assert.Equal(0, lane["m1"]);
        Assert.Equal(0, lane["m2"]);
        Assert.Equal(0, lane["m3"]);
        Assert.NotEqual(0, lane["w1"]);

        // 第二父的边画虚线：一眼分得出哪条是首父线。
        var merge = Assert.Single(layout.Edges, edge => edge.Edge.From == "w1");
        Assert.True(merge.Edge.Dashed);

        // 未合并泳道的末端画端点；主线不画。
        Assert.Contains(layout.Nodes, node => node.Node.Id == "w1" && node.IsTip);
        Assert.DoesNotContain(layout.Nodes, node => node.Node.Id == "m3" && node.IsTip);
    }

    [Fact]
    public void Arrange_HonoursExplicitLaneOnNode()
    {
        var parsed = SwimlaneReader.Read("""
            {
              "schemaVersion": 1,
              "lanes": [
                { "id": "main", "title": "主线", "tip": "b" },
                { "id": "side", "title": "旁路" }
              ],
              "nodes": [
                { "id": "a", "title": "a", "parents": [] },
                { "id": "b", "title": "b", "parents": [ "a" ], "lane": "side" }
              ]
            }
            """);
        Assert.True(parsed.Ok, parsed.Error);

        var layout = SwimlaneLayout.Arrange(parsed.Value!);
        var lane = layout.Nodes.ToDictionary(node => node.Node.Id, node => node.Lane);

        // 节点自报的泳道最硬：回溯不得把它抢回主线。
        Assert.NotEqual(0, lane["b"]);
    }

    [Fact]
    public void Arrange_SizesCanvasToContent()
    {
        var layout = SwimlaneLayout.Arrange(Linear());

        var rightmost = layout.Nodes.Max(node => node.X) + SwimlaneLayout.NodeWidth;
        Assert.True(layout.Width >= rightmost, "画布宽度必须容得下最右的节点");
        Assert.True(layout.Height >= SwimlaneLayout.LaneHeight, "画布高度至少一条泳道");
    }

    [Fact]
    public void Intersects_CullsWhatIsOutsideTheViewport()
    {
        Assert.True(SwimlaneLayout.Intersects(0, 0, 10, 10, 5, 5, 20, 20));
        Assert.False(SwimlaneLayout.Intersects(0, 0, 10, 10, 100, 100, 20, 20));
    }

    private static SwimlaneDescription Linear()
    {
        var parsed = SwimlaneReader.Read("""
            {
              "schemaVersion": 1,
              "title": "线性历史",
              "lanes": [ { "id": "main", "title": "主线", "tip": "c3" } ],
              "nodes": [
                { "id": "c1", "title": "c1", "parents": [] },
                { "id": "c2", "title": "c2", "parents": [ "c1" ] },
                { "id": "c3", "title": "c3", "parents": [ "c2" ] }
              ]
            }
            """);
        Assert.True(parsed.Ok, parsed.Error);
        return parsed.Value!;
    }
}
