using ClaudeBackup;

namespace ClaudeCounter.UI;

/// <summary>
/// Which root RestoreDialog's classify/apply steps operate against (S10:
/// restore to a different folder). Live is the default and the only option
/// that existed before this feature; Custom lets the user redirect both
/// steps at once, via <see cref="RestoreDestinationModel.ResolveDestinationRoot"/>
/// - the single place that value is computed, so classify and apply can
/// never be called with two different roots for what is supposed to be one
/// restore.
/// </summary>
public enum RestoreDestinationKind { Live, Custom }

/// <summary>
/// Pure destination-resolution and refusal logic for RestoreDialog's S10
/// "restore to a different folder" feature. No WinForms dependency, so this
/// is unit-tested directly rather than only through a Form - mirrors
/// RestoreDisplayModel's own reasoning for staying pure and testable.
///
/// Two responsibilities, both small and both safety-critical enough to pull
/// out of RestoreDialog rather than inline in an event handler:
///   - <see cref="ResolveDestinationRoot"/>: what "the destination" actually
///     resolves to right now, given the user's choice. RestoreDialog calls
///     this exactly once per preview (freezing the result for that
///     preview/apply cycle - see its own comment on why), so a preview can
///     never silently disagree with what apply then does.
///   - <see cref="IsRefusedDestination"/>: whether a candidate folder would
///     corrupt the restore operation itself if chosen. Built on <see
///     cref="RelativePathGuard.Overlaps"/> rather than a new path
///     comparison - that method is already separator-aware (a sibling
///     directory whose name happens to share a prefix, e.g.
///     "C:\staging-evil" against "C:\staging", must not be treated as
///     contained) and already covers both directions (candidate inside a
///     protected directory, or a protected directory inside candidate).
/// </summary>
public static class RestoreDestinationModel
{
    /// <summary>
    /// Resolves the effective destination root. <see
    /// cref="RestoreDestinationKind.Live"/> always resolves to <paramref
    /// name="liveRoot"/> regardless of <paramref name="customRoot"/> - a
    /// customRoot left over from a previous Custom selection must never leak
    /// through once the user has switched back to Live. <see
    /// cref="RestoreDestinationKind.Custom"/> resolves to <paramref
    /// name="customRoot"/> only when it is actually set: null/whitespace
    /// means "Custom is selected but no folder has been successfully picked
    /// yet" (mid-browse, or every attempt so far was cancelled or refused),
    /// which this returns as null rather than silently falling back to
    /// Live. Falling back would let a Preview click restore into live
    /// config while the dialog's own destination control still reads
    /// "Another folder..." - exactly the confusion this feature exists to
    /// prevent. Callers are expected to keep Preview disabled whenever this
    /// returns null.
    /// </summary>
    public static string? ResolveDestinationRoot(RestoreDestinationKind kind, string liveRoot, string? customRoot) =>
        kind == RestoreDestinationKind.Live
            ? liveRoot
            : (string.IsNullOrWhiteSpace(customRoot) ? null : customRoot);

    /// <summary>
    /// True when <paramref name="candidate"/> is, contains, or is contained
    /// by any of <paramref name="protectedScratchDirs"/> - in practice,
    /// RestoreDialog's own materialised-staging folder, the GitHub clone
    /// directory, the Drive temp-download directory, and the safety-copy
    /// base directory. Any of these overlapping the chosen destination would
    /// corrupt the restore operation itself: applying into the very folder
    /// Apply is reading staged files FROM, or into the folder Apply is about
    /// to write safety-copy originals into, is not a "wrong destination"
    /// mistake the engine's six safety rules already cover - it is a
    /// structural conflict between two roles the same directory cannot
    /// simultaneously play. Refused here, up front at selection time, rather
    /// than left for Materialize's own protectedRoot check, which only ever
    /// compares against the live config root and is deliberately unchanged
    /// by this feature (see RestoreDialog's own comment on why).
    /// </summary>
    public static bool IsRefusedDestination(string candidate, IReadOnlyList<string> protectedScratchDirs)
    {
        foreach (var dir in protectedScratchDirs)
        {
            if (RelativePathGuard.Overlaps(candidate, dir))
                return true;
        }
        return false;
    }
}
