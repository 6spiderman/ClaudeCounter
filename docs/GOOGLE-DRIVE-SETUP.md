# Google Drive backup setup

ClaudeCounter uploads Drive backups through [rclone](https://rclone.org). It never
sees or stores your Google credentials - rclone holds them in its own config and
refreshes them itself. ClaudeCounter only ever runs `rclone copy`.

Setting this up is a one-time job in three parts: create your own Google OAuth
client, authorise rclone with it, then point ClaudeCounter at the remote.

> **You need your own OAuth client ID.** rclone ships with a shared one, but it is
> **being retired and will stop working during 2026**, and it is heavily rate
> limited in the meantime. Skipping part 1 means your backups break later, so do it
> now.

---

## Part 1 - Create a Google OAuth client

Do this once, in the [Google Cloud Console](https://console.cloud.google.com/). The
Google account you use here does **not** have to be the one whose Drive you back up
to.

### 1. Create or select a project

Any project works. If you have none, create one - the name is irrelevant.

### 2. Enable the Drive API

**APIs & Services** -> **Enable APIs and Services** -> search for **Drive** ->
open **Google Drive API** -> **Enable**.

### 3. Configure the OAuth consent screen

**APIs & Services** -> **OAuth consent screen** (if prompted, **Get started**).

- **App name**: anything, e.g. `rclone`
- **User support email**: your own address is fine
- **Audience**: **External**, unless you are on Google Workspace and the target
  Drive is in the same organisation, in which case **Internal** is simpler
- **Contact information**: your own address
- Agree to the terms and **Create**

### 4. Add the scopes

Under **Data Access**, add these scopes, then **Update** and **Save**:

```
https://www.googleapis.com/auth/docs
https://www.googleapis.com/auth/drive
https://www.googleapis.com/auth/drive.metadata.readonly
```

### 5. Add yourself as a test user

**Audience** -> **+ Add users** -> add your own Google account -> **Save**.

### 6. PUBLISH THE APP - do not skip this

Still under **Audience**, click **Publish app** and confirm.

**This matters more than it looks.** An app left in **Testing** issues refresh
tokens that **expire after 7 days**. Your backups would work for a week and then
start failing silently, which is the worst possible failure mode for a backup you
are not watching. Publishing removes that expiry.

You do **not** need Google to verify the app. Verification is not required for
personal use under 100 users; you will simply see an "unverified app" warning when
you authorise, which you can safely click through for your own client.

*(If you chose **Internal** audience, publishing is not required - skip this step.)*

### 7. Create the credentials

**Credentials** -> **Create OAuth client** (or **Create credentials** -> **OAuth
client ID**):

- **Application type**: **Desktop app**
- Name: anything

Google shows you a **Client ID** and a **Client Secret**. Keep them to hand for the
next part. Treat the secret like a password - it is not needed by ClaudeCounter and
should never go into `backup.json`.

---

## Part 2 - Authorise rclone

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

### Which scope to choose

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

### Check it worked

```bash
rclone listremotes
```

You should see `gdrive:`.

---

## Part 3 - Point ClaudeCounter at it

**Settings** -> **Backup** -> destination selector -> **Google Drive**:

- Tick **Back up to Google Drive (rclone)**
- **Rclone remote**: `gdrive:ClaudeBackups`

The `gdrive:` part must match the remote name from `rclone config`. The
`ClaudeBackups` part is a folder rclone creates on first upload.

Choose what to back up with **Choose files...** - Drive has its own selection,
independent of GitHub.

Then **Back up now**. On success you get a "Backup complete" message; check the
log via **Open log folder** if anything looks wrong.

### Retention

Drive keeps one zip per run and prunes nothing by default, so a daily backup adds
365 files a year. **Advanced...** lets you keep only the most recent N, delete
anything older than N days, or both. Pruning only ever touches this app's own
`claude-backup-*.zip` files, only runs after a successful upload, and never deletes
the last remaining backup.

---

## Troubleshooting

**"rclone not found on PATH"** - rclone is not installed, or the terminal
ClaudeCounter inherited its `PATH` from predates the install. Restart the app.

**Backups worked, then stopped after about a week** - the OAuth app is still in
**Testing**. Publish it (part 1, step 6). This is the single most common cause.

**"couldn't find directory" or the folder looks empty** - you are on `drive.file`
scope and the folder was created by hand in the Drive web UI, so rclone cannot see
it. Let rclone create the folder, or reconfigure with `drive` scope.

**Scheduled backups fail while "Back up now" works** - the scheduled task runs
non-interactively, so anything that needs a browser prompt fails. Re-authorise with
`rclone config` while signed in, and confirm the app is published.

**Rate limit or quota errors** - you are probably still on rclone's shared client
ID. Redo part 1 and supply your own.

---

## What ClaudeCounter never does

- It never reads, stores, or logs your Google credentials. rclone owns them.
- It never puts a credential in `backup.json`.
- It scrubs credential-shaped strings out of anything it logs, because rclone's
  error output can echo a remote spec.
- It never uploads `.credentials.json`, `session.dat`, keys, `.env`, or any file
  whose name suggests a secret - that list is enforced in code and cannot be
  overridden from Settings or by editing `backup.json`.
