# Contributing to ClaudeCounter

Thanks for taking an interest. This is a small, deliberately plain codebase and
the conventions below are what keep it that way.

## Build and test

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) (or 9.0.300
or later) plus the .NET 8 runtime. Everything targets .NET 8, but Avalonia 12's
source generator needs a newer C# compiler than any .NET 8 SDK ships - with
only the 8.0 SDK the Linux project fails with `CS9057`. Warnings are errors
(`TreatWarningsAsErrors` in `Directory.Build.props`), so a build that is noisy
locally will fail CI.

On Windows, the whole solution:

```sh
dotnet build ClaudeCounter.sln -c Release
dotnet test
```

On Linux, the Linux app and the platform-neutral tests (the WinForms app and
its test project target Windows, so they only build here with
`-p:EnableWindowsTargeting=true`, and their tests only run on Windows):

```sh
dotnet build src/ClaudeCounter.Linux/ClaudeCounter.Linux.csproj -c Release
dotnet test tests/ClaudeCounter.Core.Tests
```

Building a local single-file executable:

```sh
# Windows
dotnet publish src/ClaudeCounter/ClaudeCounter.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish

# Linux
dotnet publish src/ClaudeCounter.Linux/ClaudeCounter.Linux.csproj \
  -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -o publish
```

### The KDE Plasma widget

The widget in `packaging/plasmoid/com.github.6spiderman.claudecounter/` is
plain QML with no build step. It only runs `claudecounter --status --json`
(and `--refresh`), so any change to that JSON is a change to the widget's
input: keep the field names stable, and cover them in `UsageStatusTests`. To
try a change on Plasma 6:

```sh
kpackagetool6 -t Plasma/Applet -i packaging/plasmoid/com.github.6spiderman.claudecounter   # first time
kpackagetool6 -t Plasma/Applet -u packaging/plasmoid/com.github.6spiderman.claudecounter   # after each edit
plasmawindowed com.github.6spiderman.claudecounter                                        # popup in a window
kpackagetool6 -t Plasma/Applet -r com.github.6spiderman.claudecounter                      # remove it again
```

QML errors go to the terminal `plasmawindowed` was started from.
`plasmawindowed` remembers the window size in `~/.config/plasmawindowedrc`;
delete that to see the popup's own size again. It runs whichever
`claudecounter` is first on your `PATH`, so to test against a local build, put
a `claudecounter` script that execs your build ahead of `/usr/bin` on `PATH`.
To see the panel badge instead of the popup, add
`preferredRepresentation: compactRepresentation` to `main.qml` temporarily.

CI restores in locked mode, so `packages.lock.json` must match what a plain
`dotnet restore` produces. Note that a `dotnet publish -r <rid>` without
`--no-restore` rewrites the lock files (it adds `Microsoft.NET.ILLink.Tasks`
and a RID section) - do not commit that.

## Project layout

```
src/ClaudeCounter.Core/    Shared, platform-neutral (plain net8.0): polling, usage
                           models, HTTP client, logging, versioning, settings model
  Auth/                    OAuth: PKCE, authorize URL, token endpoint, session
                           store, resolution chain
src/ClaudeCounter/         Windows front end (WinForms)
  Auth/                    DPAPI data protector
  Settings/                Run-key autostart manager
  UI/                      Tray flyout, dialogs, icon rendering, theme
src/ClaudeCounter.Linux/   Linux front end (Avalonia)
  Core/Auth/               Secret Service store, machine-key data protector
  Settings/                XDG autostart manager
  UI/                      Tray flyout, dialogs, icon rendering, tray-presence check
tests/ClaudeCounter.Core.Tests/  Tests for Core and the backup worker (net8.0;
                                runs on Windows and Linux)
tests/ClaudeCounter.Tests/       Tests that need WinForms or Windows APIs
                                (net8.0-windows; runs on Windows only)
packaging/                 Inno Setup + winget (Windows), nfpm .deb/.rpm (Linux),
                           startup smoke tests for both
```

Anything that is not UI and not tied to one OS belongs in
`ClaudeCounter.Core`. It must stay free of Windows- or Linux-only calls; CI
builds it on Linux on its own to catch that.

## Conventions

