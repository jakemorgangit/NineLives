# Changelog

All notable changes to Nine Lives are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Release notes on the [Releases page](https://github.com/jakemorgangit/NineLives/releases) go into
more detail on the user-facing changes; this file is the short history.

## [Unreleased]

### Added

- **S3-compatible object storage, the listing half** (#51). The app can now browse an
  `s3://` container: a hand-rolled SigV4 signer and a minimal ListObjectsV2 client - one
  signed GET, paged, path-style over TLS - behind the same storage interface Azure listings
  use, so discovery, set grouping, chain building and the restore screens neither know nor
  care which provider answered. No SDK: the signer is pinned stage by stage against AWS's
  published test vectors. The region resolves configured, then host name, then us-east-1
  (the engine's own default); Test Connection and `add-container` validation now genuinely
  prove an S3 key pair reaches its bucket; and a refusal surfaces the provider's own error
  sentence (AccessDenied and friends explain themselves). The storage-screen surfaces
  follow next.

- **S3-compatible object storage, the engine half** (#51). An `s3://` container is just
  another entry in the list - the URL's own scheme picks the provider, so existing configs
  migrate by doing nothing. The credential is the pair `AccessKeyId:SecretKey`, which is the
  engine's own secret format, so it rides the whole existing SAS pipeline unchanged (vault
  slot, in-memory member, export-strips-secrets, environment variable) with a shape check at
  entry. The server-side credential branches to `IDENTITY = 'S3 Access Key'`; backups
  overwrite with `FORMAT` (the S3 connector has no append), and both generators emit the
  optional region as `BACKUP_OPTIONS`/`RESTORE_OPTIONS` JSON on every statement. A hard
  preflight refuses an S3 restore below SQL Server 2022 or on Express - a capability `--force`
  cannot conjure - before `WITH REPLACE` drops anything. `add-container` and `--ephemeral`
  take S3 endpoints.

- **The execution verbs end as data, and receipts know their origin** (#303). `--json` on
  restore, rehearse and backup puts the ending on stdout machine-shaped: outcome, chain,
  point reached, measured duration - the rehearsal's proof carrying the real RTO number -
  warnings, refusals and the history id, while the prose stays on stderr. A pipeline
  archives the artefact instead of parsing sentences. And every receipt now records which
  front end acted: History rows say "via CLI", because a 3am scripted restore reads
  differently in an incident review than a clicked one
- **Ephemeral mode: an estate defined only in the environment** (#302). `--ephemeral` on
  any verb resolves server and container names against zero-persistence definitions from
  NINELIVES_SERVER, NINELIVES_SQL_USER/PASSWORD, NINELIVES_CONTAINER_URL and NINELIVES_SAS -
  consulted before the profile's config and written nowhere, secrets held in process memory
  only. For the locked-down service account that must not own vault state and the CI agent
  where nothing may outlive the job. History receipts still write and webhooks still fire -
  they are the point. Documented on the overview help page
- **The exposure sweep can speak** (#301): `9lives exposure --notify` pushes the sweep's
  verdict through the configured webhooks - ONE message per sweep, the worst offenders
  named worst first, into the same channel the runs already report to. Warning level and
  above by default; `--notify-always` adds the all-clear heartbeat. Endpoint subscriptions
  apply, the exit code is unaffected, and Task Scheduler plus this flag is backup
  monitoring for an entire estate with no agent, no dashboard and no PowerShell module
- **9lives backup: the other half of the orchestrator** (#300). The CLI could restore,
  rehearse and judge but not take a backup - and the scriptable case is everywhere: the
  pre-change safety snapshot with the restore in the same tool, the copy-away before risky
  maintenance, the nightly extra full to a second container. Full, differential and log
  (`--type`), striping (`--stripes`), blob container or share, and the app's rules travel:
  COPY_ONLY by default and LOUD in stderr AND the script header when disabled, destinations
  from the same layout rules every browser reads, the blob credential preflight before the
  first statement, generate-only without `--execute`, and the same history receipts and
  webhooks as every other execution verb
- **The CLI restore relocates** (#299): `--relocate` moves every file to the target's own
  default data and log directories keeping its original name, and `--data-path` /
  `--log-path` place them explicitly - mirroring the app's WITH MOVE control. The freshly
  provisioned VM rarely has the source server's drive layout, and the recorded paths used
  to fail with directory-not-found mid-run, after WITH REPLACE had already dropped the
  target. With relocation in play the disk-space preflight judges the volumes the files
  actually LAND on. The three-line Terraform template now survives any VM shape

- Rehearsal notifications now name the database being PROVEN rather than the scratch copy it
  was proven on - "Rehearsal MyDb", not "Rehearsal MyDb_rehearsal_20260810_0930"
- The Exposure dashboard sweeps itself on first visit - arriving at an empty screen that needs a
  button press first is the screen failing its own question. Refresh stays deliberate after that
- The copy screen now checks the version direction at script generation - its restore half runs
  outside the restore preflights, so a 2025 source aimed at a 2022 target used to fail with
  error 3169 only AFTER the backup half had run. The warning says plainly that the copy cannot
  work and which way it can
- **The Proven column now carries the measured RTO** - "Proven: 08-09 21:30 (took 14m 00s)".
  The rehearsal times the real restore plus CHECKDB, which is the number RTO conversations are
  otherwise made up from, and the dashboard shows it beside the exposure it answers
- **The exposure dashboard's rows act, not just alarm** - "Open in Restore" on every reachable
  row jumps to the restore screen with that server's backup history loaded and the database
  selected, one click from seeing to acting. From there, Rehearse proves it. A red row's
  distance to being acted on should be one click, or the dashboard is a wall of guilt rather
  than a to-do list
- **Scheduled rehearsals: the proof renews itself.** The rehearsal wrapped as a disabled,
  unscheduled Agent job - add a weekly schedule and every run restores the chain to a stable
  scratch name, proves it with CHECKDB, and drops it, receipts accumulating in the job history.
  Each run clears its own previous leftover first, which is safe precisely because the name
  belongs to the rehearsal alone; a failed run still retains its scratch copy as evidence until
  the next run has been seen (#259)

### Added

- **The CLI: 9lives.exe** (#63). The restore engine from a terminal - the same chain
  calculation, striped-set grouping and script generation the app runs, against the same
  configured containers, servers and credentials. Five read-only verbs: `list` (what a source
  holds), `points` (the timeline as text or JSON), `script` (validated restore T-SQL to
  stdout, STOPAT and marked-transaction targets included), `validate` (is every chain intact -
  the exit code is the answer), and `exposure` (the dashboard's sweep with the worst level as
  the exit code, so a scheduled task turns quiet log-backup silence into a red pipeline).
  Distinct exit codes for warn, broken, could-not-answer and usage, because a pipeline
  branches on numbers, not prose
- **The CLI executes - carefully.** `9lives restore` and `9lives rehearse` run against a
  target server, behind the safety defaults the CLI was designed around: nothing runs without
  `--execute`; overwriting an existing database is said with `--with-replace`, its own flag
  that `--force` cannot substitute for; and the same preflights the app fires - can the target
  read the files, does the version direction work (error 3169), is the TDE certificate there
  (error 33111) - refuse before anything is dropped, with `--force` as the deliberate,
  evidence-only override. A generate-only invocation still runs the preflights and carries
  their verdict in its exit code, so a pipeline can rehearse its own DR step for free.
  Executed runs land in the same History the app lists and notify the same webhooks -
  `9lives rehearse --execute` on a schedule is nightly proof with receipts, no SQL Agent
  required
- **The CLI documents itself**: `9lives help` for the overview, `9lives help restore` (or
  any verb, or `--help` after one) for the full page - every option explained, the behaviour,
  the per-verb exit codes, worked examples. The reference lives inside the exe, where it
  cannot drift from the parser it describes, and a test holds each page to the verb's own
  option list - an undocumented flag or a documented-but-refused one both fail the build
- **Provisioning from nothing**: `add-server` and `add-container` create the configuration
  from the command line on a machine the app has never run on - a Terraform clone, a DR
  bubble. Validated by default (the server asked its version, the container asked to answer
  with exactly the recorded SAS - a SAS looks right for weeks after it expired), converging
  on re-run, secrets in the Credential Manager and never echoed, with
  `NINELIVES_SQL_PASSWORD` / `NINELIVES_SAS` as the script-friendly alternative to flags.
  A point-in-time clone is three chained lines whose only variable is the moment

- **Webhook deliveries can take a route** (#316): the machine's own proxy settings (the
  default, and what always happened), no proxy at all, or an explicit proxy URL with
  credentials - the password in Windows Credential Manager like every secret. One setting
  beside the webhook list, used by every send: the app's runs, the test button, and the
  CLI's alike. The route travels in an export as addressing only; the secret asks again on
  the new machine
- **Webhook URLs get the secret treatment** (#317): pasted, then committed with an explicit
  Save - and from that moment obfuscated, stored in Windows Credential Manager, out of
  config.json, and never displayed again. Replacing one means saving a new one. Deliveries
  hydrate the URL at send time on clones; existing configs keep working from their in-file
  URL until the row is next saved, which migrates it
- **The restore workflow asks WHERE, as a step** (#318): step 2 is SELECT TARGET, offering
  the saved server list right in the flow. Connecting on the SQL Servers screen first shows
  the step already answered - and still changeable - instead of asking again; with nothing
  connected, the answer no longer waits to surface as an execute-time error. Changing the
  source keeps the target: it is an independent decision, and it survives backtracking
- **The database list waits for an instance** (#319): with several instances in the loaded
  backups, the DATABASE dropdown stays empty and disabled until the instance is chosen -
  two servers both backing up a database called Sales are the everyday DR pair, and a mixed
  list meant guessing whose history the timeline would show. One instance answers its own
  filter automatically; the database choice itself always stays with the user

### Changed

- The engine - every service and model - now lives in NineLives.Core, a library with no UI
  framework behind it. Nothing a user can see is different; this is the load-bearing wall for
  the command-line front end (#63), where the same chain calculation, the same preflights and
  the same script generation must run without a window ever existing

### Fixed

- **Two processes cannot lose each other's receipts** (#298). The restore history's
  read-modify-write was serialised in-process only - and the CLI made it two processes: a
  scheduled 9lives rehearse writing its receipt while the app is open was a race where the
  last writer silently dropped the other's entry, and the receipt that vanished is the
  proof the rehearsal existed - the exact thing the Proven column reads. Writers now hold a
  sidecar lock file across the read-modify-write, queueing in a short retry loop instead of
  clobbering; the lock deletes itself on close, so a crash cannot orphan it
- **The CLI checks the restore fits before anything is dropped** (#297). The preflight
  ladder's fifth rung: FILELISTONLY sizes against the target's own free space, per volume,
  judged by the same check the app uses - including the volume the target does not have at
  all, which is a MOVE problem rather than a free-space problem. Space is evidence, so
  --force downgrades the refusal to a loud warning; the shortfall is named either way. The
  CLI is the front end most often aimed at a freshly provisioned VM with small disks, and a
  restore that runs out of disk fails AFTER WITH REPLACE has dropped what it was replacing
- **Every run closes on the channel** (#295). A failed SINGLE-database backup now sends the
  close of the run with its duration, exactly as the multi-database path always has - the
  channel used to hear "Started" and then silence forever, which teaches people the channel
  is unreliable. The copy's six-notification lifecycle is pinned end to end: started,
  succeeded with duration, each half's failure named, and a refused copy never claims to
  have started
- **Names cannot smuggle statements past the person reading the script** (#294). sysname
  permits control characters and line breaks, and the restore target is free text - a name
  containing a newline used to terminate the generated script's header comment and hand the
  remainder to the server as executable text. Header comments now flatten control
  characters; everywhere else the name already travels as data. And names that came FROM
  the server - the backup's database, the encryption certificate, an orphaned user - are
  quoted exactly: a database genuinely named [Archive] stays [Archive] instead of
  round-tripping through the typed-name unwrapping into a different database
- **Three truths on the backup screen** (#293). The Agent-job handover names a backup job
  "NineLives backup ..." instead of introducing itself as a restore, and multi-select names
  all of it - "NineLives backup 3 databases" rather than an empty database in the
  description. Verify now proves the run that happened: WITH CHECKSUM exactly when the
  statements said it, captured with the run, so ticking the box after a no-checksum backup
  no longer fails a perfectly fine backup (and unticking no longer silently degrades the
  verify). And stripes is clamped to SQL Server's own 1-64 device range, so a typo of 640
  cannot become a statement the server rejects after the arm-and-confirm
- **The webhook settings tell the truth** (#292). The Test button on an endpoint with all
  three moments unticked - the natural way to pause one - now says it is subscribed to
  nothing and will never fire during a run, instead of promising a channel that stays
  silent forever. A URL typo is refused at Save with the reason on the row - it used to be
  accepted and then fail on every run, with the failures visible only in the operation log.
  And every delivery attempt, test or real, stamps the endpoint with when and how it went:
  "Last delivery 2026-08-10 21:14: FAILED - 404" on the row is the difference between a
  webhook that works and one that broke weeks ago and looked identical
- **The connected server survives its own lifecycle honestly** (#291). Deleting it is
  recognised by identity, not by a caption captured at connect time - renaming the connected
  server and then deleting it used to leave the status bar claiming "Connected to X" while
  the restore screen still held the deleted object as its target. Editing the connected
  entry's address, auth mode, login, encryption or password now DROPS the connection with an
  explanation - the old settings were proven, the new ones are not - while a rename alone
  keeps it. And renaming onto an existing name is refused, exactly as creating one is, so no
  two entries can share a caption
- **The busy strip and the taskbar flash treat all three runs alike** (#289). The global
  busy indicator - which exists so "is it doing something?" does not depend on scroll
  position - now lights for backups and copies, the two longest-running screens it ignored.
  And the taskbar attention flash on finishing behind another window, written for exactly
  the alt-tabbed-away case, now fires for a finished backup and a finished copy as it always
  did for a restore
- **The handoff tells the truth** (#288). Clicking "Open in Restore" while a restore is
  RUNNING is refused with an explanation instead of wiping the run's chain and timeline off
  the screen - the handoff may not do what the regenerate button already refuses. What
  arrives is exactly what the browser showed: extra containers ticked on an earlier visit
  are unticked, so the chain cannot assemble from backups nobody looked at. And the instance
  filter matches by identity the way every other server comparison does - a case or naming
  difference between msdb's name and a path-inferred one no longer silently drops the
  filter, and a filter that genuinely cannot apply is SAID on the status line rather than
  quietly answering a different question
- **The exposure sweep answers to the user** (#287). It can be STOPPED - a Stop button while
  it runs, and Esc reaches it - where a dozen servers with several unreachable used to mean
  minutes of timeouts with no exit; stopping keeps the previous sweep's rows, and is never
  dressed up as a server failure. A sweep that fails says so on the dashboard instead of
  dying unobserved behind the first-visit auto-sweep, and that auto-sweep now runs once per
  session as promised: "has swept" is a real flag, no longer inferred from an empty row list
  that re-triggered a full estate sweep on every visit whenever the answer WAS empty. The
  "as of" clock carries the date, an unreachable row is a property rather than a magic
  caption, and the one-click handoff hands the restore screen the exact connection whose
  sweep produced the row - two entries for the same instance with different credentials no
  longer collapse into whichever came first
- **The browser no longer mixes two sources' answers** (#286). Changing the container,
  medium or source server mid-listing cancels the in-flight load instead of letting the
  stale result land under the new source's name - and the set grouping runs with the
  container that was ASKED, not a re-read of the selection, so a mid-flight switch can no
  longer skew filename-less timestamps by the other container's time zone. A round trip to
  another screen and back keeps the listing (the refresh compares identities before
  reassigning selections), F5 now RELOADS the listing instead of emptying it, a cancelled
  reload keeps the previous complete answer whole - rows and restore button in agreement -
  the remembered container survives a rename by matching on Id, and both media report the
  same thing in the status line: the source's total databases
- The SQL Servers and Blob Storage screens now MERGE their saves into the configuration on
  disk instead of replacing it wholesale. Importing a config (or adding entries with the CLI)
  and then saving anything on either screen used to silently delete everything added since
  the screen loaded - the worst case being import, then Connect, and every imported server
  gone. Both screens also catch up with outside additions on navigation, and only deletions
  made on the screen itself delete (#276)
- The CLI's endings are trustworthy now (#296). Completion webhooks no longer race process
  exit - the execution verbs drain pending deliveries (bounded, ten seconds) before
  returning, so the success and problem messages actually arrive from a process that exits
  the moment the run ends. And Ctrl+C mid-run is a story, not a kill: the receipt says
  Cancelled, the channel is told, and the exit code is the conventional 130 so a wrapping
  script can tell an operator's interruption from a failure
- The copy's generation-time checks answer for their inputs, not for keystrokes (#285).
  Typing in the target-name box used to launch six round trips across BOTH production
  instances per character, the racing sweeps could clear each other's warnings, and a quick
  Confirm ran with the panels still blank. One serialised sweep per input change now -
  version first, previous sweep cancelled on re-entry so a stale answer can never erase a
  fresh warning - and the run waits for the verdicts before anything executes. A credential
  refusal before the run also finally uses the outcome the enum always had for it
- Closing the app on the launch mode-cards no longer erases the remembered screen (#290).
  The cards show at every start, so opening the app and closing it without clicking through
  wiped the last-screen memory and the next launch forgot where its owner works. No choice
  made now means nothing recorded - not "record nothing"
- BACKUP TO URL and the copy now ask the credential question the restore always asked
  (#284): the backup checks the source instance before its first statement, the copy checks
  BOTH ends before the source is read at full speed - a missing credential is created, an
  existing usable one (managed identity included) is left strictly alone, and an existing
  unusable one refuses with the way out named, instead of Msg 3201 after the arm-and-confirm.
  The decision now lives in one Core service all three screens share
- Copy failures get the restore screen's follow-through (#283): a Stop pressed between the
  halves is reported as the cancellation it is - not as a share-permission failure sending
  somebody to chase an ACL that is fine; a failed restore half now says what state the
  target is in (RESTORING, RECOVERY_PENDING, single-user) with the statements that get it
  out; and a copy that worked runs the orphaned-user scan - a copy to a different server
  being the canonical orphaned-login scenario. The copy's notification lifecycle gains its
  first tests while here
- A copy onto the same instance is refused up front, with the way out named (#282). The
  copy's restore carries no MOVE clauses - right for a different server, fatal on the same
  one, where the files land on the live source's own paths and SQL Server fails with
  Msg 1834 AFTER the backup half has run. The refusal points at the Restore screen's full
  WITH MOVE control; automatic relocation for this shape rides with the generation-time
  check rework (#285)
- "All user databases" means it now (#279): the database list the backup screen ticks and
  the copy screen offers is filtered to online user databases - no more tempdb (which cannot
  be backed up at all), no master offered for copying, no RESTORING or OFFLINE databases
  guaranteeing a red summary on the one-click patch-night path. Same predicate the exposure
  query always used, now in one more place it belonged
- Every long operation owns its cancellation (#281). Three screens shared one token across
  operations that can overlap, so the List databases button silently cancelled a running
  backup or copy, and a finished operation disposed the survivor's token - the container-wide
  audit could die with "Cannot access a disposed object" while its Stop button vanished. One
  instance per operation now (the rule the restore execution always kept), the list buttons
  are dead while a run is in progress, and starting an inventory operation cancels the others
  explicitly rather than through a shared disposal
- The copy runs the scripts it showed, not the ones on screen later (#280). The view stayed
  editable while the halves ran, and a keystroke in the target-name box mid-backup
  regenerated the restore half with fresh timestamped destinations - so the copy then
  restored from a file that was never written, and the outcome text pointed at the wrong
  path. The run now snapshots both scripts and the destination list at the moment of
  consent, the same immutable-run-record medicine the restore screen takes
- A generated script no longer survives the change that invalidated it (#278). On the backup
  and copy screens, an input change that makes regeneration impossible now CLEARS the script,
  the destinations and the run button instead of leaving the old statements displayed and
  runnable - switching servers after generating used to leave a script for one instance
  executable against another, with the previous server's multi-select ticks and certificate
  list still standing
- Imports no longer destroy and no longer lie (#277): the webhook list refreshes immediately
  after an import, so the next row edit cannot erase what was just imported; a matching
  container keeps the SAS pasted into its local URL (the same protection webhook URLs always
  had), unless the base URL changed - a SAS is scoped to what it was issued for; the summary
  counts webhooks; and a file without a format version - an empty JSON object used to import
  as "Nothing new" - is refused as not an export, as is a file from a newer format. Theme and
  the update/log preferences are no longer exported, because the import never applied them -
  a file must not carry what the import discards
- Find marks now asks the SOURCE instance's msdb on shared-path and ad-hoc restores - marked
  transactions are recorded where they ran, so asking the target answered "no marks" when the
  truth was "wrong catalogue". Blob still asks the connected server, which is the only
  catalogue that medium has (#268)
- A combo's closed box now shows the same text as its open list. The custom combo template
  dropped the template selector that DisplayMemberPath works through, so combos bound to
  objects rendered their raw type name once closed - the marked-transactions picker being the
  first to show it in daylight

## [1.5.0] - 2026-08-10

### Added

- **Export a restore runbook** - one self-contained Markdown document per restore point: the
  chain file by file, the prerequisites in the order the worst day needs them (credential,
  TDE/encryption certificates by thumbprint, disk space), the exact script, what to do when it
  stops part-way, and what finishes the job. Readable with no SQL tools installed, printable for
  the change-board pack, committable to the DR repo (#240)
- **The Exposure dashboard: how much data would be lost, right now.** Every user database on
  every configured server, judged from its own msdb - the derived loss window ("everything after
  14:32 is gone - up to 47m of work"), traffic-lit worst-first. The silent failures are the
  loudest: never backed up, FULL recovery with no log backups ever, chains that stopped days
  ago - and a server that will not answer is itself an alarm row, because unknown is not the
  same as fine. Rehearsal receipts join in as a "Proven" column: arithmetic says what could
  restore, only a rehearsal says it does (#239)
- **Restore rehearsal: prove a backup restores, with a receipt.** One button restores the chosen
  chain to a scratch database, proves the data with DBCC CHECKDB, and drops the scratch copy -
  the History entry records what was proven, when, and how long it took. Safety by construction:
  a generated name refused if it exists, never WITH REPLACE, every file relocated to
  scratch-named files, and the guarded DROP runs last so any failure retains the scratch copy as
  evidence. Rehearsals announce themselves through the run notifications (#238)
- **Restore to a marked transaction (STOPATMARK/STOPBEFOREMARK)** - the sharper point-in-time
  tool: plant a mark with BEGIN TRANSACTION ... WITH MARK before risky work, and afterwards the
  restore target is the transaction itself, found from msdb's own logmarkhistory, not a clock
  time reconstructed from chat messages. Stops just BEFORE the mark by default (the deployment
  never happened); the mark and the clock time are mutually exclusive on screen, and a mark with
  no log chain refuses with the reason (#243)
- **The retention referee: what would an age-based deletion rule actually do?** Report-only,
  over the loaded backups: what a keep-N-days rule keeps, what it can safely delete (with the
  bytes reclaimed), what must survive its own age because kept restores depend on it - the base
  full outside the window, the newest differential under it, and the bridge logs that carry the
  chain to the window's edge - and what is already broken. Before a lifecycle rule finds out via
  error 3136 (#241)
- **Run notifications to Teams, Slack, or any JSON endpoint** - a message when a backup, restore
  or copy starts, when it finishes, and whenever there is a problem, including each database's
  failure in a multi-database backup AT the moment it happens. Teams gets a MessageCard (works
  with both incoming-webhook connectors and Power Automate flows), Slack gets Block Kit, and the
  generic format is plain JSON fields. Configured in Settings with a per-endpoint test button;
  webhook URLs never leave the machine in a configuration export (#242)

- The backup metadata inspector now states which SQL Server version took the backup and what
  protects it - TDE, backup encryption, or "Not encrypted", because absence is information too
  (#222)

- **The finishing panel's actions show progress and speak their outcome.** DBCC CHECKDB (and any
  long panel action) drives a progress bar from the server's own percent_complete, and the result
  stays on the panel - "CHECKDB completed and found nothing wrong" - instead of scrolling away in
  the console. Failures and cancellations stay visible the same way
- The orphaned-user scan crashed with a collation conflict when the restored database's collation
  differed from the server's - both sides of the login-name comparison are now forced to one
  collation. Found in the field on the first cross-collation restore

### Changed



- **The Generate Script button is gone** - the script has built itself live on every option
  change for a while, which made the button a ritual with no effect. When the pane is empty it
  now says why, passively, where the script would be ("Enter a target database name and the
  script appears here"), instead of a dialog per keystroke. Ctrl+G retires with it
- **Every restore option is available in every mode.** Point-in-time, relocating files (WITH
  MOVE), the advanced WITH options and additional containers no longer hide behind Standard or
  Pro: the modes narrow which screens exist, never which restore options do. WITH MOVE is needed
  most on restores to a different server - the Basic scenario itself - and hiding it made Basic
  restores fail with directory errors the wider modes would not have hit

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
