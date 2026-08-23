namespace HistoryAurora.Shell.Panels;

/// <summary>控制面板声明。5.0 起从宿主 Extensibility 迁入 Aurora。</summary>
public sealed class PanelDefinition
{
    public required string Id { get; set; }

    public required string Title { get; set; }

    public bool Visible { get; set; } = true;

    public string Side { get; set; } = "right";

    public double Ratio { get; set; } = 0.22;

    public List<PanelControl> Controls { get; set; } = new();
}

public sealed class PanelControl
{
    public required string Type { get; set; }

    public string? Id { get; set; }

    public string? Label { get; set; }

    public string? Command { get; set; }

    public List<string>? Items { get; set; }

    public double? Min { get; set; }

    public double? Max { get; set; }

    public double? Step { get; set; }

    public string? Default { get; set; }

    public string? Style { get; set; }

    public bool Required { get; set; }
}
