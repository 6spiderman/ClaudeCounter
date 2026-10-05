using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;

namespace ClaudeCounter;

public sealed class App : Application
{
    private TrayApplicationContext? _trayContext;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _trayContext = new TrayApplicationContext(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
