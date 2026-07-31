using System.Runtime.InteropServices;
using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.UI;

/// <summary>
/// The whole sign-in interaction, as a control rather than a dialog, so the
/// standalone dialog and the first-run wizard host the identical thing instead
/// of two implementations that drift apart.
/// </summary>
public sealed class SignInPanel : UserControl
{
    private const int ContentWidth = 430;

    private enum State { Idle, BrowserOpened, BrowserFailed, Exchanging, Succeeded, Failed }

    private readonly SignInCoordinator _coordinator;
    private readonly Func<string, bool> _openBrowser;

    private readonly Button _openButton;
    private readonly LinkLabel _copyLink;
    private readonly TextBox _urlBox;
    private readonly Label _codeLabel;
    private readonly TextBox _codeBox;
    private readonly Button _pasteButton;
    private readonly Button _connectButton;
    private readonly Label _status;
    private readonly ProgressBar _progress;

    private CancellationTokenSource? _cts;
    private State _state = State.Idle;

    /// <summary>Raised on the UI thread once a session has been stored.</summary>
    public event Action<OAuthSession>? SignedIn;

    /// <summary>Raised whenever the panel's buttons change enablement.</summary>
    public event Action? StateChanged;

    public bool IsComplete => _state == State.Succeeded;

    /// <summary>
    /// What to say once sign-in succeeds. The default suits a dialog that
    /// closes itself straight afterwards; a host that stays open should say
    /// what the user does next, or "Fetching..." reads as a hang.
    /// </summary>
    public string SuccessMessage { get; set; } = "Signed in. Fetching your usage...";

    public SignInPanel(SignInCoordinator coordinator, Func<string, bool>? openBrowser = null)
    {
        _coordinator = coordinator;
        _openBrowser = openBrowser ?? Shell.OpenUrl;

        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 9f);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var row = 0;

        AddSpanned(layout, new Label
        {
            Text = "Sign in to Claude",
            Font = new Font("Segoe UI Semibold", 10.5f),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        }, row++);

