# Drive backup setup

ClaudeCounter's Backup tab names each destination directly - **Settings** ->
**Backup** -> **Back up to**:

- **Google Drive**, **OneDrive**, **Dropbox**, or **NAS / network share** (all
  sync folder, recommended) - point ClaudeCounter at a folder your sync client
  already watches, or a NAS share. No sign-in, no API setup, no external
  binary. The sync client - or the NAS itself - does the actual upload;
  ClaudeCounter just writes a zip into an ordinary folder. Picking one of
  these already tries to find and fill in the folder for you (see
  [Detect...](#detect) below).
- **rclone remote (advanced)** - the original method: ClaudeCounter shells out to
  [rclone](https://rclone.org), which you authorise once against your own Google
  OAuth client. More setup, but works with any rclone-supported remote, not just
  a folder a sync client already manages.

They are **not** combined - underneath, this is still one Drive destination
that uses one transport at a time, so you cannot back up to a NAS (sync
folder) and an rclone remote simultaneously; picking a different cloud/NAS
option replaces whichever one was configured before. GitHub plus one of them
still works, since GitHub is a separate destination.

This doc covers the sync-folder method first (start here unless you already know
you need rclone), then OneDrive/Dropbox/NAS specifics, then the rclone method as
an appendix.

---

## Sync folder (recommended)

### Why no authentication is needed

Google Drive for Desktop, OneDrive, and Dropbox all work the same way under the
hood: each runs a background process that watches a folder on your local disk and
uploads anything written into it. From ClaudeCounter's point of view, that folder
is just an ordinary folder - `File.Copy`, nothing more. The sync client owns the
upload, the authentication, and the retry logic; ClaudeCounter never talks to
Google, Microsoft, or Dropbox at all for this transport.

A NAS share works the same way in spirit: point ClaudeCounter at the share and
the NAS receives the file over the network the moment it is written, no
credential handling on ClaudeCounter's side either.

### Point ClaudeCounter at a folder

**Settings** -> **Backup** -> **Back up to**:

1. Pick **Google Drive**, **OneDrive**, **Dropbox**, or **NAS / network share** -
   ClaudeCounter immediately tries to auto-detect and fill in the folder path for
   this destination.
2. Tick **Back up to this destination**.
3. **Sync folder path**: already filled in if detection found exactly one match.
   Left blank if it found none (never invents a path - use Detect... or Browse...
   instead). If it found more than one match, a picker opened for you to choose.
4. Choose what to back up with **Choose files...** - this destination has its
   own selection, independent of GitHub.
5. **Back up now** to confirm it works. On success you get a "Backup complete"
   message; check the log via **Open log folder** if anything looks wrong.

### Detect...

Picking a destination above already runs detection once automatically. The
**Detect...** button re-runs it on demand - useful after signing into a sync
client, or plugging in a NAS, that was not ready yet when Settings was first
opened. Either way it probes this machine for sync-client folders and mapped
drives, scoped to whichever destination is currently selected, and lets you
pick from whatever it finds:

- **OneDrive**: the `OneDrive`, `OneDriveConsumer`, and `OneDriveCommercial`
  environment variables (Windows sets whichever apply - a work/school account,
  a personal account, or both). Duplicate paths are merged into one entry.
- **Google Drive**: `%USERPROFILE%\Google Drive`, plus a `My Drive` folder at the
  root of any drive letter (how Google Drive for Desktop's virtual drive mount
  shows up).
- **Dropbox**: `%USERPROFILE%\Dropbox`.
- **Mapped network drives (NAS)**: every currently mapped drive letter, resolved
  to its **UNC path** - see the next section for why the UNC form, not the
  drive letter, is what gets offered.

Only folders that actually exist on this machine are offered. Finding nothing
is not an error - Browse... or typing a path both still work.

### NAS: prefer a UNC path over a mapped drive letter

If you back up to a NAS, use a UNC path (`\\server\share\ClaudeBackups`), not a
mapped drive letter (`M:\ClaudeBackups`) - **Detect... already does this for
you** for any drive it finds mapped.

**Why this matters:** a mapped drive letter is tied to your interactive sign-in
session. When ClaudeCounter's scheduled backup runs from Task Scheduler - which
it does by default, non-interactively - a drive letter mapped only in your
logged-in session is not guaranteed to be visible to that scheduled process. The
practical failure mode: **"Back up now" works (you are signed in when you click
it), but the scheduled run silently fails** the same way stopped scheduled Drive
backups have historically failed for other reasons - see Troubleshooting below.
A UNC path has no such dependency; the NAS resolves it the same way regardless
of which session (or lack of one) is asking.

If you already have `M:\ClaudeBackups` configured and want to fix this: open
**Detect...** again, pick the same drive, and it will offer the UNC form instead.

### Retention

Drive keeps one zip per run and prunes nothing by default, so a daily backup adds
365 files a year, same as the rclone transport. **Advanced...** lets you keep
only the most recent N, delete anything older than N days, or both. Pruning only
ever touches this app's own `claude-backup-*.zip` files, only runs after a
successful copy, and never deletes the last remaining backup.

---

## Advanced: rclone remote

Use this only if you specifically need an rclone-supported destination that is
not just a folder a sync client already manages for you - the sync-folder method
above covers Google Drive, OneDrive, Dropbox, and NAS shares with far less setup.

ClaudeCounter uploads through [rclone](https://rclone.org) for this transport. It
never sees or stores your Google credentials - rclone holds them in its own
config and refreshes them itself. ClaudeCounter only ever runs `rclone copy`.

Setting this up is a one-time job in three parts: create your own Google OAuth
client, authorise rclone with it, then point ClaudeCounter at the remote.

> **You need your own OAuth client ID.** rclone ships with a shared one, but it is
> **being retired and will stop working during 2026**, and it is heavily rate
> limited in the meantime. Skipping part 1 means your backups break later, so do it
> now.

### Part 1 - Create a Google OAuth client

Do this once, in the [Google Cloud Console](https://console.cloud.google.com/). The
Google account you use here does **not** have to be the one whose Drive you back up
to.

#### 1. Create or select a project

Any project works. If you have none, create one - the name is irrelevant.

#### 2. Enable the Drive API

**APIs & Services** -> **Enable APIs and Services** -> search for **Drive** ->
open **Google Drive API** -> **Enable**.

#### 3. Configure the OAuth consent screen

**APIs & Services** -> **OAuth consent screen** (if prompted, **Get started**).

- **App name**: anything, e.g. `rclone`
- **User support email**: your own address is fine
- **Audience**: **External**, unless you are on Google Workspace and the target
  Drive is in the same organisation, in which case **Internal** is simpler
- **Contact information**: your own address
- Agree to the terms and **Create**

#### 4. Add the scopes

Under **Data Access**, add these scopes, then **Update** and **Save**:

```
https://www.googleapis.com/auth/docs
https://www.googleapis.com/auth/drive
https://www.googleapis.com/auth/drive.metadata.readonly
```

#### 5. Add yourself as a test user

**Audience** -> **+ Add users** -> add your own Google account -> **Save**.

#### 6. PUBLISH THE APP - do not skip this

Still under **Audience**, click **Publish app** and confirm.

**This matters more than it looks.** An app left in **Testing** issues refresh
tokens that **expire after 7 days**. Your backups would work for a week and then
start failing silently, which is the worst possible failure mode for a backup you
are not watching. Publishing removes that expiry.

You do **not** need Google to verify the app. Verification is not required for
personal use under 100 users; you will simply see an "unverified app" warning when
you authorise, which you can safely click through for your own client.

*(If you chose **Internal** audience, publishing is not required - skip this step.)*

#### 7. Create the credentials

**Credentials** -> **Create OAuth client** (or **Create credentials** -> **OAuth
client ID**):

- **Application type**: **Desktop app**
- Name: anything

Google shows you a **Client ID** and a **Client Secret**. Keep them to hand for the
next part. Treat the secret like a password - it is not needed by ClaudeCounter and
should never go into `backup.json`.

### Part 2 - Authorise rclone

Install rclone:

```bash
winget install Rclone.Rclone
```

Open a **new** terminal so `PATH` picks it up, then:

```bash
rclone config
```

Answer the prompts:

| Prompt | Answer |
|---|---|
| `n/r/c/s/q` | `n` (new remote) |
| `name>` | `gdrive` |
| `Storage>` | `drive` (Google Drive) |
| `client_id>` | the Client ID from part 1 |
| `client_secret>` | the Client Secret from part 1 |
| `scope>` | `3` for `drive.file` - see below |
| `service_account_file>` | leave blank |
| Edit advanced config? | `n` |
| Use web browser to authenticate? | `y` |
| Configure as Shared Drive? | `n` |
| Keep this remote? | `y` |

Your browser opens; sign in and approve. If you see an "unverified app" warning,
that is expected for a personal client - continue past it.

#### Which scope to choose

| Option | Scope | What it allows |
|---|---|---|
| `1` | `drive` | Full access to every file in your Drive |
| `3` | **`drive.file`** | **Only files rclone itself created** |

**`drive.file` (option 3) is the right choice here.** ClaudeCounter only ever
creates its own zips and reads them back, so rclone never needs to see the rest of
your Drive. If this client is ever compromised, the blast radius is limited to the
backup folder rather than your entire Drive.

**The one catch:** with `drive.file`, rclone cannot see folders it did not create.
So do **not** pre-create the backup folder in the Drive web UI - let the first
upload create it. If you point ClaudeCounter at a folder you made by hand, rclone
will not find it and will make its own alongside.

Choose `1` (`drive`) only if you specifically need rclone to reach existing Drive
content.

#### Check it worked

```bash
rclone listremotes
```

You should see `gdrive:`.

### Part 3 - Point ClaudeCounter at it

**Settings** -> **Backup** -> **Back up to** -> **rclone remote (advanced)**:

- Tick **Back up to this destination**
- **Rclone remote**: `gdrive:ClaudeBackups`

The `gdrive:` part must match the remote name from `rclone config`. The
`ClaudeBackups` part is a folder rclone creates on first upload.

Choose what to back up with **Choose files...** - this destination has its own
selection, independent of GitHub.

Then **Back up now**. On success you get a "Backup complete" message; check the
log via **Open log folder** if anything looks wrong.

### Retention (rclone)

Same as the sync-folder transport above: **Advanced...** lets you keep only the
most recent N, delete anything older than N days, or both. Pruning only ever
touches this app's own `claude-backup-*.zip` files, only runs after a successful
upload, and never deletes the last remaining backup.

---

## Troubleshooting

**"rclone not found on PATH"** - rclone is not installed, or the terminal
ClaudeCounter inherited its `PATH` from predates the install. Restart the app.
(rclone transport only.)

**Backups worked, then stopped after about a week** - the OAuth app is still in
**Testing**. Publish it (Part 1, step 6). This is the single most common cause.
(rclone transport only.)

**"couldn't find directory" or the folder looks empty** - you are on `drive.file`
scope and the folder was created by hand in the Drive web UI, so rclone cannot see
it. Let rclone create the folder, or reconfigure with `drive` scope. (rclone
transport only.)

**"Back up now" works but the scheduled backup fails** - for the sync-folder
transport, check whether the configured path is a mapped drive letter
(`M:\...`) rather than a UNC path (`\\server\share\...`) - see "NAS: prefer a UNC
path" above, the single most common cause for this transport. For the rclone
transport, the scheduled task runs non-interactively, so anything needing a
browser prompt fails; re-authorise with `rclone config` while signed in, and
confirm the app is published.

**Rate limit or quota errors** (rclone transport) - you are probably still on
rclone's shared client ID. Redo Part 1 and supply your own.

---

## What ClaudeCounter never does

- For the sync-folder transport: it never talks to Google, Microsoft, Dropbox, or
  the NAS directly - it only ever writes a zip into an ordinary folder. There is
  no credential of any kind for ClaudeCounter to see, store, or leak.
- For the rclone transport: it never reads, stores, or logs your Google
  credentials - rclone owns them. It never puts a credential in `backup.json`. It
  scrubs credential-shaped strings out of anything it logs, because rclone's error
  output can echo a remote spec.
- For either transport: it never uploads `.credentials.json`, `session.dat`, keys,
  `.env`, or any file whose name suggests a secret - that list is enforced in code
  and cannot be overridden from Settings or by editing `backup.json`.
