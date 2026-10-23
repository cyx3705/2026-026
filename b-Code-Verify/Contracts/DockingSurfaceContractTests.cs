using System.Reflection;
using System.Runtime.CompilerServices;
using HistoryAurora.Shell.Components.Table;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 停靠系统、窗体与装配清单都不是公开面（1.7.0）。
///
/// **没有任何一个模块引用 Aurora 的程序集**——模块只经指令总线打交道，
/// 想开窗口用 <c>ui.window</c> 注解或页面注册协议。
/// 一个没有消费者的公开面不会带来兼容性，只会带来"这个签名不敢改"。
/// 谁要把某个类型重新 public 出去，先来改这个测试，那时才会重新读一遍上面这段。
/// </summary>
public sealed class DockingSurfaceContractTests
{
    private static Assembly Aurora => typeof(AuroraTable).Assembly;

    [Fact]
    public void ShellInternalsStayInternal()
    {
        var exported = Aurora.GetExportedTypes().Select(type => type.FullName!).ToList();

        Assert.DoesNotContain(exported, name => name.StartsWith("HistoryAurora.Shell.Base.Docking", StringComparison.Ordinal));
        // 窗体与装配清单同样只对本程序集有意义：ShellConfig 里装的就是停靠描述符。
        Assert.DoesNotContain("HistoryAurora.Shell.Composition.ShellWindow", exported);
        Assert.DoesNotContain("HistoryAurora.Shell.Composition.ShellConfig", exported);
        Assert.DoesNotContain("HistoryAurora.Shell.Composition.ShellCommandServices", exported);

        // 授权给产品程序集等于把收起来的面重新放开一条缝；失效的授权看起来与有效的一模一样。
        var granted = Aurora.GetCustomAttributes<InternalsVisibleToAttribute>()
            .Select(attribute => attribute.AssemblyName.Split(',')[0].Trim())
            .ToList();
        Assert.NotEmpty(granted);
        Assert.All(granted, name => Assert.Contains(name, new[] { "Contracts", "ModuleSmoke" }));
    }
}
