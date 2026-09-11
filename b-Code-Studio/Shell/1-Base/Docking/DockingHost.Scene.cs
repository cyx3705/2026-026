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

    void ApplyScene(string name, IReadOnlyCollection<string> seed, bool rebuild);

    event EventHandler? WindowsChanged;
}

internal sealed partial class DockingHost : ISceneDocking
{
    /// <summary>
    /// 切到一个场景：换掉停靠布局。
    ///
    /// 场景只是一份命名布局（REQ-UI-094），<paramref name="seed"/> 是它的**初值**——
    /// 场景没有布局可恢复时，哪些页露面。布局的来路按先后：
    /// <list type="number">
    ///   <item><paramref name="rebuild"/> 为 true：按各页 placement 重建默认布局，再按初值定显隐（场景重置）；</item>
    ///   <item>存有同名命名布局：恢复它，显隐就是它记着的样子。存下之后才登记的页这个场景没见过，按初值定；</item>
    ///   <item>都没有：保留当前布局树，按初值定显隐。</item>
    /// </list>
    /// 最后顶栏只留一页（REQ-UI-096）。
    ///
    /// **页面视图不重建**：内容对象按 id 缓存在 <c>_contents</c>，恢复快照换的只是停靠模型
    /// （REQ-UI-086）。切走再切回来，页面里选好的来源、表格的选中行都还在。
    /// </summary>
    public void ApplyScene(string name, IReadOnlyCollection<string> seed, bool rebuild)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(seed);
        RestoreLayoutFromMaximized();

        var initial = new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);
        string? payload = null;
        if (!rebuild)
        {
            try
            {
                payload = _store.ReadNamed(name);
            }
            catch (Exception ex)
            {
                _log.Warn(LayoutSource, $"读取场景布局 {name} 失败，保留当前布局只按初值定显隐: {ex.Message}");
            }
        }

        using (Suppress())
        {
            var restored = false;
            if (payload != null)
            {
                try
                {
                    var known = ApplyLayoutSnapshot(payload);
                    if (!LayoutHasMainDocumentPane())
                        throw new InvalidOperationException("布局中缺少中央主文档区");
                    EnsureRegisteredWindows();
                    foreach (var descriptor in _descriptors.ToArray())
                    {
                        if (!known.Contains(descriptor.Id))
                            SeedVisibility(name, descriptor, initial);
                    }

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

            string? keep = null;
            if (!restored)
            {
                foreach (var descriptor in _descriptors.ToArray())
                    SeedVisibility(name, descriptor, initial);

                // 快照里记着上次的活动页，不动它；没有快照时让场景自己的中央页顶在前面，
                // 否则顶栏露出来的是常驻的命令集。
                keep = seed.FirstOrDefault(IsVisibleCenterPage);
            }

            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = name;
            EnforceSingleCenterPage(keep ?? SelectedCenterId());
        }

        ScheduleReapplyRatios();
        RebaseSoon();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按初值定一页的显隐：不在初值里的藏起来；在初值里、默认就该露面而此刻藏着的，重新露面。
    /// 默认就不显示的页（DefaultVisible = false）不替它做主。
    /// </summary>
    private void SeedVisibility(string scene, ToolWindowDescriptor descriptor, HashSet<string> initial)
    {
        var id = descriptor.Id;
        try
        {
            var visible = ComputeState(id).Visible;
            if (!initial.Contains(id))
            {
                if (visible)
                    Hide(id);
            }
            else if (descriptor.DefaultVisible && !visible)
            {
                // 不走 Show：那条路每露一页就执行一次「顶栏只留一页」，
                // 顶栏里留下的会是初值里最后一个中央页，而不是场景自己挑的那一个。
                ShowCore(id);
            }
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"场景 {scene} 处理窗口 {id} 失败: {ex.Message}");
        }
    }

    private bool IsVisibleCenterPage(string id)
        => _byId.ContainsKey(id)
           && !IsPrimaryCommandDocument(id)
           && ComputeState(id) is { Visible: true, Floating: false, Side: DockSide.Center };
}
