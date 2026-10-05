using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeCounter.Core.Auth;

/// <summary>
/// Stores the session in the desktop's Secret Service keyring (GNOME
/// Keyring, or KWallet via its Secret Service compatibility layer) by
/// shelling out to <c>secret-tool</c>, the same "known CLI over a bespoke
/// client" choice <see cref="ClaudeCounter.UI.Shell"/> makes for the browser
/// and file manager. A hand-rolled D-Bus client for the Secret Service API
/// (sessions, prompts, unlock signals) would be a lot of surface area for
/// something this infrequent.
/// </summary>
/// <remarks>
/// Falls back to <paramref name="fallback"/> - permanently, for the rest of
/// the process - the first time secret-tool turns out to be missing or a
/// call to it fails unexpectedly. A normal "no session stored yet" lookup
/// (exit code 1, empty output) is not a failure and does not trigger it.
/// </remarks>
public sealed class SecretServiceSessionStore(ISessionStore fallback) : ISessionStore
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly string[] Attributes = ["application", "ClaudeCounter", "account", "session"];

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private bool _useFallback;

    public OAuthSession? Read()
    {
        if (_useFallback)
            return fallback.Read();

        var result = Run(["lookup", .. Attributes], stdin: null);
        if (result is null)
        {
            FallBack("secret-tool is not available");
            return fallback.Read();
        }
        if (result.ExitCode != 0)
            return null; // nothing stored yet - a normal, expected outcome

        try
        {
            var session = JsonSerializer.Deserialize<OAuthSession>(result.StdOut, Json);
            if (session is null)
                return null;
            if (session.Version != OAuthSession.CurrentVersion)
            {
                Log.Warn($"Stored session in the keyring has version {session.Version}; " +
                         $"expected {OAuthSession.CurrentVersion}. Treating as signed out.");
                return null;
            }
            if (string.IsNullOrWhiteSpace(session.AccessToken) || string.IsNullOrWhiteSpace(session.RefreshToken))
                return null;
            return session;
        }
        catch (JsonException e)
        {
            Log.Warn($"Stored session in the keyring could not be parsed: {e.Message}");
            return null;
        }
    }

    public void Write(OAuthSession session)
    {
        if (_useFallback)
        {
            fallback.Write(session);
            return;
        }

        var json = JsonSerializer.Serialize(session, Json);
        var result = Run(["store", "--label=ClaudeCounter session", .. Attributes], stdin: json);
        if (result is not { ExitCode: 0 })
        {
            FallBack(result is null ? "secret-tool is not available" : "secret-tool store failed");
            fallback.Write(session);
        }
    }

    public void Clear()
    {
        if (_useFallback)
        {
            fallback.Clear();
            return;
        }
        Run(["clear", .. Attributes], stdin: null); // best-effort; a missing item is not an error
    }

    private void FallBack(string reason)
    {
        if (_useFallback)
            return;
        _useFallback = true;
        Log.Warn($"Falling back to encrypted-file session storage: {reason}");
    }

    private sealed record ProcessResult(int ExitCode, string StdOut);

    /// <returns>Null when secret-tool itself could not be run at all.</returns>
    private static ProcessResult? Run(string[] args, string? stdin)
    {
        try
        {
            var psi = new ProcessStartInfo("secret-tool")
            {
                UseShellExecute = false,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            if (stdin is not null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            // Read both streams in the background before waiting: reading
            // stdout to completion first can deadlock if stderr fills its
            // pipe buffer and the child blocks writing to it.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)Timeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                return null;
            }

            return new ProcessResult(process.ExitCode, stdoutTask.GetAwaiter().GetResult());
        }
        catch (Exception e) when (e is Win32Exception or IOException)
        {
            return null;
        }
    }
}
