using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.UI;

/// <summary>
/// The whole sign-in interaction (open browser -> paste code -> connect) as a
/// control rather than a window, so the standalone dialog and the onboarding
/// wizard host the identical thing instead of two implementations that drift
/// apart - mirrors the Windows build's SignInPanel/SignInForm split.
/// </summary>
public sealed class SignInView : UserControl
{
    private enum State { Idle, BrowserOpened, Exchanging, Succeeded, Failed }

    private readonly SignInCoordinator _coordinator;
    private readonly Func<string, bool> _openBrowser;

    private readonly Button _openButton;
    private readonly TextBox _urlBox;
    private readonly TextBox _codeBox;
    private readonly Button _connectButton;
    private readonly TextBlock _status;
    private readonly ProgressBar _progress;

    private State _state = State.Idle;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on the UI thread once a session has been stored.</summary>
    public event Action<OAuthSession>? SignedIn;

    /// <summary>Raised whenever the buttons' enablement changes, for a host that shows its own state-dependent controls.</summary>
    public event Action? StateChanged;

    public bool IsComplete => _state == State.Succeeded;

    /// <summary>
    /// What to say once sign-in succeeds. The default suits a dialog that
    /// closes itself straight afterwards; a host that stays open (the
    /// onboarding wizard) should say what the user does next.
    /// </summary>
    public string SuccessMessage { get; set; } = "Signed in. Fetching your usage...";

    /// <summary>The button a hosting window should make its default (Enter-triggered) button.</summary>
    public Button DefaultButton => _connectButton;

    public SignInView(SignInCoordinator coordinator, Func<string, bool>? openBrowser = null)
    {
        _coordinator = coordinator;
        _openBrowser = openBrowser ?? Shell.OpenUrl;

        _openButton = new Button { Content = "Open Claude in your browser" };
        _openButton.Click += (_, _) => StartAttempt();

        _urlBox = new TextBox { IsReadOnly = true, IsVisible = false, PlaceholderText = "Authorization URL" };

        _codeBox = new TextBox { PlaceholderText = "Paste the code from your browser", IsEnabled = false };
        _codeBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                Sync();
        };

        _connectButton = new Button { Content = "Connect", IsEnabled = false, IsDefault = true };
        _connectButton.Click += async (_, _) => await CompleteAsync();

        _progress = new ProgressBar { IsIndeterminate = true, IsVisible = false, Height = 4 };

        _status = new TextBlock
        {
            Text = "Not signed in.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = "ClaudeCounter opens claude.ai in your browser. After you approve, " +
                           "Anthropic shows you a code - paste it below.\n\n" +
                           "ClaudeCounter keeps its own session and never touches Claude Code's.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Gray,
                },
                _openButton,
                _urlBox,
                _codeBox,
                _progress,
                _status,
                _connectButton,
            },
        };
        _connectButton.HorizontalAlignment = HorizontalAlignment.Right;
    }

    private void StartAttempt()
    {
        var request = _coordinator.Begin();
        _codeBox.Text = "";
        _urlBox.Text = request.Url;

        if (_openBrowser(request.Url))
        {
            SetState(State.BrowserOpened, "Waiting for you to approve in the browser...", Brushes.Gray);
        }
        else
        {
            _urlBox.IsVisible = true;
            SetState(State.BrowserOpened,
                "Couldn't open a browser. Copy the link above and open it yourself.", Brushes.Orange);
        }
        _codeBox.Focus();
    }

    private async Task CompleteAsync()
    {
        if (_state == State.Exchanging)
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        SetState(State.Exchanging, "Signing in...", Brushes.Gray);

        SignInOutcome outcome;
        try
        {
            outcome = await _coordinator.CompleteAsync(_codeBox.Text, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new SignInOutcome.Transient("The request timed out.");
        }

        Render(outcome);
    }

    private void Render(SignInOutcome outcome)
    {
        switch (outcome)
        {
            case SignInOutcome.Success success:
                SetState(State.Succeeded, SuccessMessage, Brushes.LimeGreen);
                SignedIn?.Invoke(success.Session);
                break;

            case SignInOutcome.NotStarted:
                Fail("Open Claude in your browser first.");
                break;

            case SignInOutcome.BadInput:
                Fail("That doesn't look like a code. Copy the whole value Anthropic showed you.");
                break;

            case SignInOutcome.StateMismatch:
                Fail("That code is from a different sign-in attempt. Click the button above to start over.");
                break;

            case SignInOutcome.Rejected:
                Fail("Anthropic rejected the code. Codes expire after a few minutes - " +
                     "click the button above to try again.");
                break;

            case SignInOutcome.Transient transient:
                Fail($"Couldn't reach Anthropic ({transient.Message}). Check your connection and try again.");
                break;
        }
    }

    private void Fail(string message)
    {
        SetState(State.Failed, message, Brushes.OrangeRed);
        _codeBox.Focus();
        _codeBox.SelectAll();
    }

    private void SetState(State state, string status, IBrush color)
    {
        _state = state;
        _status.Text = status;
        _status.Foreground = color;
        Sync();
    }

    private void Sync()
    {
        var busy = _state == State.Exchanging;
        var done = _state == State.Succeeded;
        var started = _state is State.BrowserOpened or State.Failed;

        _openButton.IsEnabled = !busy && !done;
        _openButton.Content = _state switch
        {
            State.Idle or State.Succeeded => "Open Claude in your browser",
            State.Failed => "Start over",
            _ => "Reopen browser",
        };

        _codeBox.IsEnabled = started && !busy;
        _connectButton.IsEnabled = started && !busy && _codeBox.Text?.Trim().Length > 0;
        _progress.IsVisible = busy;

        StateChanged?.Invoke();
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        base.OnUnloaded(e);
    }
}
