using Xunit;

namespace ClaudeCounter.Tests;

/// <summary>
/// A fact about behaviour that only exists on Windows (drive letters, UNC
/// paths, the registry, files that cannot be deleted while open). Runs on
/// Windows, and is reported as skipped - with the reason - elsewhere, so the
/// Linux CI run neither fails on it nor silently drops it.
/// </summary>
public sealed class WindowsOnlyFactAttribute : FactAttribute
{
    public WindowsOnlyFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour.";
    }
}

/// <summary>The <see cref="WindowsOnlyFactAttribute"/> counterpart for theories.</summary>
public sealed class WindowsOnlyTheoryAttribute : TheoryAttribute
{
    public WindowsOnlyTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Windows-only behaviour.";
    }
}

/// <summary>
/// A theory about Unix path semantics (rooted "/..." paths, no drive letters),
/// the Linux counterpart of a <see cref="WindowsOnlyTheoryAttribute"/> case.
/// </summary>
public sealed class UnixOnlyTheoryAttribute : TheoryAttribute
{
    public UnixOnlyTheoryAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only behaviour.";
    }
}

/// <summary>A fact about Unix-only behaviour, such as file permission modes.</summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
            Skip = "Unix-only behaviour.";
    }
}
