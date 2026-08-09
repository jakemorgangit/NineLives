# Changelog

All notable changes to Nine Lives are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Release notes on the [Releases page](https://github.com/jakemorgangit/NineLives/releases) go into
more detail on the user-facing changes; this file is the short history.

## [Unreleased]

### Added

- **Run notifications to Teams, Slack, or any JSON endpoint** - a message when a backup, restore
  or copy starts, when it finishes, and whenever there is a problem, including each database's
  failure in a multi-database backup AT the moment it happens. Teams gets a MessageCard (works
  with both incoming-webhook connectors and Power Automate flows), Slack gets Block Kit, and the
  generic format is plain JSON fields. Configured in Settings with a per-endpoint test button;
  webhook URLs never leave the machine in a configuration export (#242)

### Changed

- **One console, not two.** The restore screen no longer keeps an inline copy of the run's
  output behind the execution window - every line and panel was rendered twice. The window is
  the console; a "View the last run's output" button reopens it over the same record, and the
  History screen keeps the permanent copy

## [1.4.1] - 2026-08-09

### Fixed

- A drive the target does not have was described as having "0.0 B free". Absent is now said as
  absent, with the actual fix named: relocate the files (MOVE) or pick a different target -
  freeing space on a drive that does not exist was never the answer (#233)
- **Copy Database crashed mid-run** - "An ItemsControl is inconsistent with its items source" -
  when SQL Server's progress messages, which arrive on the connection's worker thread, were added
  to the bound console straight from that thread. The copy and backup screens now marshal the way
  the restore screen always has, and the batching console buffer owns its thread-affinity outright
  so no future screen can reintroduce the race (#233)

## [1.4.0] - 2026-08-09

Nine Lives stops being a blob restore tool. It backs up and restores, to and from Azure Blob
Storage or a path both servers can see - and it can do both in one action.

It also stops showing all of that to everybody. The app grew a great deal in a short time, and
growth without a way to opt out of it is how a tool becomes intimidating to the people it was
built for.

### Added

- **Three modes - Basic, Standard and Pro - chosen once on first run** and changeable in Settings.
  Basic is the app as it originally was: pick a container, pick a restore point, restore. Standard
  adds the second medium, taking backups, point-in-time and file relocation. Pro adds everything
  else. Narrowing the mode never deletes anything (#176)
- **Hand a restore over as a SQL Server Agent job**, one step per batch, created disabled and
  unscheduled - for a maintenance window, or a change process that takes submitted scripts rather
  than somebody at a keyboard (#32)

- **Back up a database**, to a blob container or to a path the SQL Server service account can
  write to. `COPY_ONLY` by default, so a production differential schedule is left alone, with what
  turning that off costs stated on the screen, in the generated script, and in the console as it
  runs (#165)
- **Restore from a path both servers can see**, through the same workflow blob restores use - the
  same timeline, chain, options, point-in-time and execute path. Backups are found through the
  source instance's own `msdb` rather than by guessing from filenames (#149)
- **Copy a database to another server in one action** - back up the source, restore onto the
  target, through either medium. The confirmation names both servers, and a run that backed up but
  failed to restore says the backup is still good rather than reporting a bare failure (#105)
- **Audit backups against their own headers.** An explicit action with an estimate in front of it,
  a progress bar and a Stop, that reads what each backup actually is and reports where it disagrees
  with what its path claimed. Results are cached against the blob's ETag, so a second run is
  instant and a restart does not lose them. Audited backups carry a pill on the restore point and
  the chain, and the scope is the chosen database or the whole container (#130)
- **Identify backups a filename could not place.** Files landing with no type or no database are
  invisible to the restore screen entirely; the app now says how many there are and can settle them
  from their headers (#130)
- **Managed-identity credentials**, so an estate that forbids long-lived SAS tokens can restore as
  well as browse without one. Gated on SQL Server 2022 or Azure SQL MI, with a refusal that names
  the version it found (#147)
- **A disk-space check before a restore.** The file sizes were already coming back with the logical
  names and being discarded; summed per volume against what the target says it has free, they
  answer whether the restore can physically fit (#32)
- **A win-arm64 build** alongside x64 (#39)
- **Integration tests against a real blob API**, using Azurite, covering the listing and the
  path-pattern inference that decides what every backup is (#37)

### Changed

- **The operation log is readable inside the app that wrote it** - today's file with a filter,
  copy and refresh, from Settings. The support conversation is "what does the log say about X?",
  not "navigate to this folder" (#214)
- **The configuration can move to another machine without moving a single secret** - export
  writes containers, servers and settings to a file that is safe on a share or in a ticket (SAS
  tokens and SQL passwords stay in Windows Credential Manager; a SAS pasted into a container URL
  is stripped on the way out), and import adds and updates but never deletes. Each connection
  asks for its credential again on the new machine (#213)
- **Several databases can be backed up in one run** - tick them (or "All user databases"), one
  BACKUP per database sharing the same options, run database-at-a-time so a failure on the sixth
  names the sixth and the rest still run. The verify offer afterwards covers exactly what
  succeeded. "A copy-only of everything before we patch" is now one screen, once (#208)
- **The download is 55% smaller** - 71 MB instead of 157 MB - by compressing the single-file
  bundle. Measured before enabling: time-to-first-window is unchanged, hidden entirely inside the
  splash dwell (#212)
- **The app comes back where it was left**: window size, position and maximised state, and the
  screen that was in use - carrying on from the launch cards lands there rather than starting
  again. Geometry is applied only when it still puts a grabbable title bar on a screen, so a
  monitor unplugged since cannot swallow the window (#211)
- **The end of a backup stops being a dead end**: a successful run offers "Verify what was
  written" - RESTORE VERIFYONLY over the exact devices the statement wrote - and the backup
  screen gains the same "Copy as Agent job" handover the restore screen has, because backups are
  the thing people actually schedule (#207)
- **Backups can be taken WITH ENCRYPTION** - AES-256 against a server certificate picked from
  the source's own list (only certificates whose private key the backup can actually use are
  offered). The caution is stated where the choice is made: every future restore target needs
  that certificate first, so export it and keep it with the DR kit (#222)
- **TDE and encrypted backups are checked before the restore, not discovered by error 33111.**
  The preflight reads both certificate thumbprints from the header it already fetches; a missing
  certificate refuses by thumbprint - and by certificate NAME when the source instance can be
  asked - with the BACKUP CERTIFICATE / CREATE CERTIFICATE route spelled out and passwords left
  to whoever owns them. Copy Database warns before the backup half if the source database is TDE
  and the target lacks its certificate (#222)
- **Browse Backups hands over to Restore in one move** - a button once a database is chosen, and
  "Restore this database" on every row. The restore screen arrives with the same source selected,
  the backups loaded and the database landed, so the only thing left is choosing the restore
  point. Works from either browsing source, container or server history (#202)
- **A newer version's backup aimed at an older server refuses before anything runs**, naming both
  versions and the way out - SQL Server can never restore in that direction (error 3169), and
  without the check it failed mid-restore, after WITH REPLACE had dropped the target. The legal
  upgrade direction proceeds with a note about the one-way door. One HEADERONLY on the target,
  device-aware, so blob, shared-path and ad-hoc chains all get the same check (#210)
- **A running restore shows its progress as a bar**, built from the server's own STATS lines -
  per statement across the chain, mirrored to the Windows taskbar so the app conveys progress
  while minimised. Failure turns the taskbar red and stays red until the next run; finishing
  behind another window flashes the taskbar button, the polite signal that stops the moment the
  app is clicked (#204)
- **A successful restore now ends with the job's remainder on screen**, in the same read-then-run
  shape as the recovery panel: DBCC CHECKDB offered on every success - the restore is the cheapest
  moment to find corruption, and it proves the backup rather than just the copy - plus an automatic
  scan for orphaned SQL-auth users, the single most common post-restore fault. Fixable orphans get
  the ALTER USER ... WITH LOGIN statement; one with no matching login gets guidance that copies but
  never runs, because inventing a statement means inventing a password. The database's compat
  level, recovery model and owner are stated, not altered (#205)
- **A backup file nobody's msdb knows can now be restored** - the vendor .bak, the file that
  outlived its server. A third source on the restore screen takes pasted paths, asks a server to
  read each file's own headers, and hands the result to the same chain, options and script
  machinery as every other source. A file holding several appended backups yields one restore
  point per position and the script says WITH FILE = n; a single stripe of a striped set is
  refused by name, because its header happily describes media it cannot deliver (#203)
- HEADERONLY, FILELISTONLY and VERIFYONLY statements now name DISK or URL by what each file
  actually is, instead of assuming blob - which also makes the header audit meaningful for
  backups discovered through msdb (#203)
- **The app opens on the mode cards** rather than the container list. The first thing on screen
  says what shape the app is in and offers to change it, instead of the app simply being that shape
  with nothing saying why. Only a question the first time - after that the current mode is marked
  and "Keep what I have" carries straight on (#200)
- **A restore chain can span containers** - a full archived to cool storage with the logs that
  carry it forward still in the hot one. Additional containers are ticked alongside the selected
  one, which stays the primary: it is what the credential panel points at and the script header
  names. Every container the chain actually uses is checked for a credential on the target before
  the restore starts, because RESTORE FROM URL matches a credential by container URL and the
  failure would otherwise land after WITH REPLACE had dropped the target (#32)
- **Browse Backups can read a server's backup history**, not only a blob container. Backing up and
  restoring have taken either medium since #165 and this screen did not come along, so the one
  screen whose whole purpose is looking could not look at half of what the app writes. A share is
  not walked as a directory - a folder of .bak files says nothing about which database each belongs
  to - so the instance that took them is asked what it recorded (#197)
- **The modes are named for the work, not for a rank.** "Restore only", "Back up and restore" and
  "Everything", rather than Basic/Standard/Pro with a column of ticks against a column of features,
  an "everything in Basic" ladder and a "Use Pro" button. They are three views of one application
  and none of them costs anything; the screen should not suggest otherwise (#191)
- **The mode cards can be reopened** from Settings, with the mode in force marked and a way to leave
  without changing anything. They said what each mode turns on and were then shown once, replaced by
  a drop-down of three names - which is the part of them worth the least (#191)
- **The sidebar carries the logo** rather than the name typed out in bold, so the mark appears on the
  screen people look at all day and not only on the splash. The strapline is now
  RESTORE | RECOVER | RESUME: the old one named one medium and one direction (#191)
- Chains are built from **LSNs** wherever the source knows them - which means anything read from an
  instance's `msdb`. A differential is paired with the full it was genuinely taken against rather
  than the nearest one by time, and a log joins a chain when it carries it forward rather than when
  its timestamp happens to fall in range. Backups discovered by listing a container carry no LSNs
  and behave exactly as before (#130)
- The restore summary stays on screen while the rest of the page scrolls
- The README describes what the app is rather than what it was

### Fixed

- Copy Database never ran the disk-space check the restore screen has had since #182, so the
  screen where a copy fails after the target is already dropped was the one that never warned. It
  now checks the source's live file sizes against the target's volumes when the scripts are
  generated - including a drive the target does not have at all, which without MOVE clauses is
  exactly where the restore would aim (#206)
- "Keep what I have" on the launch cards landed on the container list - the exact landing the
  cards exist to avoid. It now lands on Restore, the same place choosing a mode lands (#209)
- The widest mode drew an empty bordered box under the source picker. The audit card's border was
  gated on the mode while its contents waited on the backups, so between "this mode can audit" and
  "there is something to audit" the chrome outlived its own contents (#195)
- The mode cards sat at the top of the window and off to the right of it. Hiding the sidebar left
  its 220px column behind, because a ColumnDefinition keeps its width whatever happens to the
  element inside it (#191)
- The mode picker in Settings showed a raw object - `ModeOption { Mode = Pro, Name` - instead of the
  mode's name. The picker is gone in favour of the cards, and the buttons on those cards now line up
  with each other rather than landing wherever the text above them ended (#191)
- A differential whose base full is missing is no longer offered at all. It used to be paired with
  the nearest full by time, which SQL Server rejects - after `WITH REPLACE` has already dropped the
  target (#130)
- An `msdb` row whose backup files have since been pruned is no longer offered as a restore point
  with nothing to read from (#149)
- The Audit button stayed disabled after choosing a database, next to an estimate that had
  correctly updated - the button was never told to re-ask (#130)
- Test runs wrote to the real log file and audit cache in the profile of whoever ran them (#130)

## [1.3.0] - 2026-08-06

Authentication that does not need a SAS token, and the restore screen taken apart from the inside.
Runs on **.NET 10**. **953 tests**, up from 587.

Entra ID is the headline. Browsing a container and connecting to SQL Server no longer require a
long-lived SAS token or a stored password, which is what kept the tool out of estates that prohibit
them. The server-side credential a restore authenticates with is a separate thing, and this release
also stops the app quietly rewriting one it did not create.

### Added

- **Light and high-contrast themes**, switchable from About and remembered between runs. The dark
  theme is retuned to match the splash screen it always sat next to (#109)
- **Verify Backups** — `RESTORE VERIFYONLY` over every backup in the chain, so an unreadable blob
  is found in seconds rather than an hour into a restore (#26)
- **`CHECKSUM` and `CONTINUE_AFTER_ERROR`** restore options, both off by default (#26)
- **Restore history** — every execution recorded with its script, console log and outcome, plus a
  **Save output** action for the console (#31)
- **A sortable list of restore points** beside the timeline, with type filters and a date range
  that zooms rather than just filters (#27)
- Backup times now record which clock they came from; blob-derived ones are marked approximate (#47)
- A **Stop** button for the server queries — verify, validate, the metadata reads and the
  post-failure recovery actions (#111)
- Execute now says *why* it is disabled instead of just being greyed out (#117)
- **Entra ID authentication for blob containers**, as an alternative to a SAS token, for
  organisations that do not allow long-lived SAS. A permission failure names the account that was
  refused and the role it is missing, which Azure's own error does not (#29)
- **Entra MFA for SQL Server connections** (`ActiveDirectoryInteractive`), with the sign-in prompt
  parented to the app window (#30)
- **A per-container backup server time zone**, so backups whose filenames carry no timestamp sort
  correctly against the rest instead of being marked approximate. The conversion is from the
  instant, so daylight saving is handled (#102)
- **The app says when it is working** — spinners on the long actions, a completion tick, and a
  global busy status (#128)
- The saved servers list gained card edges and a marker for the connected one, and dropped the
  auth line it repeated on every row (#127)

### Fixed

- **The script on screen is the script that runs.** Four options — both WITH MOVE paths, the
  STANDBY undo file and STATS — never regenerated it. Ticking WITH MOVE while connected could
  produce a script with no `MOVE` clause at all, sending the restore to the file paths baked into
  the backup (#110)
- STANDBY with a blank undo path emitted `STANDBY = ''`, which fails as the last statement of the
  chain — after the target has already been overwritten (#110)
- Changing the container on the Restore screen left the previous container's chain and script
  armed and executable (#112)
- **Refresh Token silently deleted a container's tags** (#114)
- Reloading with the same database selected never rebuilt the restore points, so a backup taken
  since the last scan never appeared (#27)
- A container with no full backup had its explanation overwritten by the load summary (#41)
- Saving now writes the config *before* the secret, so a refused save cannot leave Credential
  Manager holding a password the config file does not match (#113)
- The credential check no longer races itself and reports the container you just left (#111)
- **Verify Backups now passes the MOVE clauses**, so it verifies the restore that is actually going
  to run rather than a different one (#129)
- White text on the yellow accent was unreadable under high contrast (#126)
- **The app no longer freezes while the Entra sign-in browser is open.** `InteractiveBrowserCredential`
  waits for the sign-in on whatever thread asked for the token, and every blob operation starts on
  the UI thread — so the window stopped painting for as long as the browser was open, which is to
  say it asked you to go and authenticate while appearing to have crashed (#152)
- **A Managed Identity blob credential is no longer converted to SAS by running a restore.** The
  check reduced every identity to "is it a SAS credential", so a credential authenticating as the
  instance's own identity was reported as broken and then altered into a SAS one — changing how
  anything else on that instance reached the same container. Execute now leaves any usable
  credential alone, and never converts one it did not create: replacing an identity is what the
  button on the panel is for, and it says so before it is pressed (#145)

### Changed

- The I/O services are behind interfaces, so the ViewModels can be tested. The first ViewModel
  tests came with it (#41)
- `null!` placeholders on the chain members replaced with `required` (#116)
- `RestoreOptions` no longer carries a SAS token it never used (#42)
- One publish definition instead of three. The release workflow was missing `PublishReadyToRun`
  because it repeated the profile's flags rather than using it (#39)
- The four verification actions are named for what they each do, rather than four variations on
  "verify" (#117)
- The update banner is styled to be noticed — it was dark on a dark app, so it read as chrome (#100)
- **The restore screen is five smaller pieces rather than one 2,600-line class**: the console, the
  timeline, the point-in-time target, the options, and the server-side credential. Behaviour is
  unchanged; what changed is that each part can now be tested on its own, and most of the new tests
  in this release cover behaviour that previously needed a whole restore to reach (#115)

## [1.2.0] - 2026-08-05

The biggest release so far, and mostly about correctness. Runs on **.NET 10**. **587 tests**, up
from 345.

### Fixed

- The restore could run against a different server than the one you connected with (#11)
- Every Execute dropped and recreated the blob credential, whether or not anything needed
  changing — removing it out from under anything else using it (#10)
- A database whose name contained "diff" had its full backups classified as differentials, and
  could end up with no restore points at all (#45)
- A transaction log written as `.bak` became the root of a chain and truncated earlier log chains (#44)
- A briefly locked `config.json` wiped every saved server and container (#7)
- Renaming a container stranded its SAS token (#8)
- Test Connection saved secrets before you did (#12)
- An unexpected error no longer closes the app and takes the execution log with it (#13)

### Added

- Cancellation for a running restore and a long container listing (#25)
- Post-failure recovery guidance, with the statements that put the database right (#14)
- A proper terminal for the execution console — its own window, live progress, SQL syntax
  highlighting, and a script that updates as options change
- An operation log at `%LOCALAPPDATA%\NineLives\logs`, with secrets stripped on the way in (#40)
- Unreachable log backups are reported rather than quietly disappearing (#46)

### Security

- SQL passwords are passed out-of-band and never appear in a connection string (#20)
- Test Connection reports whether certificate validation would actually succeed (#17)
- A SAS expiry that cannot be read counts as expired rather than valid (#21)
- Copied blob URLs no longer carry the SAS token (#18)
- Every dependency pinned and locked, verified on every build (#16)
- Releases carry a Sigstore build provenance attestation (#33)

## [1.1.0] - 2026-08-05

345 tests.

### Fixed

- Every log backup reachable from a chain is now offered (#24)
- Backup sets are identified per server and per instance, so two instances no longer merge (#43, #48)
- Copy-only fulls are no longer used as differential bases (#49)

### Added

- Structural and LSN chain validation (#62, #23)
- Tags on servers and containers (#67)
- An update check against the releases page (#65)
- The paw logo and the splash screen

## [1.0.0] - 2026-08-05

First release. Restore SQL Server databases from Azure Blob Storage with full, differential and
transaction log chains.

[Unreleased]: https://github.com/jakemorgangit/NineLives/compare/v1.2.0...HEAD
[1.2.0]: https://github.com/jakemorgangit/NineLives/compare/v1.1.0...v1.2.0
[1.1.0]: https://github.com/jakemorgangit/NineLives/compare/v1.0.0...v1.1.0
[1.0.0]: https://github.com/jakemorgangit/NineLives/releases/tag/v1.0.0