        AddSpanned(layout, new Label
        {
            Text = "ClaudeCounter opens claude.ai in your browser. After you approve, " +
                   "Anthropic shows you a code - paste it below.\r\n\r\n" +
                   "ClaudeCounter keeps its own session and never changes Claude Code's.",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 14),
        }, row++);

        _openButton = new Button
        {
            Text = "Open Claude in your browser",
            AutoSize = true,
            Margin = new Padding(0, 0, 12, 0),
        };
        _openButton.Click += (_, _) => StartAttempt();

        _copyLink = new LinkLabel
        {
            Text = "copy link instead",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 6, 0, 0),
        };
        _copyLink.LinkClicked += (_, _) => CopyLink();

        layout.Controls.Add(Row(_openButton, _copyLink), 0, row++);

        // Only shown when we could not launch a browser: a selectable box the
        // user can copy the URL out of by hand.
        _urlBox = new TextBox
        {
            ReadOnly = true,
            Width = ContentWidth,
            Visible = false,
            Margin = new Padding(0, 0, 0, 8),
        };
        AddSpanned(layout, _urlBox, row++);

        _codeLabel = new Label
        {
            Text = "Paste the code from your browser",
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 2),
        };
        AddSpanned(layout, _codeLabel, row++);

        _codeBox = new TextBox { Width = ContentWidth - 80, Margin = new Padding(0, 1, 8, 0) };
        _codeBox.TextChanged += (_, _) => Sync();

        _pasteButton = new Button { Text = "Paste", AutoSize = true, Margin = new Padding(0) };
        _pasteButton.Click += (_, _) => PasteFromClipboard();

        layout.Controls.Add(Row(_codeBox, _pasteButton), 0, row++);

        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            Width = ContentWidth,
            Height = 4,
            Visible = false,
            Margin = new Padding(0, 8, 0, 4),
        };
        AddSpanned(layout, _progress, row++);

        _status = new Label
        {
            Text = "Not signed in.",
            AutoSize = true,
            MaximumSize = new Size(ContentWidth, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 6, 0, 6),
        };
        AddSpanned(layout, _status, row++);

        _connectButton = new Button
        {
            Text = "Connect",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 4, 0, 0),
        };
        _connectButton.Click += async (_, _) => await CompleteAsync();
        layout.Controls.Add(_connectButton, 0, row);

        Controls.Add(layout);
        Sync();
    }

    /// <summary>The button a hosting form should make its AcceptButton.</summary>
    public IButtonControl DefaultButton => _connectButton;

    private static void AddSpanned(TableLayoutPanel layout, Control control, int row) =>
        layout.Controls.Add(control, 0, row);

    /// <summary>Lays controls side by side so a trailing link sits beside its
    /// button rather than being pushed to the far edge by a stretched column.</summary>
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4),
        };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private void StartAttempt()
    {
        var request = _coordinator.Begin();
        _codeBox.Clear();
        _urlBox.Text = request.Url;

        if (_openBrowser(request.Url))
        {
            SetState(State.BrowserOpened, "Waiting for you to approve in the browser...",
                SystemColors.GrayText);
        }
        else
        {
            SetState(State.BrowserFailed,
                "Couldn't open a browser. Copy the link below and open it yourself.",
                Theme.BandColor(Band.Amber));
        }
        _codeBox.Focus();
    }

    private void CopyLink()
    {
        // Begin() if the user goes straight for the link without pressing the
        // button, so the URL they copy belongs to a live attempt.
        if (_state is State.Idle or State.Succeeded)
            StartAttemptWithoutBrowser();

        try
        {
            Clipboard.SetText(_urlBox.Text);
            _status.Text = "Link copied. Open it in your browser, then paste the code below.";
            _status.ForeColor = SystemColors.GrayText;
        }
        catch (ExternalException)
        {
            // Another process can hold the clipboard open; the box is selectable.
            _status.Text = "Couldn't copy. Select the link below and copy it manually.";
            _status.ForeColor = Theme.BandColor(Band.Amber);
        }
        _urlBox.Visible = true;
    }

    private void StartAttemptWithoutBrowser()
    {
        var request = _coordinator.Begin();
        _codeBox.Clear();
        _urlBox.Text = request.Url;
        SetState(State.BrowserFailed, "Open the link below, then paste the code.", SystemColors.GrayText);
    }

    private void PasteFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
                _codeBox.Text = Clipboard.GetText().Trim();
        }
        catch (ExternalException)
        {
            _status.Text = "Couldn't read the clipboard. Paste into the box with Ctrl+V.";
            _status.ForeColor = Theme.BandColor(Band.Amber);
        }
    }

    private async Task CompleteAsync()
    {
        if (_state == State.Exchanging)
            return;

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        SetState(State.Exchanging, "Signing in...", SystemColors.GrayText);

        SignInOutcome outcome;
        try
        {
            outcome = await _coordinator.CompleteAsync(_codeBox.Text, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new SignInOutcome.Transient("The request timed out.");
        }

        if (IsDisposed)
            return;

        Render(outcome);
    }

    private void Render(SignInOutcome outcome)
    {
        switch (outcome)
        {
            case SignInOutcome.Success success:
                SetState(State.Succeeded, SuccessMessage, Theme.BandColor(Band.Green));
                SignedIn?.Invoke(success.Session);
                break;

            case SignInOutcome.NotStarted:
                Fail("Open Claude in your browser first.");
                break;

            case SignInOutcome.BadInput:
                Fail("That doesn't look like a code. Copy the whole value Anthropic showed you.");
                break;

            case SignInOutcome.StateMismatch:
                Fail("That code is from a different sign-in attempt. Click Start over.");
                break;

            case SignInOutcome.Rejected:
                // Codes are single-use and short-lived, so there is nothing to
                // retry - the only way forward is a fresh attempt.
                Fail("Anthropic rejected the code. Codes expire after a few minutes - " +
                     "click Start over and try again.");
                break;

            case SignInOutcome.Transient transient:
                Fail($"Couldn't reach Anthropic ({transient.Message}). " +
                     "Check your connection and try again.");
                break;
        }
    }

    private void Fail(string message)
    {
        SetState(State.Failed, message, Theme.BandColor(Band.Red));
        _codeBox.Focus();
        _codeBox.SelectAll();
    }

    private void SetState(State state, string status, Color color)
    {
        _state = state;
        _status.Text = status;
        _status.ForeColor = color;
        Sync();
    }

    private void Sync()
    {
        var busy = _state == State.Exchanging;
        var done = _state == State.Succeeded;
        var started = _state is State.BrowserOpened or State.BrowserFailed or State.Failed;

        _openButton.Enabled = !busy && !done;
        _openButton.Text = _state switch
        {
            State.Idle or State.Succeeded => "Open Claude in your browser",
            State.Failed => "Start over",
            _ => "Reopen browser",
        };

        _copyLink.Enabled = !busy && !done;
        _urlBox.Visible = _state == State.BrowserFailed || (_urlBox.Visible && !done);

        _codeLabel.Enabled = started && !busy;
        _codeBox.Enabled = started && !busy;
        _pasteButton.Enabled = started && !busy;
        _connectButton.Enabled = started && !busy && _codeBox.Text.Trim().Length > 0;

        _progress.Visible = busy;

        StateChanged?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts?.Cancel();
            _cts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
