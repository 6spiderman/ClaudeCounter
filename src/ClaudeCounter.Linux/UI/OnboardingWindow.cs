using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeCounter.Core.Auth;
using ClaudeCounter.Settings;

namespace ClaudeCounter.UI;

/// <summary>
/// First-run wizard: what the app does, sign in, set preferences. Step 2
/// hosts the same <see cref="SignInView"/> the tray menu's dialog opens, so
/// there is one sign-in implementation rather than two - mirrors the Windows
/// build's OnboardingForm.
/// </summary>
public sealed class OnboardingWindow : Window
{
    private const int Steps = 3;

    private readonly AppSettings _settings;
    private readonly SignInView _signInView;
    private readonly ContentControl _host;
    private readonly Control[] _pages;
    private readonly Button _backButton;
    private readonly Button _nextButton;
    private readonly Button _skipButton;
    private readonly ComboBox _intervalCombo;
    private readonly CheckBox _autostartCheck;

    private int _step;

    public bool SignedIn { get; private set; }

    /// <summary>
    /// Raised the moment sign-in succeeds, not when the wizard closes, so the
    /// tray icon can refresh while the user is still reading the step that
    /// tells them to go and look at it.
    /// </summary>
    public event Action? SignInCompleted;

    public OnboardingWindow(SignInCoordinator coordinator, AppSettings settings)
    {
        _settings = settings;

        Title = "Welcome to ClaudeCounter";
        Width = 560;
        Height = 460;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        _signInView = new SignInView(coordinator)
        {
            // The wizard stays open after this, so the default
            // "Fetching your usage..." would sit there looking stuck.
            SuccessMessage = "Signed in. Your usage is in the system tray now - " +
                             "look for the coloured icon by the clock.",
        };
        _signInView.StateChanged += Sync;
        _signInView.SignedIn += _ =>
        {
            SignedIn = true;
            SignInCompleted?.Invoke();
            Sync();
        };

        _intervalCombo = new ComboBox
        {
            ItemsSource = AppSettings.IntervalPresets.Select(m => $"{m} min").ToArray(),
            SelectedIndex = Math.Max(0, Array.IndexOf(AppSettings.IntervalPresets, settings.PollIntervalMinutes)),
        };
        _autostartCheck = new CheckBox
        {
            Content = "Start ClaudeCounter when I log in",
            IsChecked = settings.AutostartEnabled,
        };

        _pages = [BuildWelcome(), BuildConnect(), BuildPreferences()];
        _host = new ContentControl { Margin = new Avalonia.Thickness(0, 0, 0, 16) };

        _skipButton = new Button { Content = "Skip for now" };
        _skipButton.Click += (_, _) => Finish();

        _backButton = new Button { Content = "Back" };
        _backButton.Click += (_, _) => GoBack();

        _nextButton = new Button { Content = "Next", IsDefault = true };
        _nextButton.Click += (_, _) => Advance();

        var nav = new DockPanel();
        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { _backButton, _nextButton },
        };
        DockPanel.SetDock(rightButtons, Dock.Right);
        DockPanel.SetDock(_skipButton, Dock.Left);
        nav.Children.Add(rightButtons);
        nav.Children.Add(_skipButton);

        var root = new DockPanel { Margin = new Avalonia.Thickness(20) };
        DockPanel.SetDock(nav, Dock.Bottom);
        root.Children.Add(nav);
        root.Children.Add(_host);
        Content = root;

        ShowStep(0);
    }

    private Control BuildWelcome()
    {
        // Rendered from IconRenderer rather than shipped as an asset: it is
        // the literal thing the user is about to see in their tray.
        var sample = new Image
        {
            Width = 48,
            Height = 48,
            Source = IconRenderer.RenderBitmap("42", Band.Green),
        };

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children =
            {
                sample,
                new TextBlock
                {
                    Text = "Your Claude usage, always visible",
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            },
        };

        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                header,
                new TextBlock
                {
                    Text = "That icon sits in your system tray showing how much of your five-hour " +
                           "session you have used. It turns amber, then red, as you approach your " +
                           "limit. Click it for the weekly and per-model windows and a countdown to " +
                           "each reset.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                },
                new TextBlock
                {
                    Text = "What it reads, and what it doesn't",
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Avalonia.Thickness(0, 10, 0, 0),
                },
                new TextBlock
                {
                    Text = "ClaudeCounter reads your plan usage from Anthropic and nothing else. " +
                           "Your sign-in is stored on this keyring, for your account on this PC only, " +
                           "and is never sent anywhere except Anthropic's own servers.\n\n" +
                           "There is no telemetry and no analytics.\n\n" +
                           "ClaudeCounter is not affiliated with, endorsed by, or supported by Anthropic.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                },
            },
        };
    }

    private Control BuildConnect() => _signInView;

    private Control BuildPreferences()
    {
        var intervalRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = "Check my usage every", VerticalAlignment = VerticalAlignment.Center }, _intervalCombo },
        };

        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "A couple of preferences", FontSize = 16, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = "You can change these any time from the tray menu.",
                    Foreground = Brushes.Gray,
                },
                intervalRow,
                new TextBlock
                {
                    Text = "Anything under 3 minutes may be rate limited; ClaudeCounter backs off " +
                           "automatically when that happens.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                },
                _autostartCheck,
            },
        };
    }

    private void ShowStep(int step)
    {
        _step = Math.Clamp(step, 0, Steps - 1);
        _host.Content = _pages[_step];
        Sync();
    }

    private void Advance()
    {
        if (_step < Steps - 1)
            ShowStep(_step + 1);
        else
            Finish();
    }

    private void GoBack() => ShowStep(_step - 1);

    private void Sync()
    {
        _backButton.IsEnabled = _step > 0;
        _nextButton.Content = _step == Steps - 1 ? "Finish" : "Next";

        // Sign-in is genuinely optional - a user with Claude Code installed
        // already sees their usage - so Next is never blocked, and skip stays
        // available throughout.
        _skipButton.IsVisible = _step < Steps - 1;
        _skipButton.Content = _step == 1 && !SignedIn ? "Skip for now" : "Skip setup";
    }

    private void Finish()
    {
        _settings.PollIntervalMinutes = AppSettings.IntervalPresets[_intervalCombo.SelectedIndex];
        _settings.AutostartEnabled = _autostartCheck.IsChecked ?? false;
        _settings.OnboardingCompleted = true;
        Close();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing with the X still counts as done; nobody wants this window
        // reappearing every launch because they dismissed it.
        _settings.OnboardingCompleted = true;
        base.OnClosing(e);
    }
}
