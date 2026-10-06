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

On Linux, the shared library and the Linux app (the WinForms app and the test
project target Windows, so they only build here with
`-p:EnableWindowsTargeting=true`, and the tests only run on Windows - CI runs
them there):

```sh
dotnet build src/ClaudeCounter.Linux/ClaudeCounter.Linux.csproj -c Release
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
tests/ClaudeCounter.Tests/ Tests for Core and the Windows front end
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
`tests/ClaudeCounter.Tests/Fakes/`. Temporary files go through
`Directory.CreateTempSubdirectory()`.

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
