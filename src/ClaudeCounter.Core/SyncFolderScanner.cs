using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ClaudeBackup;

/// <summary>
/// One detected sync-folder candidate for the sync-folder backup transport
/// (<see cref="DriveTarget.FolderPath"/>), after existence-checking and
/// de-duplication - see <see cref="SyncFolderScanner"/>. Never constructed
/// for a path that was not found to actually exist.
/// </summary>
public sealed record SyncFolderCandidate(string DisplayName, string Path);

/// <summary>
/// Probes the well-known folders a sync client (OneDrive, Google Drive,
/// Dropbox) or a mapped NAS share already uses on this machine, so the
/// Backup tab's "Detect..." button can offer them instead of making the user
/// find the path themselves - the biggest usability win of the sync-folder
/// transport (S14b design brief: Google Drive for Desktop, OneDrive, Dropbox
/// and a NAS share all present as ordinary folders needing zero
/// authentication, so finding them automatically is most of this feature's
/// value).
///
/// Two-phase, mirroring <see cref="ClaudeLocationScanner"/>: <see
/// cref="DetectFrom"/> is the pure, fully unit-testable core - every input
/// (environment variables, the user profile path, the drive roots to probe,
/// the mapped network drives, even the existence check itself) is passed in
/// rather than read directly - and <see cref="Detect"/> is the thin real-
/// machine entry point that reads the real environment, walks real drive
/// letters, and reads the real registry.
///
/// Never throws: a missing environment variable, an absent registry hive, or
/// an unreadable/disconnected drive must degrade to "found nothing", not
/// crash the Settings dialog. <see cref="Detect"/> wraps everything in one
/// outer try/catch on top of <see cref="DetectFrom"/>'s own per-candidate
/// tolerance, and <see cref="ReadMappedNetworkDrives"/> similarly degrades a
/// missing/unreadable "Network" registry key (no mapped drives at all - a
/// common, entirely normal case, not an error) to an empty list.
/// </summary>
public static class SyncFolderScanner
{
    /// <summary>
    /// Real entry point: reads the real OneDrive/OneDriveConsumer/
    /// OneDriveCommercial environment variables, %USERPROFILE%, every
    /// currently-visible drive letter (for a Google-Drive-for-Desktop-style
    /// "My Drive" mount), and HKCU\Network for mapped drives - then runs it
    /// all through <see cref="DetectFrom"/>. Never throws.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static IReadOnlyList<SyncFolderCandidate> Detect()
    {
        try
        {
            var envVars = new Dictionary<string, string?>
            {
                ["OneDrive"] = Environment.GetEnvironmentVariable("OneDrive"),
                ["OneDriveConsumer"] = Environment.GetEnvironmentVariable("OneDriveConsumer"),
                ["OneDriveCommercial"] = Environment.GetEnvironmentVariable("OneDriveCommercial"),
            };
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var driveRoots = SafeGetDriveRoots();
            var mappedDrives = ReadMappedNetworkDrives();

            return DetectFrom(envVars, userProfile, driveRoots, mappedDrives, Directory.Exists);
        }
        catch (Exception)
        {
            // Never throw out of detection (design brief) - degrade to
            // "found nothing", the same tolerance rule ClaudeLocationScanner
            // applies for its own real entry point.
            return Array.Empty<SyncFolderCandidate>();
        }
    }

