# Nine Lives 🐈‍⬛

[![Build](https://github.com/jakemorgangit/NineLives/actions/workflows/build.yml/badge.svg)](https://github.com/jakemorgangit/NineLives/actions/workflows/build.yml)

**Every database deserves nine lives.**

Nine Lives is a modern desktop application for **backing up, restoring, and PROVING SQL Server databases restore** — to and from Azure Blob Storage or a file share both servers can see. Point-in-time recovery with a visual timeline (down to a named marked transaction), intelligent chain detection from LSNs, striped backups, TDE awareness, restore rehearsals with receipts, an exposure dashboard, and run notifications to Teams or Slack. Built with WPF on .NET 10, featuring dark, light and high-contrast UIs.

*A free tool from [Blackcat Data Solutions](https://blackcat.wales).*

![Screenshot - Main Interface](docs/screenshots/main-interface.png)

### Download Release

Download the latest release from the [Releases page](https://github.com/jakemorgangit/NineLives/releases).

The application is distributed as a single self-contained executable (`NineLives.exe`) — no installation required. Simply download and run.

On a Windows-on-ARM machine, take `NineLives-arm64.exe` instead. The plain `NineLives.exe` is x64 and will run there under emulation, just more slowly.

## Why Nine Lives?

Restoring a native SQL Server backup is painful with existing tooling when the destination server has no msdb backup history — the normal case for DR, environment refreshes, and migrations:

- **SSMS** makes you pick blobs from a container one file at a time, with no chain calculation and no timeline.
- **dbatools** (`Restore-DbaDatabase`) is excellent if you live in PowerShell — Nine Lives is the GUI complement, not a replacement. If your team prefers clicking a restore point on a timeline over composing cmdlet parameters mid-incident, this is for you.

Nine Lives discovers every backup, groups striped sets, computes the full restore chain (full → differential → log tail), and gives you a clickable point-in-time timeline. Generate the T-SQL, or execute it directly with live progress.

### Two media, both directions

Where a backup lives is a choice per operation, not a property of the tool:

|             | Azure Blob Storage      | A path both servers can see |
|-------------|-------------------------|-----------------------------|
| **Back up** | `BACKUP ... TO URL`     | `BACKUP ... TO DISK`        |
| **Restore** | `RESTORE ... FROM URL`  | `RESTORE ... FROM DISK`     |

Neither is right in every estate. **Blob** needs no network path between the two hosts, which is often the real blocker when source and target sit in different environments. **A shared path** is faster, costs no egress, needs no SAS with write, and does not make the restore wait on an upload — where both instances can reach it.

There is also a third source for the classic case neither medium covers: **a backup file somebody handed you** — a vendor's `.bak`, a file that outlived its server. Paste the path, and the file's own headers drive the same chain, options and execution as everything else.

## Proof, not hope

> The only tested backup is a restored backup.

Everyone repeats it; almost nobody has evidence. Since v1.5, Nine Lives produces the evidence:

- **Restore rehearsal** — one button restores the chosen chain to a scratch database, proves the data with `DBCC CHECKDB`, and drops the scratch copy. The History entry is the receipt an auditor asks for, and the duration is your *measured* RTO. Safety by construction: a generated name refused if it exists, never `WITH REPLACE`, every file relocated, and the cleanup runs last so any failure retains the evidence. Wrap it as an Agent job and the proof renews itself weekly.
- **The Exposure dashboard** — *"if this server died now, everything after 14:32 is gone — up to 47m of work"*, per database, across every configured server, worst first. Never-backed-up databases, FULL recovery with no log backups, chains that stopped, servers that will not answer — silence made red. Each row shows when a rehearsal last proved it, and how long that took; each row is one click from the restore screen.
- **Run notifications** — a message to Teams (incoming webhook or Power Automate), Slack, or any JSON endpoint when a backup, restore or copy starts, finishes, or hits a problem — including each database's failure in a multi-database backup at the moment it happens. For everyone whose focus is rightly *not* on the application while a 40-minute restore runs.
- **The DR runbook** — one self-contained Markdown export per restore point: the chain file by file, the prerequisites in worst-day order (credential, TDE certificates by thumbprint, disk space), the exact script, what to do when it stops part-way, what finishes the job. For the change-board pack and the DR repo.
- **The retention referee** — what a keep-N-days rule would actually do: what it keeps, what it can safely delete (with the bytes reclaimed), what must survive its own age because kept restores depend on it, and what is already broken. Report-only, deliberately: deleting stays a human act.

## Safety nets

Every one of these fires **before** `WITH REPLACE` drops anything:

- **Disk space** — the restore's file sizes against the target's volumes, including a drive the target does not have at all.
- **Version direction** — a newer version's backup aimed at an older server refuses by name (error 3169, caught before the run, not after the drop).
- **TDE and encrypted backups** — the certificate question asked up front, with the missing certificate named and the export/import route spelled out, instead of error 33111 mid-DR.
- **Chain truth** — chains build from LSNs wherever the source knows them; a differential with a missing base is not offered at all; an explicit audit verifies backups against their own headers.
- **Readability** — shared-path and ad-hoc restores confirm the *target's service account* can actually open the files before anything runs.

The whole workflow is the same either way: the same timeline, the same chain, the same options, the same script, the same execute path. Only two things ever differ — where the list of backups comes from, and how a `RESTORE` addresses a file.

### Restoring from a shared path

A file share cannot be listed the way a container can: a directory of `.bak` files says nothing about which database each belongs to, what type it is, or which full a differential was taken against. So Nine Lives reads the **source instance's own `msdb`** instead, which recorded all of it — including the LSNs, so a differential is paired with the full it was genuinely taken against rather than the nearest one by time.

Two things it checks that are easy to get wrong by hand:

- **`msdb` records the path the source wrote.** A job that backed up to `E:\SQLBackups\MyDb.bak` recorded a path that means something entirely different on the target — and if it resolves there at all, it resolves to the target's *own* `E:` drive, which is worse than failing. Nine Lives warns about that before it checks anything, and lets you say how the target reaches the same place.
- **Before the restore runs, it asks the target whether it can actually read every file** — with `RESTORE HEADERONLY`, on that instance, as the account that will do the work. This app's process can see a share the SQL Server service account cannot, and the usual cause of failure is an instance running as a local account or `NT SERVICE\MSSQLSERVER`, which has no identity on the network and so cannot read *any* share. Finding that out afterwards means finding it out after `WITH REPLACE` has already dropped the database being restored over.

### Taking a backup

`COPY_ONLY` is on by default, and turning it off is loud. A plain full backup resets the differential base on the source, so every differential that database's schedule takes afterwards depends on the file Nine Lives just wrote — which the person running the restore has never heard of. That warning appears on the screen, in the generated script, and in the console as the backup starts.

Backups are written to the layout the container is configured with, so what Nine Lives writes is what Nine Lives can then find.

## The CLI: 9lives.exe

The same engine from a terminal - for the pipeline, the runbook step, the scheduled task. `9lives.exe` ships beside the app and reads the configuration the app maintains: its containers, its servers, its credentials. Configure once in the GUI, script against it here.

```
9lives exposure                          the estate, judged, in one exit code
9lives list --container backups
9lives points --container backups --database Sales
9lives script --container backups --database Sales --at "2026-08-02 19:00" --out restore.sql
9lives validate --server SRV01 --json
```

Five read-only verbs. `list` says what a source holds; `points` is the timeline as text or JSON; `script` emits the validated restore T-SQL for a moment - the same chain calculation, striped-set grouping and STOPAT/marked-transaction handling the GUI runs, which is why the script it emits actually works; `validate` checks every chain is intact and answers in the exit code; `exposure` sweeps every configured server and exits with the worst level it found, so a scheduled task turns quiet log-backup silence into a red pipeline.

Exit codes are the contract: `0` fine, `1` warnings, `2` broken or unreachable-by-chain, `3` could not answer, `64` usage. `--json` on the read verbs makes the output composable with jq and friends.

The full reference ships inside the exe: `9lives help` for the overview, `9lives help restore` (or any verb) for the complete page - every option, the behaviour, the exit codes, examples. Documentation that lives in the binary cannot drift from it, and a test holds every page to the parser's own option lists.

**Provisioning from nothing.** A freshly built VM - a Terraform clone, a DR bubble, a scratch environment - has no app config and nobody at a screen. `add-server` and `add-container` create the configuration from the command line, validated by default: the server is asked its version (address, credentials and permissions proven in one round trip), the container is asked to answer with exactly the recorded SAS - because a SAS is a string that looks right for weeks after it expired. Both converge on re-run, secrets can ride in `NINELIVES_SQL_PASSWORD` / `NINELIVES_SAS` instead of flags, and a chained script stops at the first failed validation. The whole template is three lines, and the only variable is the moment:

```
9lives add-server --name target --address localhost --user svc_restore --password %SQL_PW%
9lives add-container --name backups --url https://acct.blob.core.windows.net/sqlbackups --sas %SAS%
9lives restore --container backups --database Sales --target target --at "%POINT_IN_TIME%" --execute
```

Two verbs execute, and they are built out of refusals: `restore` and `rehearse` run nothing without `--execute`; overwriting an existing database is said with `--with-replace`, its own flag that `--force` cannot substitute for; and the same preflights the app fires - file readability, version direction (error 3169), the TDE certificate (error 33111) - refuse before anything is dropped, `--force` being the deliberate override for evidence only. Executed runs land in the app's History and notify the same webhooks, so `9lives rehearse --execute` on a schedule is nightly proof with receipts - no SQL Agent required.

## Features

### Copying and Auditing
- Copy a database from one server onto another in one action, through blob or a shared path
- Audit a database's backups against their own headers, with the result cached so a second run is
  instant — and a pill on every restore point that has been confirmed
- Hand a restore over as a SQL Server Agent job, created disabled and unscheduled, for a
  maintenance window or a change process

### Backing Up
- Back up a database to Azure Blob Storage or to a path the SQL Server service account can write to
- `COPY_ONLY` by default, so a production differential schedule is left alone — and a clear warning when it is turned off
- `COMPRESSION` and `CHECKSUM` on by default
- Striped backups, which for blob are the only way past the 195 GB per-blob limit
- Written to the container's own configured layout, so the backup is discoverable by the restore screen
- The script is shown before anything runs, and the button arms on the first press

### Azure Blob Storage Integration
- Connect to Azure Blob Storage containers using SAS tokens, or Entra ID for organisations that prohibit long-lived SAS ([see the caveat](#entra-id-for-blob-storage))
- Automatic discovery of backup files (Full, Differential, Transaction Log)
- Intelligent parsing of blob path structures with customisable patterns
- Support for striped backup sets (multiple files per backup)
- Availability Group backups using Ola Hallengren's default AG naming (`Cluster$AG_Database_FULL_yyyymmdd_hhmmss_n.bak`)
- Secure SAS token storage using Windows Credential Manager

### SQL Server Connectivity
- Windows Authentication and SQL Server Authentication support
- Entra ID authentication — interactive (MFA), integrated, and default — for Azure SQL Managed Instance and Entra-enabled estates ([see the caveat](#entra-id-authentication))
- Configurable connection options (Encrypt, Trust Server Certificate, Timeout)
- Persistent server configurations with secure password storage
- Real-time connection status visible across all views

### Backup Discovery & Browsing
- Browse all backups in a container with filtering by server, database, and type
- Automatic inference of database names from folder structures
- Configurable blob path pattern builder with drag-and-drop components
- Summary statistics showing backup set counts (not just file counts)

### Point-in-Time Recovery
- Visual timeline of restore points across Full, Differential, and Log backups
- Colour-coded dots for Full (blue), Differential (orange), and Log (green) backups
- Clickable restore points with automatic chain calculation
- Intelligent backup chain building including required differentials
- **Stop at an exact moment (`STOPAT`)** — select a transaction log restore point and specify a
  precise time within it, rather than being limited to the end of a log backup. `STOPAT` is
  emitted on every `RESTORE LOG` in the chain, so recovery stops in whichever log actually
  contains the target time.

### Restore Script Generation
- Complete T-SQL restore scripts with proper `FROM URL` syntax
- **No SAS token or credential in scripts** — the generated script never contains credentials; the SQL Server must already have a credential for the blob container URL (or you create it from the app)
- Restore options show whether the blob credential exists on the connected server; optional "Create credential on server" so you can create/update it without putting the token in any script
- Support for all common restore options:
  - `WITH REPLACE` - Overwrite existing database
  - `WITH MOVE` - Relocate data/log files (auto-detects server default paths)
  - Recovery modes: `RECOVERY`, `NORECOVERY`, `STANDBY`
  - `KEEP_REPLICATION`, `ENABLE_BROKER`, `NEW_BROKER`
  - Configurable `STATS` percentage
- Disconnect sessions option (`SET SINGLE_USER WITH ROLLBACK IMMEDIATE`)

### Direct Execution
- Execute restore scripts directly against connected SQL Server instances
- Real-time progress logging with auto-scroll
- Safe "arm-to-execute" confirmation
- Full restore chain execution with progress feedback in execution console

## Screenshots

### Blob Storage Configuration
![Screenshot - Blob Configuration](docs/screenshots/blob-config.png)

*Configure Azure Blob Storage containers with SAS tokens. Drag-and-drop path pattern builder for custom folder structures.*

### Browse Backups
![Screenshot - Browse Backups](docs/screenshots/browse-backups.png)

*Browse all backups with filtering by server, database, and backup type. Summary shows set counts accounting for striped backups.*

### Restore Timeline
![Screenshot - Restore Timeline](docs/screenshots/restore-timeline.png)

*Visual timeline with clickable restore points. Select any point to see the complete restore chain required.*

### Restore Options
![Screenshot - Restore Options](docs/screenshots/restore-options.png)

*Configure restore options including WITH MOVE with auto-detected server paths, recovery mode, and more.*

### Script Generation & Execution
![Screenshot - Script Execution](docs/screenshots/script-execution.png)

*Generate restore scripts or execute directly with real-time progress logging.*

## Installation

### Download Release

Download the latest release from the [Releases page](https://github.com/jakemorgangit/NineLives/releases).

**Requirements:**
- Windows 10/11 (x64)
- No .NET runtime installation required (self-contained)

### "Windows protected your PC"

The executable is not yet code-signed, so SmartScreen will stop it the first time you run it.
Click **More info** → **Run anyway**.

That warning means Windows has not seen this file before — not that anything is wrong with it. But
you shouldn't have to take that on faith for a tool you're about to point at production with
sysadmin rights, so there are two ways to check.

**Checksum** — confirms the file matches what the release page publishes:

```powershell
Get-FileHash NineLives.exe -Algorithm SHA256
```

Compare against `SHA256SUMS.txt` on the release.

**Build provenance** — confirms the file was built by this repository's release workflow, from a
specific commit, and has not been altered since. Needs the [GitHub CLI](https://cli.github.com):

```powershell
gh attestation verify NineLives.exe --repo jakemorgangit/NineLives
```

A pass prints the commit and workflow that produced it. This is a stronger guarantee than the
checksum, which only proves the file matches a number published beside it.

Getting the exe signed properly is tracked in
[#33](https://github.com/jakemorgangit/NineLives/issues/33); the options are written up in
[docs/code-signing.md](docs/code-signing.md).

### Build from Source

#### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) or later
- Windows 10/11 (WPF application)

#### Clone and Build

```bash
git clone https://github.com/jakemorgangit/NineLives.git
cd NineLives
dotnet build
```

#### Run in Development

```bash
dotnet run --project src/NineLives
```

#### Run Tests

```bash
dotnet test
```

#### Publish Single-File Executable

```bash
dotnet publish src/NineLives -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o ./publish
```

The executable will be created at `./publish/NineLives.exe`.

## Usage Guide

### 1. Configure Blob Storage

1. Navigate to **Blob Storage** in the sidebar
2. Click **+ Add Container**
3. Enter a display name for the container
4. Enter the full container URL (e.g., `https://mystorageaccount.blob.core.windows.net/sqlbackups`)
5. Choose how to authenticate:
   - **SAS token** — paste one with at least `list` and `read` permissions
   - **Entra ID** — nothing to paste, and nothing is stored ([caveat below](#entra-id-for-blob-storage))
6. Configure the **Blob Path Structure** to match your backup folder layout:
   - Default: `{BackupType}/{ServerName}/{DatabaseName}/{FileName}`
   - Drag and drop components to rearrange
   - Add `{InstanceName}` if your paths include SQL instance names
   - For Availability Group backups, select the AG source type (supports Ola Hallengren default AG naming and cluster/AG path tokens)
7. Click **Test Connection** to verify access
8. Click **Save**

#### Entra ID for Blob Storage

| Mode | What it does | When to pick it |
| --- | --- | --- |
| **Interactive (MFA)** | Opens a browser to sign in. The sign-in lasts as long as the app is running and is written nowhere | Any Entra-enabled account, including MFA |
| **Default** | Environment, then managed identity, then Azure CLI / Visual Studio sign-in, then a prompt | Running the app on an Azure VM with a managed identity |

Your account needs **Storage Blob Data Reader** on the container. Owner or Contributor is **not**
enough — blob *data* access is a separate set of roles from the ones that administer the account,
which is the usual first surprise.

If another tool works with the same account, that is not a contradiction. Azure Storage Explorer
commonly falls back to the storage **account key**, which Owner *can* fetch through the management
plane, so it never exercises the role at all.

When a permission error does appear, **Test Connection names the account it signed in as**. Checking
that first is worth doing — a 403 against an account that demonstrably has access usually means a
different account or tenant was used than the one being thought about.

Switching a container from SAS to Entra deletes its stored token from Windows Credential Manager. An
organisation that has banned long-lived SAS has banned it wherever it is sitting.

**This covers browsing only.** The RESTORE itself runs on SQL Server, which needs its own credential
for the container URL — for Entra that is `CREATE CREDENTIAL ... WITH IDENTITY = 'Managed Identity'`
on SQL Server 2022+ or Azure SQL MI. Entra on the client and a SAS credential on the server is a
perfectly valid combination.

### 2. Configure SQL Server (Optional - for direct execution)

1. Navigate to **SQL Servers** in the sidebar
2. Click **+ Add Server**
3. Enter server details and authentication method
4. Configure encryption and certificate options as needed
5. Click **Test Connection** to verify
6. Click **Save**
7. Select the server and click **Connect** to establish a session

#### Entra ID authentication

Three modes, all of which store nothing:

| Mode | What it does | When to pick it |
| --- | --- | --- |
| **Interactive (MFA)** | Signs in through the Windows account broker, parented to the app window | The mode that satisfies multi-factor authentication. Optionally give a username to pre-select the account |
| **Integrated** | Uses the Windows account you are already signed in with, no prompt | Machine joined to the directory |
| **Default** | Environment, then managed identity, then the signed-in account, then a prompt | SQL Server on an Azure VM, where the managed identity should be used |

Switching a saved connection from SQL Authentication to any other mode deletes its stored password
from Windows Credential Manager — a secret nothing uses any more should not outlive the reason it
was stored.

Note that this covers **the app's own connection to SQL Server**. Restoring `FROM URL` still needs a
credential on the *instance* for the blob container, which is separate and configured on the server.

### 3. Browse Backups

1. Navigate to **Browse Backups** in the sidebar
2. Select your configured container from the dropdown
3. Click **Load Backups**
4. Use filters to narrow down by server, database, or backup type
5. Review the summary showing backup set counts

### 4. Back Up a Database

1. Navigate to **Back Up** in the sidebar
2. Choose the server and click **List databases**, then pick the database
3. Choose where it goes — a blob container, or a folder the SQL Server service account can write to
4. Leave **COPY_ONLY** ticked unless you specifically mean to move the differential base
5. Click **Generate script**, read what it says, then press **Run backup** twice — it arms on the first press

### 5. Restore a Database

1. Navigate to **Restore** in the sidebar
2. Choose where the backups live:
   - **Azure Blob Storage** — select your container and click **Load Backups**
   - **A path both servers can see** — select the instance that TOOK the backups and click **Load Backups**. Its `msdb` is read rather than a folder being listed. If the target reaches the files by a different path, give both forms in the two boxes below.
3. Filter by server/database if needed
4. Click a restore point on the timeline (or in the list)
5. Review the restore chain in the details panel
6. Configure restore options:
   - **Target Database Name** - Name for the restored database
   - **WITH REPLACE** - Overwrite if database exists
   - **WITH MOVE** - Relocate files (paths auto-detected from connected server)
   - **Recovery Mode** - RECOVERY (online), NORECOVERY (for more restores), or STANDBY
   - **Point in Time** - when a transaction log restore point is selected, tick *Stop at a
     specific time* and enter the target as `yyyy-MM-dd HH:mm:ss`. The time must fall within the
     selected log's window (after the previous restore point, up to the selected one); to stop
     earlier or later, pick a different point on the timeline.
7. Click **Generate Script** to create the T-SQL
8. Either:
   - **Copy to Clipboard** or **Save to File** to run manually in SSMS
   - **Execute on Server** to run directly (requires connected SQL Server)

### 6. Copy a Database to Another Server

Back up the source and restore it onto the target in one action.

1. Navigate to **Copy Database** in the sidebar
2. Choose the source server and database, then click **List databases**
3. Choose how it travels — a blob container, or a folder both servers can reach
4. Choose the target server and what to call the copy
5. Click **Generate scripts** — both statements are shown together, because what happens to each
   server is one decision
6. Press **Copy database** twice; the confirmation names both servers

If the backup succeeds and the restore fails, the app says so explicitly: the backup is real and
usable, and restoring from it is the right next step rather than running the whole thing again.

### 7. Audit Backups Against Their Headers

Optional, and worth doing on a container nobody has confirmed the path pattern for.

Path-and-filename inference is right most of the time and wrong in ways that stay invisible until a
restore is needed — a log filed as a full, a backup filed under the wrong database. The header is
what a `RESTORE` reads, so it settles the question.

1. Load a container and choose a database on the **Restore** screen
2. The **Audit these backups** panel estimates how long it will take, from a measured cost per
   backup set
3. Click **Audit this database**; it can be stopped at any point and what it checked is kept
4. Restore points and chain members that match their headers carry a **✓ audited** pill

Results are cached against each blob's ETag, so a second audit is instant and closing the app does
not lose them. A backup replaced under the same name is read again.

### 8. Execute Restore

When clicking **Execute on Server**:
1. The button changes to "Confirm Execute (5)" with a countdown
2. A warning banner appears showing the target server and database
3. Click the button again within 5 seconds to confirm
4. Watch the real-time execution log for progress
5. Upon completion, the database is restored and online (if RECOVERY mode selected)

## SAS Token Requirements

Your SAS token needs the following minimum permissions:
- **List (l)** - To enumerate blobs in the container
- **Read (r)** - To read backup files

For restore execution, the SQL Server instance must have a credential for the blob container URL. Two identities work: `SHARED ACCESS SIGNATURE`, and — on SQL Server 2022 and later or Azure SQL MI — `Managed Identity`. You can create the SAS kind from the app using "Create credential on server" in the Restore options; the SAS token is never included in generated scripts.

A credential that already exists is never rewritten by running a restore. If it holds a managed identity the app reports it as valid and leaves it alone, since replacing it would change how the whole instance reaches that container. Replacing one is a deliberate press of the button on the credential panel, which says exactly what it would do.

Example SAS token permissions: `sp=rl` (read + list)

Recommended: `sp=racwdl` (read, add, create, write, delete, list) for full functionality.

## Backup Folder Structure

The application parses backup file paths to infer metadata. Configure the path pattern to match your structure.

### Default Pattern
```
{BackupType}/{ServerName}/{DatabaseName}/{FileName}
```

Example paths:
```
FULL/SQLSERVER01/AdventureWorks/20240115_220000_1.bak
DIFF/SQLSERVER01/AdventureWorks/20240116_060000_1.bak
LOG/SQLSERVER01/AdventureWorks/20240116_070000.trn
```

### With Instance Name
```
{BackupType}/{ServerName}/{InstanceName}/{DatabaseName}/{FileName}
```

Example:
```
FULL/SQLSERVER01/MSSQLSERVER/AdventureWorks/20240115_220000.bak
```

### Availability Group Backups (Ola Hallengren default naming)

Flat filenames using Ola's default AG format are parsed automatically:
```
azdbcluster1$MyAG_MyDatabase_FULL_20260226_200032_1.bak
azdbcluster1$MyAG_MyDatabase_LOG_20260226_210500_1.trn
```

Path-based AG layouts are also supported via the `{ClusterName}`, `{AgName}`, `{ClusterName$AgName}`, and `{ClusterName_AgName}` tokens.

### Striped Backups

The application automatically detects striped backup sets by filename pattern:
```
20240115_220000_1.bak
20240115_220000_2.bak
20240115_220000_3.bak
20240115_220000_4.bak
```

These are grouped as a single backup set with 4 files.

## Restore Chain Logic

The application builds complete restore chains automatically:

1. **Full Backup** - Always required as the base
2. **Differential Backup** - The latest differential since the full (differentials are cumulative)
3. **Transaction Logs** - All logs after the differential (or full if no diffs)

When you select a transaction log restore point, the chain includes:
- The most recent full backup before that log
- The latest differential between the full and the log
- All transaction logs from the differential to your selected point

## Security

### Credential Storage

- **SAS Tokens** - Stored securely in Windows Credential Manager
- **SQL Passwords** - Stored securely in Windows Credential Manager
- **Configuration** - Non-sensitive settings stored in `%LOCALAPPDATA%\NineLives\config.json`

### SAS Token Handling

- **After save, the token is never shown again** — once a SAS token is saved for a container, it cannot be viewed in the UI; you can only replace it by entering a new token. This reduces the risk of exposure.
- For new containers (before save), you can type the token and optionally show/hide it while editing.

## Architecture

Built with:
- **.NET 10** (LTS) - Windows Presentation Foundation (WPF)
- **CommunityToolkit.Mvvm** - MVVM pattern implementation
- **Azure.Storage.Blobs** - Azure Blob Storage SDK
- **Microsoft.Data.SqlClient** - SQL Server connectivity

### Project Structure
```
NineLives/
├── .github/
│   └── workflows/               # CI: PR build/test gate, version-bump gate, release pipeline
├── docs/
│   └── screenshots/             # README screenshots
├── src/
│   ├── NineLives/
│   │   ├── Assets/              # Application icon
│   │   ├── Converters/          # XAML value converters
│   │   ├── Models/              # Data models
│   │   ├── Properties/          # Publish profiles
│   │   ├── Services/            # Business logic services
│   │   ├── Themes/              # Dark theme resources
│   │   ├── ViewModels/          # MVVM ViewModels
│   │   └── Views/               # XAML views
│   └── NineLives.Tests/         # xunit tests (chain logic, parsers, script generation)
├── build_standalone.cmd         # One-shot publish script
└── NineLives.sln                # Solution file
```

## Known Limitations

- Windows only (WPF application)
- Single container per restore operation — a chain whose full and log backups live in different
  containers is not yet supported
- A managed-identity credential can be created from here, but only on **SQL Server 2022 or later,
  or Azure SQL Managed Instance**. Earlier versions accept the statement and then fail at restore
  time, so the app does not offer it there
- Creating that credential does not grant anything. The instance needs a managed identity, and that
  identity needs **Storage Blob Data Reader** on the container
- Restoring to a UNC path is supported, but the disk-space check does not cover it — a share has no
  drive letter for SQL Server to report free space on

## Troubleshooting

### "AuthorizationFailure" when connecting to blob storage
- Verify your SAS token has `list` permission
- Check the token hasn't expired
- Ensure the container URL is correct (no trailing slash)
- On Entra ID, the account needs **Storage Blob Data Reader** on the container. Owner or
  Contributor is not enough — see [Entra ID for Blob Storage](#entra-id-for-blob-storage) above.
  Test Connection names the account it signed in as

### "File cannot be restored to..." error
- Enable **WITH MOVE** option
- Connect to SQL Server first to auto-detect default paths
- Manually specify data/log file paths

### Database left in "RESTORING" state
- This is expected if you selected **NORECOVERY** mode
- Run `RESTORE DATABASE [YourDB] WITH RECOVERY` to bring it online

## Contributing

Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md) for the full guide.

In short: **`dev` is the default branch and where work lands; `main` tracks the released code.**
Fork, branch from `dev`, and open your pull request against `dev`. Releases are a `dev` → `main`
pull request with a version bump.

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Author

**Jake Morgan** - [Blackcat Data Solutions Ltd](https://blackcat.wales)

## Acknowledgements

- UI design inspired by [Erik Darling's SQL Performance tools](https://github.com/erikdarlingdata/PerformanceMonitor)
- Built with [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet)
