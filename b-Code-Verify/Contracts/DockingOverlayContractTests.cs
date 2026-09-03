using System.IO;
using System.Xml.Linq;
using Xunit;

namespace HistoryAurora.Verify;

/// <summary>
/// Architecture gates for the docking overlay. The AvalonDock overlay has two
/// different responsibilities: its named controls provide hit rectangles, while
/// their content is only artwork. These checks prevent a future theme edit from
/// coupling those responsibilities again.
/// </summary>
public sealed class DockingOverlayContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AuroraOverlayContainsEveryAvalonDockTemplatePartAndDropTarget()
    {
        var document = LoadOverlay();
        var names = document.Descendants()
            .Select(element => (string?)element.Attribute(X + "Name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);

        var expected = new[]
        {
            "PART_DropTargetsContainer",
            "PART_PreviewBox",
            "PART_DockingManagerDropTargets",
            "PART_AnchorablePaneDropTargets",
            "PART_DocumentPaneDropTargets",
            "PART_DocumentPaneFullDropTargets",
            "PART_DockingManagerDropTargetLeft",
            "PART_DockingManagerDropTargetRight",
            "PART_DockingManagerDropTargetBottom",
            "PART_DockingManagerDropTargetTop",
            "PART_AnchorablePaneDropTargetTop",
            "PART_AnchorablePaneDropTargetRight",
            "PART_AnchorablePaneDropTargetBottom",
            "PART_AnchorablePaneDropTargetLeft",
            "PART_AnchorablePaneDropTargetInto",
            "PART_DocumentPaneDropTargetTop",
            "PART_DocumentPaneDropTargetRight",
            "PART_DocumentPaneDropTargetBottom",
            "PART_DocumentPaneDropTargetLeft",
            "PART_DocumentPaneDropTargetInto",
            "PART_DocumentPaneFullDropTargetTop",
            "PART_DocumentPaneFullDropTargetRight",
            "PART_DocumentPaneFullDropTargetBottom",
            "PART_DocumentPaneFullDropTargetLeft",
            "PART_DocumentPaneFullDropTargetInto",
            "PART_DocumentPaneDropTargetTopAsAnchorablePane",
            "PART_DocumentPaneDropTargetRightAsAnchorablePane",
            "PART_DocumentPaneDropTargetBottomAsAnchorablePane",
            "PART_DocumentPaneDropTargetLeftAsAnchorablePane",
        };

        Assert.All(expected, name => Assert.Contains(name, names));
    }

    [Fact]
    public void OverlaySeparatesStableHitRectanglesFromClippedIndicatorArtwork()
    {
        var document = LoadOverlay();
        var hitStyle = document.Descendants(Xaml + "Style")
            .Single(style => (string?)style.Attribute(X + "Key") == "AuroraOverlayHitTarget");

        Assert.Equal("40", SetterValue(hitStyle, "Width"));
        Assert.Equal("40", SetterValue(hitStyle, "Height"));
        Assert.Equal("Center", SetterValue(hitStyle, "HorizontalContentAlignment"));
        Assert.Equal("Center", SetterValue(hitStyle, "VerticalContentAlignment"));
        Assert.Equal("{StaticResource AuroraOverlayIndicatorTemplate}",
            SetterValue(hitStyle, "ContentTemplate"));

        var targetControls = document.Descendants(Xaml + "ContentControl")
            .Where(control => ((string?)control.Attribute(X + "Name"))?.Contains("DropTarget", StringComparison.Ordinal) == true)
            .ToList();
        Assert.Equal(23, targetControls.Count);
        Assert.All(targetControls, control =>
            Assert.Equal("{StaticResource AuroraOverlayHitTarget}",
                (string?)control.Attribute("Style")));

        var indicator = document.Descendants(Xaml + "DataTemplate")
            .Single(template => (string?)template.Attribute(X + "Key") == "AuroraOverlayIndicatorTemplate");
        var border = indicator.Descendants(Xaml + "Border").Single();
        Assert.Equal("28", (string?)border.Attribute("Width"));
        Assert.Equal("28", (string?)border.Attribute("Height"));
        Assert.Equal("True", (string?)border.Attribute("ClipToBounds"));
        Assert.Equal("False", (string?)border.Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void PreviewBoxIsAnIndependentTransparentOutline()
    {
        var preview = LoadOverlay().Descendants(Xaml + "Path")
            .Single(path => (string?)path.Attribute(X + "Name") == "PART_PreviewBox");

        Assert.Equal("Transparent", (string?)preview.Attribute("Fill"));
        Assert.Equal("False", (string?)preview.Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void AuroraThemeInstallsTheOwnedOverlayTemplateAfterDockingStyles()
    {
        var document = XDocument.Load(Path.Combine(
            FindSourceRoot(), "b-Code-Studio", "Shell", "2-Components", "Themes", "AuroraTheme.xaml"));
        var sources = document.Descendants(Xaml + "ResourceDictionary")
            .Select(dictionary => (string?)dictionary.Attribute("Source"))
            .Where(source => source != null)
            .ToList();

        var docking = sources.FindIndex(source => source!.EndsWith("AuroraDocking.xaml", StringComparison.Ordinal));
        var overlay = sources.FindIndex(source => source!.EndsWith("AuroraOverlay.xaml", StringComparison.Ordinal));
        Assert.True(docking >= 0 && overlay > docking,
            $"AuroraOverlay must override docking defaults: docking={docking}, overlay={overlay}");
    }

    [Fact]
    public void ProbeIsReadOnlyAndCannotReintroduceRuntimeVisualRepair()
    {
        var root = FindSourceRoot();
        var probe = File.ReadAllText(Path.Combine(
            root, "b-Code-Studio", "Shell", "1-Base", "Docking", "DockingDragProbe.cs"));

        Assert.DoesNotContain("ApplyTemplate", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsurePreviewScale", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureDockingIndicatorVisuals", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("EnsureOpenOverlayVisuals", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("DockingOverlayResourceRepair", probe, StringComparison.Ordinal);
    }

    private static string SetterValue(XElement style, string property)
        => (string?)style.Elements(Xaml + "Setter")
            .Single(setter => (string?)setter.Attribute("Property") == property)
            .Attribute("Value") ?? string.Empty;

    private static XDocument LoadOverlay()
        => XDocument.Load(Path.Combine(
            FindSourceRoot(), "b-Code-Studio", "Shell", "2-Components", "Themes", "AuroraOverlay.xaml"));

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(
                    directory.FullName,
                    "b-Code-Studio",
                    "Shell",
                    "1-Base",
                    "Docking",
                    "WindowDragDriver.cs")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("无法定位 Aurora 源码根目录");
    }
}
