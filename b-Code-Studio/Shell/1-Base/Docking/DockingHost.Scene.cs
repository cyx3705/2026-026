using HistoryVulcan.Core.Logging;

namespace HistoryAurora.Shell.Base.Docking;

/// <summary>
/// 场景切换要用到的那一小块停靠面（REQ-UI-085）。
///
/// 不并进 <see cref="IDockingService"/>：那个接口在测试里有四个替身，
/// 场景只需要这里的六项，没有理由让四个替身陪着改。
/// </summary>
internal interface ISceneDocking
{
    IReadOnlyList<ToolWindowInfo> ListWindows();

    void Show(string id);

    void Hide(string id);

    void SaveLayout(string name);

    void ApplyScene(string name, IReadOnlyCollection<string> members, bool rebuild);

    event EventHandler? WindowsChanged;
}

internal sealed partial class DockingHost : ISceneDocking
{
    /// <summary>
    /// 切到一个场景：换掉停靠布局，只留 <paramref name="members"/> 里的页面。
    ///
    /// 布局的来路按先后：
    /// <list type="number">
    ///   <item><paramref name="rebuild"/> 为 true：按各页 placement 重建默认布局（场景重置）；</item>
    ///   <item>存有同名命名布局：恢复它——命名布局按场景 id 取名，模块场景的 id 就是模块名；</item>
    ///   <item>都没有：保留当前布局树，只做显隐。</item>
    /// </list>
    /// 然后不在场景里的页一律隐藏。
    ///
    /// **页面视图不重建**：内容对象按 id 缓存在 <c>_contents</c>，恢复快照换的只是停靠模型
    /// （REQ-UI-086）。切走再切回来，页面里选好的来源、表格的选中行都还在。
    ///
    /// 命令集是主文档区的锚点，藏掉它会被 <see cref="EnsureCentralWorkspace"/> 当场修回去，
    /// 所以调用方应把它当常驻页传进来；这里遇到它也只跳过，不硬藏。
    /// </summary>
    public void ApplyScene(string name, IReadOnlyCollection<string> members, bool rebuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(members);
        RestoreLayoutFromMaximized();

        var keep = new HashSet<string>(members, StringComparer.OrdinalIgnoreCase);
        string? payload = null;
        if (!rebuild)
        {
            try
            {
                payload = _store.ReadNamed(name);
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取场景布局 {name} 失败，保留当前布局只做显隐: {ex.Message}");
            }
        }

        using (Suppress())
        {
            var restored = false;
            if (payload != null)
            {
                try
                {
                    ApplyLayoutSnapshot(payload);
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
                    EnsureRegisteredWindows();
                    restored = true;
                    _seedRatiosFromLayout = true;
                }
                catch (Exception ex)
                {
                    _log.Warn(LayoutSource, $"场景布局 {name} 无法恢复，按默认布局重建: {ex.Message}");
                    rebuild = true;
                }
            }

            if (rebuild)
            {
                BuildDefaultLayout();
                _seedRatiosFromLayout = false;
                foreach (var d in _descriptors)
                    _ratios[d.Id] = NormalizeRatio(d.DefaultRatio, 0.25);
            }

            foreach (var descriptor in _descriptors.ToArray())
            {
                var id = descriptor.Id;
                try
                {
                    if (!keep.Contains(id))
                    {
                        if (!IsPrimaryCommandDocument(id) && ComputeState(id).Visible)
                            Hide(id);
                    }
                    else if (!restored && !rebuild && descriptor.DefaultVisible && !ComputeState(id).Visible)
                    {
                        // 别的场景藏起来的页，回到自己的场景要重新露面；
                        // 默认就不显示的页（DefaultVisible = false）不替它做主。
                        Show(id);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warn(LayoutSource, $"场景 {name} 处理窗口 {id} 失败: {ex.Message}");
                }
            }

            // 快照里记着上次的活动页，不动它；没有快照时让场景自己的中央页顶在前面，
            // 否则中央区露出来的是常驻的命令集。
            if (!restored)
                SelectCenterPage(members.FirstOrDefault(IsVisibleCenterPage));

            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = name;
        }

        ScheduleReapplyRatios();
        RebaseSoon();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsVisibleCenterPage(string id)
        => _byId.ContainsKey(id)
           && !IsPrimaryCommandDocument(id)
           && ComputeState(id) is { Visible: true, Floating: false, Side: DockSide.Center };

    private void SelectCenterPage(string? id)
    {
        if (id == null)
            return;
        var anchorable = FindAnchorable(id);
        if (anchorable == null || anchorable.IsHidden)
            return;
        anchorable.IsSelected = true;
        anchorable.IsActive = true;
    }
}
