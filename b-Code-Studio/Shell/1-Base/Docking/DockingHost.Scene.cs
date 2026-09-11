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

    /// <summary>按场景初值露面：位置空着才露面，不顶掉任何一页；默认就不显示的页不动。</summary>
    void ShowIfSeatFree(string id);

    void SaveLayout(string name);

    void ApplyScene(
        string name,
        IReadOnlyCollection<string> seed,
        bool rebuild,
        IReadOnlyCollection<string>? prefer = null);

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
    /// 一格一页（REQ-UI-100）：按初值露面的页不顶掉任何一页，位置被占着就不露面；
    /// 只有 <paramref name="prefer"/> 里的页（模块场景传该模块自己的页）会顶掉它位置上的页——
    /// 于是 Janus 的「图」与常驻的控制台同一格时，进 Janus 露「图」。
    ///
    /// **页面视图不重建**：内容对象按 id 缓存在 <c>_contents</c>，恢复快照换的只是停靠模型
    /// （REQ-UI-086）。切走再切回来，页面里选好的来源、表格的选中行都还在。
    /// </summary>
    public void ApplyScene(
        string name,
        IReadOnlyCollection<string> seed,
        bool rebuild,
        IReadOnlyCollection<string>? prefer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(seed);
        RestoreLayoutFromMaximized();

        var initial = new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);
        var preferred = new HashSet<string>(prefer ?? [], StringComparer.OrdinalIgnoreCase);
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
                    // 1.20.1 及以前存下的场景布局里一格可能有好几页：每格留选中的那一页。
                    EvictExtraPages();
                    SeedVisibility(name, _descriptors.Where(d => !known.Contains(d.Id)).ToArray(), initial, preferred);

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

            if (!restored)
                SeedVisibility(name, _descriptors.ToArray(), initial, preferred);

            EnsureCentralWorkspace();
            AttachLayout();
            CurrentLayoutName = name;
        }

        ScheduleReapplyRatios();
        RebaseSoon();
        WindowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 按初值定显隐，分三步：不在初值里的藏起来；在初值里、默认就该露面而此刻藏着的，位置空着才露面；
    /// 最后 <paramref name="preferred"/> 里的页露面并顶掉它位置上的页。
    /// 默认就不显示的页（DefaultVisible = false）不替它做主。
    /// </summary>
    private void SeedVisibility(
        string scene,
        IReadOnlyList<ToolWindowDescriptor> descriptors,
        HashSet<string> initial,
        HashSet<string> preferred)
    {
        foreach (var descriptor in descriptors.Where(d => !initial.Contains(d.Id)))
            Seed(scene, descriptor.Id, () => HidePage(FindRequiredAnchorable(descriptor.Id)));

        var shown = descriptors
            .Where(d => initial.Contains(d.Id) && d.DefaultVisible)
            .OrderBy(d => preferred.Contains(d.Id))
            .ToArray();
        foreach (var descriptor in shown)
        {
            var id = descriptor.Id;
            if (preferred.Contains(id))
                Seed(scene, id, () => Show(id));
            else if (!ComputeState(id).Visible)
                Seed(scene, id, () => ShowIfSeatFree(id));
        }
    }

    private void Seed(string scene, string id, Action action)
    {
        try
        {
            EnsureRegistered(id);
            action();
        }
        catch (Exception ex)
        {
            _log.Warn(LayoutSource, $"场景 {scene} 处理窗口 {id} 失败: {ex.Message}");
        }
    }
}
