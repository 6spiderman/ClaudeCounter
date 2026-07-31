using ClaudeCounter.Core;

namespace ClaudeCounter.UI;

public sealed class AboutForm : Form
{
    private readonly UpdateChecker _updates;
    private readonly Button _checkButton;
    private readonly Label _updateStatus;
    private readonly LinkLabel _downloadLink;
    private readonly Label _wingetHint;

    private CancellationTokenSource? _checkCts;
    private string? _downloadUrl;

    public AboutForm(UpdateChecker updates)
    {
        _updates = updates;

        Text = "About ClaudeCounter";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(440, 280);
        Font = new Font("Segoe UI", 9f);
        // A tray app owns no window, so a dialog can otherwise open behind
        // whatever the user was looking at.
        ShowInTaskbar = true;
        Icon = Shell.AppIcon();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 12),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var iconBox = new PictureBox
        {
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.Zoom,
            Image = Shell.AppIcon(64)?.ToBitmap(),
            Margin = new Padding(0, 2, 0, 0),
        };
        layout.Controls.Add(iconBox, 0, 0);
        layout.SetRowSpan(iconBox, 3);

        layout.Controls.Add(new Label
        {
            Text = $"ClaudeCounter {AppInfo.DisplayVersion}",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
        }, 1, 0);

        layout.Controls.Add(new Label
        {
            Text = "Your Claude plan usage in the system tray.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 2),
        }, 1, 1);

        layout.Controls.Add(new Label
        {
            Text = "Not affiliated with, endorsed by, or supported by Anthropic.",
            AutoSize = true,
            MaximumSize = new Size(330, 0),
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(0, 0, 0, 14),
        }, 1, 2);

        var links = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
            WrapContents = false,
        };
        links.Controls.Add(NewLink("Project page", AppInfo.RepoUrl));
        links.Controls.Add(NewLink("Releases", AppInfo.ReleasesUrl));
        links.Controls.Add(NewLink("License", AppInfo.LicenseUrl));
        layout.Controls.Add(links, 1, 3);

        var logLink = new LinkLabel
        {
            Text = "Open log folder",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 10),
        };
        logLink.LinkClicked += (_, _) => Shell.ShowLogFolder();
        layout.Controls.Add(logLink, 1, 4);

        _checkButton = new Button
        {
            Text = "Check for updates",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        };
        _checkButton.Click += async (_, _) => await CheckAsync();
        layout.Controls.Add(_checkButton, 1, 5);

        _updateStatus = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            ForeColor = SystemColors.GrayText,
        };
        layout.Controls.Add(_updateStatus, 1, 6);

        _downloadLink = new LinkLabel { Text = "Download it", AutoSize = true, Visible = false };
        _downloadLink.LinkClicked += (_, _) =>
        {
            if (_downloadUrl is { } url)
                Shell.OpenUrl(url);
        };
        layout.Controls.Add(_downloadLink, 1, 7);

        _wingetHint = new Label
        {
            Text = $"Installed with winget? Run: winget upgrade {AppInfo.WingetId}",
            AutoSize = true,
            MaximumSize = new Size(320, 0),
            ForeColor = SystemColors.GrayText,
            Visible = false,
        };
        layout.Controls.Add(_wingetHint, 1, 8);

        var closeButton = new Button { Text = "Close", DialogResult = DialogResult.OK, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0),
        };
        buttons.Controls.Add(closeButton);
        layout.Controls.Add(buttons, 1, 9);

        Controls.Add(layout);
        AcceptButton = closeButton;
        CancelButton = closeButton;

        if (AppInfo.IsDevBuild)
            ShowStatus("Local development build - update checks are informational only.");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;
        Activate();
        TopMost = false;
    }

    private static LinkLabel NewLink(string text, string url)
    {
        var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 0, 14, 0) };
        link.LinkClicked += (_, _) => Shell.OpenUrl(url);
        return link;
    }

    private async Task CheckAsync()
    {
        _checkCts?.Cancel();
        _checkCts?.Dispose();
        _checkCts = new CancellationTokenSource();

        _checkButton.Enabled = false;
        _downloadLink.Visible = false;
        _wingetHint.Visible = false;
        ShowStatus("Checking...");

        try
        {
            var result = await _updates.CheckAsync(AppInfo.Version, _checkCts.Token);
            if (IsDisposed)
                return;
            Render(result);
        }
        catch (OperationCanceledException)
        {
            // Superseded by another click, or the form closed.
        }
        finally
        {
            if (!IsDisposed)
                _checkButton.Enabled = true;
        }
    }

    private void Render(UpdateCheck result)
    {
        switch (result)
        {
            case UpdateCheck.UpToDate:
                ShowStatus("You're on the latest version.");
                break;

            case UpdateCheck.Available available:
                ShowStatus($"v{available.Version} is available.");
                _downloadUrl = available.HtmlUrl;
                _downloadLink.Visible = true;
                // Portable users have nothing to upgrade with winget, so only
                // mention it when we are running from the installed location.
                _wingetHint.Visible = AppInfo.IsInstalled;
                break;

            case UpdateCheck.Failed failed:
                Log.Warn($"Update check failed: {failed.Message}");
                ShowStatus("Couldn't check for updates.");
                break;
        }
    }

    private void ShowStatus(string text) => _updateStatus.Text = text;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _checkCts?.Cancel();
            _checkCts?.Dispose();
        }
        base.Dispose(disposing);
    }
}