    private static IReadOnlyList<string> SafeGetDriveRoots()
    {
        try
        {
            // DriveInfo.Name only reads the volume letter list, not the
            // drive's readiness/type - safe even for a disconnected or empty
            // removable drive. The actual "does 'My Drive' exist under it"
            // check happens later, per-candidate, in DetectFrom.
            return DriveInfo.GetDrives().Select(d => d.Name).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Reads HKCU\Network\&lt;letter&gt;\RemotePath for every currently
    /// mapped drive letter - the UNC path Windows itself already resolves a
    /// mapped drive to, with no P/Invoke or WMI needed. Deliberately
    /// tolerant: a missing "Network" key (nothing mapped) is the common case,
    /// not an error, and degrades to an empty list; one unreadable
    /// drive-letter subkey is skipped rather than aborting every other one -
    /// same tolerance rule as <see cref="ClaudeLocationScanner.ProbeCandidates"/>.
    /// Internal (not private) so it is directly unit-testable for "does not
    /// throw on this machine" without exposing it as part of the scanner's
    /// public surface - <see cref="Detect"/> is the public real entry point.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal static IReadOnlyList<(char Letter, string RemotePath)> ReadMappedNetworkDrives()
    {
        var result = new List<(char, string)>();
        try
        {
            using var networkKey = Registry.CurrentUser.OpenSubKey("Network");
            if (networkKey is null)
                return result;

            foreach (var letterName in networkKey.GetSubKeyNames())
            {
                if (letterName.Length != 1)
                    continue;
                try
                {
                    using var driveKey = networkKey.OpenSubKey(letterName);
                    if (driveKey?.GetValue("RemotePath") is string remotePath && !string.IsNullOrWhiteSpace(remotePath))
                        result.Add((char.ToUpperInvariant(letterName[0]), remotePath));
                }
                catch (Exception)
                {
                    // One bad drive-letter subkey must not take down the rest.
                }
            }
        }
        catch (Exception)
        {
            // No "Network" key, access denied, or (under a test host on a
            // non-Windows CI runner) registry access unsupported at all -
            // all degrade to "no mapped drives found".
        }
        return result;
    }

    /// <summary>
    /// Pure, fully unit-testable core. Builds the candidate list from
    /// already-resolved inputs, then de-duplicates by normalized full path
    /// (case-insensitive, trailing separator ignored - two different env
    /// vars, or an env var and a mapped-drive UNC, can legitimately resolve
    /// to the identical folder - see the design brief's OneDrive example)
    /// and filters to candidates <paramref name="directoryExists"/> reports
    /// as actually present. Only ever offers a path that exists - a
    /// candidate whose path cannot even be normalized (e.g. malformed), or
    /// whose existence check itself throws (e.g. an unreachable UNC share),
    /// is dropped rather than thrown out of the whole scan.
    /// </summary>
    /// <param name="environmentVariables">
    /// OneDrive / OneDriveConsumer / OneDriveCommercial values, keyed by
    /// variable name - a key that is absent, null, or blank is simply not
    /// offered as a candidate.
    /// </param>
    /// <param name="userProfilePath">%USERPROFILE% - blank skips the fixed Google Drive and Dropbox candidates rather than probing a relative path.</param>
    /// <param name="driveRoots">Every drive root to probe for a Google-Drive-for-Desktop-style "&lt;root&gt;\My Drive" mount, e.g. "C:\", "G:\".</param>
    /// <param name="mappedNetworkDrives">Every mapped drive letter and the UNC path it resolves to (see <see cref="ReadMappedNetworkDrives"/>).</param>
    /// <param name="directoryExists">
    /// The existence check itself, injected so this method never touches a
    /// real disk - real callers pass <see cref="Directory.Exists(string?)"/>.
    /// </param>
    public static IReadOnlyList<SyncFolderCandidate> DetectFrom(
        IReadOnlyDictionary<string, string?> environmentVariables,
        string userProfilePath,
        IReadOnlyList<string> driveRoots,
        IReadOnlyList<(char Letter, string RemotePath)> mappedNetworkDrives,
        Func<string, bool> directoryExists)
    {
        var candidates = new List<SyncFolderCandidate>();

        // OneDrive - all three env vars are equally valid candidates; order
        // here only affects which DisplayName wins when two resolve to the
        // identical path (de-duplicated below), not correctness.
        AddIfSet(candidates, "OneDrive - Work/School", environmentVariables, "OneDriveCommercial");
        AddIfSet(candidates, "OneDrive - Personal", environmentVariables, "OneDriveConsumer");
        AddIfSet(candidates, "OneDrive", environmentVariables, "OneDrive");

        // Google Drive - the fixed %USERPROFILE%\Google Drive location, plus
        // Drive for Desktop's virtual-drive-letter mount (a "My Drive" folder
        // at the root of any drive letter - unverified on the reference
        // machine, so this is probed defensively rather than assumed).
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            candidates.Add(new SyncFolderCandidate("Google Drive", System.IO.Path.Combine(userProfilePath, "Google Drive")));
        foreach (var root in driveRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var label = $"Google Drive ({root.TrimEnd('\\', '/')})";
            candidates.Add(new SyncFolderCandidate(label, System.IO.Path.Combine(root, "My Drive")));
        }

        // Dropbox
        if (!string.IsNullOrWhiteSpace(userProfilePath))
            candidates.Add(new SyncFolderCandidate("Dropbox", System.IO.Path.Combine(userProfilePath, "Dropbox")));

        // Mapped network drives (NAS) - offered as the UNC path, NEVER the
        // drive letter. A drive-letter mapping is per-interactive-session and
        // is not guaranteed to resolve inside a Task Scheduler run, so a
        // backup configured as "M:\..." can silently fail once scheduled
        // while the UNC form ("\\192.168.1.210\media\...") keeps working -
        // this is the single most important behaviour of this method.
        foreach (var (letter, remotePath) in mappedNetworkDrives)
        {
            if (string.IsNullOrWhiteSpace(remotePath))
                continue;
            candidates.Add(new SyncFolderCandidate($"NAS share ({letter}: -> {remotePath})", remotePath));
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<SyncFolderCandidate>();
        foreach (var candidate in candidates)
        {
            string normalized;
            bool exists;
            try
            {
                normalized = System.IO.Path.GetFullPath(candidate.Path)
                    .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
                exists = directoryExists(candidate.Path);
            }
            catch (Exception)
            {
                // Malformed path, or the existence check itself threw (e.g.
                // an unreachable UNC share) - treat as absent, same
                // tolerance as ClaudeLocationScanner.ProbeCandidates.
                continue;
            }

            if (!exists)
                continue;
            if (!seenPaths.Add(normalized))
                continue;

            found.Add(candidate);
        }
        return found;
    }

    /// <summary>
    /// Narrows a candidate list (normally the result of <see cref="Detect"/>)
    /// down to the ones that plausibly belong to <paramref name="provider"/> -
    /// the Backup tab's named-destination selector (S16) uses this so
    /// picking "OneDrive" only ever offers OneDrive-shaped candidates, not
    /// every sync folder found on the machine. Matches on <see
    /// cref="SyncFolderCandidate.DisplayName"/>'s prefix, which <see
    /// cref="DetectFrom"/> above already sets to one of a small fixed set
    /// ("OneDrive...", "Google Drive...", "Dropbox", "NAS share (...)") for
    /// every candidate it can produce. <see cref="SyncProvider.Other"/>
    /// matches nothing - there is no "Other" entry in the named-destination
    /// selector to filter for.
    /// </summary>
    public static IReadOnlyList<SyncFolderCandidate> FilterByProvider(
        IReadOnlyList<SyncFolderCandidate> candidates, SyncProvider provider)
    {
        string? prefix = provider switch
        {
            SyncProvider.OneDrive => "OneDrive",
            SyncProvider.GoogleDrive => "Google Drive",
            SyncProvider.Dropbox => "Dropbox",
            SyncProvider.Nas => "NAS share",
            _ => null,
        };
        if (prefix is null)
            return Array.Empty<SyncFolderCandidate>();

        return candidates.Where(c => c.DisplayName.StartsWith(prefix, StringComparison.Ordinal)).ToList();
    }

    /// <summary>
    /// Linux counterpart of <see cref="Detect"/>: probes the folders Linux sync
    /// clients create by default, and mounted network shares. Never throws.
    /// </summary>
    public static IReadOnlyList<SyncFolderCandidate> DetectLinux()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var mounts = ReadLinuxMounts("/proc/self/mounts");
            var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            var gvfsDir = string.IsNullOrWhiteSpace(runtimeDir) ? null : System.IO.Path.Combine(runtimeDir, "gvfs");
            return DetectLinuxFrom(home, mounts, gvfsDir, ListDirectories, Directory.Exists);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<SyncFolderCandidate>();
        }
    }

    private static readonly HashSet<string> NetworkFileSystems = new(StringComparer.Ordinal)
    {
        "cifs", "smb3", "smbfs", "nfs", "nfs4", "fuse.sshfs", "fuse.rclone",
    };

    /// <summary>
    /// The pure core of <see cref="DetectLinux"/>. Display names start with the
    /// same prefixes as the Windows candidates ("OneDrive", "Google Drive",
    /// "Dropbox", "NAS share") so <see cref="FilterByProvider"/> works
    /// unchanged on both platforms.
    /// </summary>
    /// <param name="home">The user's home folder; blank skips the per-client folders.</param>
    /// <param name="mounts">Mounted file systems as (source, mount point, type), e.g. from /proc/self/mounts.</param>
    /// <param name="gvfsDirectory">The GVFS mount directory ($XDG_RUNTIME_DIR/gvfs), where KDE/GNOME file managers mount network shares; null to skip.</param>
    /// <param name="listDirectories">Names of the subdirectories of a folder (empty if it cannot be read).</param>
    /// <param name="directoryExists">Whether a folder exists.</param>
    public static IReadOnlyList<SyncFolderCandidate> DetectLinuxFrom(
        string home,
        IReadOnlyList<(string Source, string MountPoint, string FileSystem)> mounts,
        string? gvfsDirectory,
        Func<string, IReadOnlyList<string>> listDirectories,
        Func<string, bool> directoryExists)
    {
        var candidates = new List<SyncFolderCandidate>();

        if (!string.IsNullOrWhiteSpace(home))
        {
            var homeEntries = listDirectories(home);

            // OneDrive (abraunegg/onedrive and OneDriveGUI default to ~/OneDrive;
            // business accounts are often "OneDrive - <Org>").
            foreach (var name in homeEntries.Where(n => n.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                // The folder name already starts with "OneDrive", so it is the label.
                candidates.Add(new SyncFolderCandidate(name, System.IO.Path.Combine(home, name)));

            // Google Drive has no official Linux client: these are the names
            // the common ones use (google-drive-ocamlfuse, rclone mounts set
            // up by hand, Insync's per-account folders).
            foreach (var name in new[] { "Google Drive", "GoogleDrive", "google-drive", "gdrive" })
                candidates.Add(new SyncFolderCandidate($"Google Drive (~/{name})", System.IO.Path.Combine(home, name)));
            var insync = System.IO.Path.Combine(home, "Insync");
            if (directoryExists(insync))
            {
                foreach (var account in listDirectories(insync).Order(StringComparer.Ordinal))
                    candidates.Add(new SyncFolderCandidate($"Google Drive (Insync: {account})", System.IO.Path.Combine(insync, account, "Google Drive")));
            }

            // Dropbox's own Linux client: ~/Dropbox, or "Dropbox (Personal)" /
            // "Dropbox (<Team>)" when a work account is linked too.
            foreach (var name in homeEntries.Where(n => n.StartsWith("Dropbox", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                candidates.Add(new SyncFolderCandidate(name, System.IO.Path.Combine(home, name)));
        }

        // NAS: network file systems mounted through fstab, autofs or by hand.
        foreach (var (source, mountPoint, fileSystem) in mounts)
        {
            if (NetworkFileSystems.Contains(fileSystem) && !string.IsNullOrWhiteSpace(mountPoint))
                candidates.Add(new SyncFolderCandidate($"NAS share ({source})", mountPoint));
        }

        // NAS: shares opened in Dolphin/Nautilus, mounted by GVFS for the
        // current session ("smb-share:server=nas,share=media").
        if (gvfsDirectory is not null && directoryExists(gvfsDirectory))
        {
            foreach (var name in listDirectories(gvfsDirectory).Order(StringComparer.Ordinal))
            {
                if (name.StartsWith("smb-share:", StringComparison.Ordinal) ||
                    name.StartsWith("nfs:", StringComparison.Ordinal) ||
                    name.StartsWith("sftp:", StringComparison.Ordinal))
                    candidates.Add(new SyncFolderCandidate($"NAS share ({name})", System.IO.Path.Combine(gvfsDirectory, name)));
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var found = new List<SyncFolderCandidate>();
        foreach (var candidate in candidates)
        {
            bool exists;
            try
            {
                exists = directoryExists(candidate.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                exists = false;
            }
            if (exists && seen.Add(System.IO.Path.TrimEndingDirectorySeparator(candidate.Path)))
                found.Add(candidate);
        }
        return found;
    }

    /// <summary>
    /// Parses a mounts table (/proc/self/mounts format: source, mount point,
    /// type, ... separated by spaces, with spaces inside fields written as
    /// \040). Missing or unreadable means no mounts.
    /// </summary>
    public static IReadOnlyList<(string Source, string MountPoint, string FileSystem)> ParseLinuxMounts(IEnumerable<string> lines)
    {
        var result = new List<(string, string, string)>();
        foreach (var line in lines)
        {
            var fields = line.Split(' ');
            if (fields.Length < 3)
                continue;
            result.Add((Unescape(fields[0]), Unescape(fields[1]), fields[2]));
        }
        return result;

        static string Unescape(string field) => System.Text.RegularExpressions.Regex.Replace(
            field, @"\\([0-7]{3})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());
    }

    private static IReadOnlyList<(string Source, string MountPoint, string FileSystem)> ReadLinuxMounts(string path)
    {
        try
        {
            return File.Exists(path) ? ParseLinuxMounts(File.ReadLines(path)) : Array.Empty<(string, string, string)>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<(string, string, string)>();
        }
    }

    private static IReadOnlyList<string> ListDirectories(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? Directory.EnumerateDirectories(folder).Select(d => System.IO.Path.GetFileName(d)).ToList()
                : Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static void AddIfSet(
        List<SyncFolderCandidate> candidates, string displayName,
        IReadOnlyDictionary<string, string?> environmentVariables, string key)
    {
        if (environmentVariables.TryGetValue(key, out var path) && !string.IsNullOrWhiteSpace(path))
            candidates.Add(new SyncFolderCandidate(displayName, path!));
    }
}
