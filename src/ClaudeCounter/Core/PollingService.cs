using ClaudeCounter.Core.Auth;

namespace ClaudeCounter.Core;

public enum ProblemKind
{
    None,

    /// <summary>No usable token. Only the user can fix this, by signing in.</summary>
    SignInRequired,

    /// <summary>The usage endpoint rejected the token we had.</summary>
    TokenExpired,

    RateLimited,
    Network,
}

public sealed class PollState
{
    public UsageSnapshot? Snapshot { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public ProblemKind Problem { get; set; }
    public string? ProblemMessage { get; set; }

    /// <summary>Where the last token came from, or null if none was resolved.</summary>
    public TokenSource? Source { get; set; }

    /// <summary>True when running on Claude Code's session rather than our own.</summary>
    public bool IsBootstrapped => Source == TokenSource.CliBootstrap;
}

/// <summary>
/// Async poll loop. Start() must be called on the UI thread: every await then
/// resumes on the WinForms SynchronizationContext, so consumers of Updated
/// never need Invoke marshaling. Do not add ConfigureAwait(false) here.
/// </summary>
public sealed class PollingService : IDisposable
{
    // While borrowing Claude Code's session we re-check often: it is a cheap
    // file read, and the CLI may refresh the token for us at any moment.
    private static readonly TimeSpan BootstrapRetry = TimeSpan.FromMinutes(2);

    // Nothing will change until the user signs in, so stop hammering. Posting a
    // refresh token the server has already refused, every two minutes, forever,
    // is wasted traffic against an endpoint we are careful about.
    private static readonly TimeSpan SignInRetry = TimeSpan.FromMinutes(15);

    private readonly TokenProvider _credentials;
    private readonly UsageClient _client;
    private readonly Func<int> _getIntervalMinutes;
    private readonly CancellationTokenSource _cts = new();
    private readonly PollState _state = new();

    private CancellationTokenSource _wake = new();
    private int _consecutiveFailures;
    private TimeSpan? _retryAfter;
    private bool _started;

    public event Action<PollState>? Updated;

    public PollingService(TokenProvider credentials, UsageClient client, Func<int> getIntervalMinutes)
    {
        _credentials = credentials;
        _client = client;
        _getIntervalMinutes = getIntervalMinutes;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Log.Info($"Polling started. Interval: {_getIntervalMinutes()} min.");
        _ = RunLoopAsync();
    }

    /// <summary>Wake the loop for an immediate poll (manual refresh, settings change).</summary>
    public void TriggerNow() => _wake.Cancel();

    private async Task RunLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            await PollOnceAsync();
            if (_cts.IsCancellationRequested) break;

            var wake = _wake = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, wake.Token);
            try
            {
                await Task.Delay(NextDelay(), linked.Token);
            }
            catch (OperationCanceledException)
            {
                // Either shutdown (loop condition handles it) or TriggerNow.
            }
        }
    }

    private async Task PollOnceAsync()
    {
        _retryAfter = null;
        CredentialResult credential;
        try
        {
            credential = await _credentials.GetTokenAsync(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        switch (credential)
        {
            case CredentialResult.Token token:
                if (_state.Source != token.Source)
                    Log.Info($"Token source: {Describe(token.Source)}.");
                _state.Source = token.Source;
                await FetchAsync(token.AccessToken);
                break;
            case CredentialResult.SignInRequired required:
                _state.Source = null;
                SetProblem(ProblemKind.SignInRequired, $"{required.Reason} - sign in to Claude");
                Log.Warn($"Sign-in required: {required.Reason}");
                break;
            case CredentialResult.TransientError error:
                _consecutiveFailures++;
                SetProblem(ProblemKind.Network, $"Sign-in check failed: {error.Message}");
                Log.Warn($"Token resolution transient failure: {error.Message}. Failures: {_consecutiveFailures}.");
                break;
        }
        Updated?.Invoke(_state);
    }

    private async Task FetchAsync(string accessToken)
    {
        UsageResult result;
        try
        {
            result = await _client.FetchAsync(accessToken, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        switch (result)
        {
            case UsageResult.Success success:
                _consecutiveFailures = 0;
                _state.Snapshot = success.Snapshot;
                _state.LastSuccessAt = DateTimeOffset.Now;
                SetProblem(ProblemKind.None, null);
                var s = success.Snapshot;
                Log.Info(
                    $"Poll OK. 5h={Fmt(s.FiveHour)} 7d={Fmt(s.SevenDay)} " +
                    $"opus={Fmt(s.SevenDayOpus)} sonnet={Fmt(s.SevenDaySonnet)}");
                break;
            case UsageResult.Unauthorized:
                // On the bootstrap path the CLI may refresh its own token and
                // fix this without us; on our own session it means sign in again.
                SetProblem(ProblemKind.TokenExpired, _state.IsBootstrapped
                    ? "Claude Code's token was rejected - sign in to Claude"
                    : "Token rejected - sign in to Claude again");
                Log.Warn($"Poll rejected (401/403) on the {Describe(_state.Source)} token.");
                break;
            case UsageResult.RateLimited rateLimited:
                _consecutiveFailures++;
                _retryAfter = rateLimited.RetryAfter;
                SetProblem(ProblemKind.RateLimited, "Rate limited - backing off");
                Log.Warn($"Poll rate limited (429). Retry-after: " +
                         $"{(rateLimited.RetryAfter?.ToString() ?? "n/a")}. Failures: {_consecutiveFailures}.");
                break;
            case UsageResult.TransientError error:
                _consecutiveFailures++;
                SetProblem(ProblemKind.Network, error.Message);
                Log.Warn($"Poll transient error: {error.Message}. Failures: {_consecutiveFailures}.");
                break;
        }
    }

    private void SetProblem(ProblemKind kind, string? message)
    {
        _state.Problem = kind;
        _state.ProblemMessage = message;
    }

    private static string Fmt(UsageWindow? window) =>
        window is null ? "-" : $"{window.Utilization:0}%";

    private static string Describe(TokenSource? source) => source switch
    {
        TokenSource.EnvironmentVariable => $"{TokenProvider.EnvVarName} environment variable",
        TokenSource.OwnSession => "ClaudeCounter's own session",
        TokenSource.CliBootstrap => "Claude Code's session (bootstrap)",
        _ => "none",
    };

    private TimeSpan NextDelay()
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _getIntervalMinutes()));
        return _state.Problem switch
        {
            ProblemKind.None => interval,

            ProblemKind.RateLimited or ProblemKind.Network =>
                BackoffPolicy.NextDelay(_consecutiveFailures, _retryAfter),

            // Nothing improves until the user signs in, so back well off.
            ProblemKind.SignInRequired => Longest(interval, SignInRetry),

            // A rejected bootstrap token can fix itself when the CLI refreshes,
            // so keep checking; a rejected session of our own cannot.
            ProblemKind.TokenExpired => _state.IsBootstrapped
                ? Shortest(interval, BootstrapRetry)
                : Longest(interval, SignInRetry),

            _ => interval,
        };
    }

    private static TimeSpan Shortest(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Longest(TimeSpan a, TimeSpan b) => a > b ? a : b;

    public void Dispose()
    {
        _cts.Cancel();
        _wake.Cancel();
        _cts.Dispose();
    }
}
