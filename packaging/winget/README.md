# winget submission

These three files are templates for [microsoft/winget-pkgs][pkgs]. They are not
valid as written - every `<PLACEHOLDER>` has to be filled in per release.

In the winget-pkgs repository they live at:

```
manifests/6/6spiderman/ClaudeCounter/<VERSION>/
```

## Placeholders

| Placeholder | Where it comes from |
| --- | --- |
| `<VERSION>` | The release version, no leading `v` (e.g. `1.0.0`) |
| `<RELEASE_DATE>` | The release date, `YYYY-MM-DD` |
| `<SHA256_X64>` | From `SHA256SUMS.txt`, **uppercased** |
| `<SHA256_ARM64>` | From `SHA256SUMS.txt`, **uppercased** |

winget wants uppercase hashes; the release workflow writes `SHA256SUMS.txt` in
lowercase for the `Get-FileHash` comparison in the release notes. Convert them.

## The first submission

Do it by hand. Automating a process you have not watched once is how you find
out about the review bot the hard way.

```powershell
winget install wingetcreate
wingetcreate new https://github.com/6spiderman/ClaudeCounter/releases/download/v1.0.0/ClaudeCounter-1.0.0-x64-setup.exe
```

`wingetcreate` computes the hashes and opens the PR for you. Use these files as
the reference for what the answers should be - particularly `Scope: user`, the
Inno silent switches, and the `ProductCode`.

Before submitting, validate locally:

```powershell
winget validate --manifest <folder>
winget install --manifest <folder>     # requires local manifest support enabled
winget uninstall ClaudeCounter
```

Enable local manifests once with `winget settings --enable LocalManifestFiles`
from an elevated prompt.

**Expect the review to take a few days.** The installer is unsigned, which
trips a manual-review flag.

## Later releases

Once the package exists, automate it. Add a workflow triggered on
`release: published` using [winget-releaser][releaser]:

```yaml
- uses: vedantmgoyal9/winget-releaser@v2
  with:
    identifier: 6spiderman.ClaudeCounter
    installers-regex: '-setup\.exe$'
    token: ${{ secrets.WINGET_TOKEN }}
```

`WINGET_TOKEN` is a fine-grained PAT with **Contents: read and write** on your
fork of `winget-pkgs`. It cannot be `GITHUB_TOKEN` - that has no access to
another repository.

## Do not ship arm64 to winget yet

The arm64 installer is cross-published and has never been run on real hardware.
Drop the `arm64` entry from `installer.yaml` until someone confirms it works,
and keep offering it on the GitHub release in the meantime.

[pkgs]: https://github.com/microsoft/winget-pkgs
[releaser]: https://github.com/vedantmgoyal9/winget-releaser
