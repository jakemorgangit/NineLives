# Changelog

All notable changes to Nine Lives are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

Release notes on the [Releases page](https://github.com/jakemorgangit/NineLives/releases) go into
more detail on the user-facing changes; this file is the short history.

## [Unreleased]

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
