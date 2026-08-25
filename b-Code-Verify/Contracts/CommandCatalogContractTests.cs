using HistoryAurora.Shell.CommandSurface;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// 命令集与补全的契约（REQ-UI-013 / REQ-UI-014）。
///
/// 这套会话原先由 HistoryMercury 的命令工作台提供。宿主 5.0 拆掉界面 SDK 之后没人再挂，
/// 于是**命令集与指令详情两页整体消失**，控制台按 Tab 也只剩一条
/// 「切换命令集失败」——目录是界面的基础设施，不该取决于哪个模块在不在场。
///
/// 因此这里断言的是"没有任何模块时它也成立"：下面每个用例都只有一张本地注册表。
/// </summary>
public sealed class CommandCatalogContractTests
{
    [Fact]
    public void Visible_FiltersByDomainClassAndQuery()
    {
        var session = Session();

        Assert.Equal(4, session.Visible().Count);

        Assert.True(session.TrySetDomain("demo", out _));
        Assert.Equal(3, session.Visible().Count);

        Assert.True(session.TrySetCommandClass("branch", out _));
        Assert.Equal(2, session.Visible().Count);

        session.SetFilter(session.CurrentFilter with { Query = "rename" });
        Assert.Equal("demo.branch.rename", Assert.Single(session.Visible()).Name);
    }

    [Fact]
    public void Domain_BackToAllCollapsesTheClassFilter()
    {
        // 严格两级（DEC-021）：类的取值范围随域收敛，否则会留下一个筛不出任何东西的组合。
        var session = Session();
        Assert.True(session.TrySetDomain("demo", out _));
        Assert.True(session.TrySetCommandClass("branch", out _));

        Assert.True(session.TrySetDomain("全部", out _));

        Assert.Equal("全部", session.CurrentFilter.CommandClass);
        Assert.Empty(session.Classes);
    }

    [Fact]
    public async Task Complete_SuggestsCommandNamesByPrefix()
    {
        var session = Session();

        var result = await session.CompleteAsync("demo.branch", 11);

        Assert.True(result.HasCandidates);
        Assert.All(result.Candidates, candidate =>
            Assert.Equal(ConsoleCompletionKind.Command, candidate.Kind));
        Assert.Contains(result.Candidates, candidate => candidate.InsertText == "demo.branch.rename");
        // 补全要替换掉已经打出来的那一段，而不是接在后面。
        Assert.Equal(0, result.ReplaceStart);
        Assert.Equal(11, result.ReplaceLength);
    }

    [Fact]
    public async Task Complete_FallsBackToContainsWhenNothingStartsWithIt()
    {
        // "想不起来是 dock 还是 layout，只记得中间一段"是这里最常见的用法。
        var session = Session();

        var result = await session.CompleteAsync("rename", 6);

        Assert.Contains(result.Candidates, candidate => candidate.InsertText == "demo.branch.rename");
    }

    [Fact]
    public async Task Complete_SuggestsParameterNamesAfterTheCommand()
    {
        var session = Session();

        var result = await session.CompleteAsync("demo.branch.rename t", 20);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(ConsoleCompletionKind.Parameter, candidate.Kind);
        Assert.Equal("to=", candidate.InsertText);
    }

    [Fact]
    public async Task Complete_DoesNotOfferAParameterThatIsAlreadyWritten()
    {
        var session = Session();

        var result = await session.CompleteAsync("demo.branch.rename to=x ", 24);

        Assert.DoesNotContain(result.Candidates, candidate => candidate.InsertText == "to=");
    }

    [Fact]
    public async Task Complete_SuggestsAllowedValuesAfterTheEqualsSign()
    {
        var session = Session();

        var result = await session.CompleteAsync("demo.branch.mode kind=f", 23);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(ConsoleCompletionKind.Value, candidate.Kind);
        Assert.Equal("fast", candidate.InsertText);
        // 只替换等号右边那一段，参数名要留着。
        Assert.Equal(22, result.ReplaceStart);
        Assert.Equal(1, result.ReplaceLength);
    }

    [Fact]
    public async Task Detail_ReadsParametersFromTheLocalRegistry()
    {
        var session = Session();

        var detail = await session.DetailAsync("demo.branch.rename");

        Assert.NotNull(detail);
        Assert.Equal("demo", detail!.Command.Domain);
        Assert.Equal("branch", detail.Command.CommandClass);
        var parameter = Assert.Single(detail.Parameters);
        Assert.Equal("to", parameter.Name);
        Assert.True(parameter.Required);
    }

    [Fact]
    public async Task Detail_ReturnsNullForAnUnknownCommand()
    {
        var session = Session();

        Assert.Null(await session.DetailAsync("nobody.registered.this"));
    }

    private static LocalCommandCatalogSession Session()
    {
        var registry = new CommandRegistry();
        registry.Register(Command("demo.branch.rename", "branch", "重命名分支",
            new ParameterSpec { Name = "to", Description = "新名字", Required = true, Position = 0 }));
        registry.Register(Command("demo.branch.mode", "branch", "切换模式",
            new ParameterSpec
            {
                Name = "kind",
                Description = "模式",
                AllowedValues = ["fast", "safe"],
            }));
        registry.Register(Command("demo.repo.list", "repo", "列出仓库"));
        registry.Register(Command("other.thing.do", "thing", "别的域"));

        var log = new MemoryLog();
        return new LocalCommandCatalogSession(new CommandBus(registry, log), log);
    }

    private static CommandDescriptor Command(
        string name,
        string commandClass,
        string summary,
        params ParameterSpec[] parameters) => new()
    {
        Name = name,
        Domain = name.Split('.')[0],
        CommandClass = commandClass,
        Summary = summary,
        Readonly = true,
        Parameters = parameters,
        Handler = CommandDescriptor.Sync(_ => CommandResult.Ok("ok")),
    };

    private sealed class MemoryLog : IShellLog
    {
        private readonly List<ShellLogEntry> _entries = [];

        public void Log(ShellLogLevel level, string category, string message)
        {
            var entry = new ShellLogEntry(DateTime.Now, level, category, message);
            _entries.Add(entry);
            EntryAdded?.Invoke(this, entry);
        }

        public event EventHandler<ShellLogEntry>? EntryAdded;

        public IReadOnlyList<ShellLogEntry> Snapshot() => _entries.ToList();
    }
}
