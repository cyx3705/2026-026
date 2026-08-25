using System.Reflection;
using System.Runtime.CompilerServices;
using HistoryAurora.Shell.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 停靠系统不再是公开面（1.7.0）。
///
/// 它曾经是宿主 Core 的一部分，5.0 迁进 Aurora 之后仍以 public 挂在外面，
/// 而**没有任何一个模块引用 Aurora 的程序集**——模块只经指令总线打交道，
/// 想开窗口用 <c>ui.window</c> 注解或页面注册协议。
/// 一个没有消费者的公开面不会带来兼容性，只会带来"这个签名不敢改"。
///
/// 这条门禁把它钉住：谁要把某个停靠类型重新 public 出去，先来改这个测试，
/// 那时才会重新读一遍上面这段。
/// </summary>
public sealed class DockingSurfaceContractTests
{
    private static Assembly Aurora => typeof(AuroraTable).Assembly;

    [Fact]
    public void DockingNamespaceExportsNothing()
    {
        var exported = Aurora.GetExportedTypes()
            .Where(type => (type.Namespace ?? "").StartsWith(
                "HistoryAurora.Shell.Docking",
                StringComparison.Ordinal))
            .Select(type => type.FullName!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(exported);
    }

    [Fact]
    public void ShellAssemblyDoesNotExposeItsWindowOrConfiguration()
    {
        // 窗体与装配清单同样只对本程序集有意义：ShellConfig 里装的就是停靠描述符。
        var exported = Aurora.GetExportedTypes().Select(type => type.FullName!).ToHashSet(StringComparer.Ordinal);

        Assert.DoesNotContain("HistoryAurora.Shell.ShellWindow", exported);
        Assert.DoesNotContain("HistoryAurora.Shell.ShellConfig", exported);
        Assert.DoesNotContain("HistoryAurora.Shell.ShellCommandServices", exported);
    }

    [Fact]
    public void InternalsAreVisibleOnlyToTheVerificationProjects()
    {
        // 授权给产品程序集等于把刚收起来的停靠面重新放开一条缝，
        // 而失效的授权（分仓后不再编译本程序集的宿主测试）看起来与有效的一模一样。
        string[] allowed = ["Smoke", "Contracts", "ModuleSmoke"];

        var granted = Aurora.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0].Trim())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(granted);
        Assert.All(granted, name => Assert.Contains(name, allowed));
    }
}
