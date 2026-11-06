namespace AuroraShell;

/// <summary>
/// 壳的应用对象：一个窗口，电脑上记住窗口位置与大小（REQ-SHELL-003）。
/// </summary>
public sealed class App : Application
{
    private const string BoundsKey = "shell.window";

    protected override Window CreateWindow(Microsoft.Maui.IActivationState? activationState)
    {
        var window = new Window(new ShellPage()) { Title = "OneHistory · Aurora" };
        if (DeviceInfo.Idiom == DeviceIdiom.Desktop)
        {
            if (ReadBounds() is { } bounds)
            {
                window.X = bounds.X;
                window.Y = bounds.Y;
                window.Width = bounds.Width;
                window.Height = bounds.Height;
            }
            else
            {
                window.Width = 1440;
                window.Height = 900;
            }
            window.Destroying += (_, _) => SaveBounds(window);
        }
        return window;
    }

    private static Rect? ReadBounds()
    {
        var parts = Preferences.Default.Get(BoundsKey, "").Split(',');
        if (parts.Length != 4 || !parts.All(part => double.TryParse(part, System.Globalization.CultureInfo.InvariantCulture, out _)))
            return null;
        var values = parts.Select(part => double.Parse(part, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return values[2] >= 480 && values[3] >= 320 ? new Rect(values[0], values[1], values[2], values[3]) : null;
    }

    private static void SaveBounds(Window window)
    {
        if (window.Width < 480 || window.Height < 320)
            return;
        Preferences.Default.Set(BoundsKey, string.Join(',', new[] { window.X, window.Y, window.Width, window.Height }
            .Select(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
