using ClaudeBackup;
using ClaudeCounter.Core;

namespace ClaudeCounter.Notifications;

/// <summary>
/// Pure mapping from a <see cref="BackupHealthResult"/> (ClaudeCounter.Core's
/// BackupHealth.Evaluate output) to what each tray surface should show -
/// extracted out of TrayApplicationContext/FlyoutForm/AlertPopupForm
/// specifically so it is unit-testable with no Form, no Icon, and no disk I/O
/// (see docs/superpowers/specs/2026-08-11-backup-health.md's Testing section:
/// "Health-to-display mapping ... must be pure and unit-tested - extract it
/// rather than embedding it in the form").
///
/// Every method here keys off <see
/// cref="BackupHealthStateExtensions.WarrantsAttention"/> - the single source
/// of truth for "is this a problem" - rather than re-deriving a Failed-or-
/// Stale check of its own. That is the one rule this whole task exists to
/// enforce: an earlier draft of the design spec hand-rolled exactly that
/// check and would have left a backup that never runs at all (NeverRun)
/// unbadged forever - see WarrantsAttention's own doc comment.
/// </summary>
public static class BackupHealthPresenter
{
    /// <summary>
    /// The tooltip's one-line backup summary, or null when there is nothing
    /// to add - either the worker is not installed (<paramref name="result"/>
    /// is null, per BackupTaskManager.WorkerAvailable()) or the state does
    /// not warrant attention (Healthy or NotConfigured). Deliberately terse
    /// ("Backup: failed", not the destination or message) - the tooltip has a
    /// hard 127-character budget shared with the usage lines (see
    /// TrayApplicationContext.MaxTooltipLength) and is already close to it;
    /// the caller appends this line LAST, after the guaranteed Session/Week
    /// lines, so if the budget clamp has to cut anything, it cuts this line
    /// rather than a usage number.
    ///
    /// S17b: this stays a single state-label line - never a per-destination
    /// join - specifically so it stays O(1) in destination count and can
    /// never itself threaten the 127-char budget no matter how many
    /// destinations are configured; see TooltipLineStaysShort in
    /// BackupHealthPresenterTests for the pin. Contrast <see
    /// cref="PopupContent"/>, which DOES join per-destination detail and
    /// does need an explicit cap for N destinations.
    /// </summary>
    public static string? TooltipLine(BackupHealthResult? result) =>
        result is null || !result.State.WarrantsAttention() ? null : $"Backup: {StateLabel(result.State)}";

    /// <summary>
    /// The flyout's backup-health lines - empty when the worker is not
    /// installed or the state is NotConfigured (never nag a user who does not
    /// use backup). Otherwise always a one-line health summary, plus one
    /// destination-detail line per destination that is not Healthy when the
    /// overall state warrants attention (naming which destination and when it
    /// last succeeded, or that it never has, for NeverRun).
    /// </summary>
    public static IReadOnlyList<string> FlyoutLines(BackupHealthResult? result, DateTimeOffset now)
    {
        if (result is null || result.State == BackupHealthState.NotConfigured)
            return Array.Empty<string>();

        var lines = new List<string> { $"Backup: {StateLabel(result.State)}" };
        if (result.State.WarrantsAttention())
        {
            foreach (var d in result.Destinations.Where(d => d.State != DestinationHealthState.Healthy))
                lines.Add($"{d.Name}: {DestinationDetail(d, now)}");
        }
        return lines;
    }

    /// <summary>
    /// Upper bound on how many unhealthy destinations <see
    /// cref="PopupContent"/> names in its body (S17b). Two hardcoded
    /// destinations could never produce more than two clauses in the "; "
    /// join below, so this never mattered before; with N destinations the
    /// popup - a small, fixed-width, chrome-less card meant to be read at a
    /// glance near the tray, not a scrollable list (see AlertPopupForm's
    /// body Label: AutoSize with MaximumSize height 0, i.e. unbounded - it
    /// grows to fit whatever text it is given) - would otherwise grow one
    /// line per broken destination with no cap at all. Capped at 3: enough
    /// to name the common case (one or two failures) in full, with the
    /// flyout (<see cref="FlyoutLines"/>, always complete, no cap) as the
    /// place to see every unhealthy destination when there are more than
    /// this.
    /// </summary>
    private const int MaxPopupDestinations = 3;

    /// <summary>
    /// Title/body for the one-per-transition popup (see <see
    /// cref="ShouldNotify"/> - the caller only invokes this on an actual
    /// transition, never on every poll).
    /// </summary>
    public static (string Title, string Body) PopupContent(BackupHealthResult result, DateTimeOffset now)
    {
        var title = result.State switch
        {
            BackupHealthState.Failed => "Backup failed",
            BackupHealthState.Stale => "Backup is stale",
            BackupHealthState.NeverRun => "Backup has never run",
            _ => "Backup problem",
        };

        var broken = result.Destinations.Where(d => d.State != DestinationHealthState.Healthy).ToList();
        string body;
        if (broken.Count == 0)
        {
            body = $"Backup is {StateLabel(result.State)}.";
        }
        else
        {
            // S17b: cap the join at MaxPopupDestinations - see that
            // constant's own doc comment - and summarize whatever is left
            // rather than silently dropping it, so a user with many
            // destinations still knows there is more to see (in the flyout)
            // instead of assuming the popup already named everything wrong.
            var shown = string.Join("; ", broken.Take(MaxPopupDestinations).Select(d => $"{d.Name}: {DestinationDetail(d, now)}"));
            var remaining = broken.Count - MaxPopupDestinations;
            body = remaining > 0 ? $"{shown}; and {remaining} more - see the tray for details." : $"{shown}.";
        }
        return (title, body);
    }

    /// <summary>
    /// True exactly on a transition from a state that does not warrant
    /// attention into one that does - repeated polls in the same state must
    /// not re-notify, and recovery to Healthy (or NotConfigured) must not
    /// notify at all (see the design spec's "Popup" section). <paramref
    /// name="previous"/> is null when there is no persisted dedupe state yet
    /// (a fresh install, or one that has never observed a backup poll
    /// before) - that counts as "coming from a quiet state", so a first-ever
    /// poll that lands on NeverRun/Failed/Stale still notifies once, matching
    /// the spec's explicit "NotConfigured/first-ever poll -> NeverRun"
    /// example.
    /// </summary>
    public static bool ShouldNotify(BackupHealthState? previous, BackupHealthState current)
    {
        var wasQuiet = previous is null || !previous.Value.WarrantsAttention();
        return wasQuiet && current.WarrantsAttention();
    }

    private static string StateLabel(BackupHealthState state) => state switch
    {
        BackupHealthState.Healthy => "healthy",
        BackupHealthState.NeverRun => "never run",
        BackupHealthState.Failed => "failed",
        BackupHealthState.Stale => "stale",
        BackupHealthState.NotConfigured => "not configured",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };

    private static string DestinationDetail(DestinationHealth d, DateTimeOffset now) =>
        d.LastSuccessUtc is { } last ? $"last succeeded {TimeText.Ago(last, now)}" : "never succeeded";
}