**Never log a secret.** The auth code handles a PKCE verifier, an authorization
code, an access token and a refresh token. None of them may reach the log, an
exception message, a tooltip, or a UI label. If you need to log around them,
log the *outcome* ("refresh succeeded", "code rejected"), never the value.
There is a test that runs a full sign-in against a temp log and asserts none of
the secret values appear in it. Keep it passing.

**No NuGet unless it earns its place.** The Windows app has exactly one
runtime dependency (`System.Security.Cryptography.ProtectedData`, for DPAPI).
The Linux app has Avalonia (the UI toolkit) and `Tmds.DBus.Protocol`, the
D-Bus client Avalonia already ships, referenced directly for the logind
sleep/resume signal. Forty lines of semver parsing is not worth a package. If
you think something is, say why in the PR.

**Constructor injection over statics.** Every class that does I/O takes an
optional constructor parameter for its dependency (`HttpMessageHandler`,
`Func<DateTimeOffset> now`, a store interface) defaulting to the real thing.
That is the only reason the auth code is testable without a browser.

**UI layout is imperative, on purpose.** There are no `.Designer.cs` files
and no XAML, on either platform. Windows (WinForms) and Avalonia windows build
their controls in code, and the flyout rebuilds its contents on every update.
Follow the surrounding style rather than introducing a layout framework.

**Linux windows must never block logout.** Avalonia cancels the desktop's
logout if any window refuses to close during it. A window that only hides on
close (like the flyout) must still let `WindowCloseReason.OSShutdown` and
`ApplicationShutdown` through.

**Result types over exceptions** for expected failures. Network problems, a
rejected token and a malformed file are all modelled as records in a result
union, not thrown. Reserve exceptions for genuine bugs.

## Tests

xUnit, no mocking library. Fakes are hand-rolled and live in
`tests/ClaudeCounter.Core.Tests/Fakes/`. Temporary files go through
`Directory.CreateTempSubdirectory()`.

There are two test projects. **`tests/ClaudeCounter.Core.Tests`** is plain
`net8.0` and holds everything that tests the shared library and the backup
worker; CI runs it on both Windows and Linux. **`tests/ClaudeCounter.Tests`**
targets Windows and holds only what needs WinForms or Windows APIs (forms,
the Windows icon renderer, DPAPI, Task Scheduler). Put a new test in
`ClaudeCounter.Core.Tests` unless it cannot compile without Windows.

Tests there must pass on both systems: build paths with `Path.Combine`
(never `C:\` or `/home` literals as real paths), and do not rely on Windows
file locking. When the behaviour itself only exists on one system (drive
letters and UNC paths, the registry, files that cannot be deleted while
open), mark the case `[WindowsOnlyFact]`/`[WindowsOnlyTheory]` - or
`[UnixOnlyFact]`/`[UnixOnlyTheory]` for Linux-only behaviour - so the other system reports it
as skipped instead of failing (see `WindowsOnly.cs`).

New behaviour needs a test. Bug fixes need a test that fails before the fix.

For anything that only shows up on a real machine - the installer, DPAPI, the
tray, SmartScreen, and on Linux the keyring, autostart and logout - work
through [docs/MANUAL-TESTING.md](docs/MANUAL-TESTING.md).
It is also the pre-release checklist.

## Pull requests

Small and focused beats large and comprehensive. Fill in the PR template
including the manual verification checklist - especially the credentials-file
hash check if you touched anything under `Core/Auth/`.

No CLA. By opening a PR you agree your contribution is licensed under the
project's [MIT license](LICENSE).

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).

## A note on packages.lock.json

CI restores with `dotnet restore ClaudeCounter.sln --locked-mode`, which fails the
build outright if a committed lock file does not match what the projects actually
reference.

A local self-contained publish can rewrite these files - it has previously added a
`Microsoft.NET.ILLink.Tasks` entry that no project references, which made
`--locked-mode` fail with NU1004 on three projects while a plain `dotnet restore`
kept "fixing" the working tree so the problem was invisible locally.

So: if `git status` shows a `packages.lock.json` change you did not intend, do not
commit it. Regenerate with a plain `dotnet restore ClaudeCounter.sln`, then prove
the CI path from a clean tree:

```
git status --short          # must be empty
dotnet restore ClaudeCounter.sln --locked-mode
```

Verifying after a plain restore rather than from the committed state is what let
this reach the branch in the first place.
