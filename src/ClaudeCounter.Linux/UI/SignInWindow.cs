using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.UI;

/// <summary>Thin host for <see cref="SignInView"/>, opened from the tray menu or the flyout.</summary>
public sealed class SignInWindow : Window
{
    private readonly SignInView _view;

    /// <summary>Raised on the UI thread once a session has been stored.</summary>
    public event Action<OAuthSession>? SignedIn;

    public SignInWindow(SignInCoordinator coordinator, Func<string, bool>? openBrowser = null)
    {
        Title = "Sign in to Claude";
        Width = 480;
        Height = 380;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _view = new SignInView(coordinator, openBrowser);
        _view.SignedIn += OnSignedIn;

        var cancelButton = new Button { Content = "Cancel" };
        cancelButton.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 10,
            Children =
            {
                _view,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancelButton },
                },
            },
        };
    }

    private void OnSignedIn(OAuthSession session)
    {
        SignedIn?.Invoke(session);
        // Give the success message a moment to register before the window
        // vanishes; closing instantly reads as "did that work?".
        _ = CloseShortlyAsync();
    }

    private async Task CloseShortlyAsync()
    {
        await Task.Delay(900);
        Dispatcher.UIThread.Post(Close);
    }
}
