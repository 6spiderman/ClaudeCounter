# Contributing to ClaudeCounter

Thanks for taking an interest. This is a small, deliberately plain codebase and
the conventions below are what keep it that way.

## Build and test

```sh
dotnet build ClaudeCounter.sln -c Release
dotnet test
```

Building a local single-file executable:

```sh
dotnet publish src/ClaudeCounter/ClaudeCounter.csproj \
  -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true \
  -o publish
```

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download). Warnings are
errors (`TreatWarningsAsErrors` in `Directory.Build.props`), so a build that is
noisy locally will fail CI.

## Project layout

```
src/ClaudeCounter/
  Core/        Polling, usage models, HTTP client, logging, versioning
  Core/Auth/   OAuth: PKCE, authorize URL, token endpoint, session store, resolution chain
  Settings/    AppSettings, JSON store, autostart manager
  UI/          Tray flyout, dialogs, icon rendering, theme
tests/ClaudeCounter.Tests/
```

## Conventions

**Never log a secret.** The auth code handles a PKCE verifier, an authorization
code, an access token and a refresh token. None of them may reach the log, an
exception message, a tooltip, or a UI label. If you need to log around them,
log the *outcome* ("refresh succeeded", "code rejected"), never the value.
There is a test that runs a full sign-in against a temp log and asserts none of
the secret values appear in it. Keep it passing.

**No NuGet unless it earns its place.** The app has exactly one runtime
dependency (`System.Security.Cryptography.ProtectedData`, for DPAPI). Forty
lines of semver parsing is not worth a package. If you think something is,
say why in the PR.

**Constructor injection over statics.** Every class that does I/O takes an
optional constructor parameter for its dependency (`HttpMessageHandler`,
`Func<DateTimeOffset> now`, a store interface) defaulting to the real thing.
That is the only reason the auth code is testable without a browser.

**WinForms layout is imperative, on purpose.** There are no `.Designer.cs`
files and no XAML. Forms build their controls in code, and the flyout rebuilds
its contents on every update. Follow the surrounding style rather than
introducing a layout framework.

**Result types over exceptions** for expected failures. Network problems, a
rejected token and a malformed file are all modelled as records in a result
union, not thrown. Reserve exceptions for genuine bugs.

## Tests

xUnit, no mocking library. Fakes are hand-rolled and live in
`tests/ClaudeCounter.Tests/Fakes/`. Temporary files go through
`Directory.CreateTempSubdirectory()`.

New behaviour needs a test. Bug fixes need a test that fails before the fix.

For anything that only shows up on a real machine - the installer, DPAPI, the
tray, SmartScreen - work through [docs/MANUAL-TESTING.md](docs/MANUAL-TESTING.md).
It is also the pre-release checklist.

## Pull requests

Small and focused beats large and comprehensive. Fill in the PR template
including the manual verification checklist - especially the credentials-file
hash check if you touched anything under `Core/Auth/`.

No CLA. By opening a PR you agree your contribution is licensed under the
project's [MIT license](LICENSE).

## Security

Do not open a public issue for a vulnerability. See [SECURITY.md](SECURITY.md).
