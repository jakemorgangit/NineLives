# Contributing to Nine Lives

Thanks for taking an interest. This is a tool people point at production databases during
incidents, so the bar for correctness is high — but the codebase is small and the setup is quick.

## Branching model

**`dev` is the default branch and where all work lands. `main` is the released code.**

```
feature branch  ──PR──►  dev  ──release PR──►  main  ──tag──►  GitHub Release
```

- **Work targets `dev`.** Branch from `dev`, open your PR against `dev`.
- **`main` always matches the published release.** Anyone cloning `main` gets code that
  corresponds to the executable on the Releases page — which matters for a restore tool, where
  "which version am I actually running?" is a question people ask mid-incident.
- **Releases are a `dev` → `main` pull request.** A check enforces that `<Version>` in
  `src/NineLives/NineLives.csproj` has not already been published as a release — so a version
  number can never cover two different builds. Publishing a GitHub Release then builds, tests,
  and attaches `NineLives.exe` with checksums automatically.

Branch naming is loose, but `fix/<issue>-<slug>` and `feat/<issue>-<slug>` keep things scannable.

## Building and testing

```bash
git clone https://github.com/jakemorgangit/NineLives.git
cd NineLives
dotnet build
dotnet test
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) and Windows (it is a
WPF app).

### Live SQL Server tests

Some tests exercise real SQL Server behaviour that cannot be mocked meaningfully — for example
that a failed restore actually throws rather than reporting success, and that a credential name
cannot break out of its quoting. They are skipped unless you point them at an instance:

```powershell
$env:NINELIVES_TEST_SQL = ".\SQLEXPRESS"    # or "(localdb)\MSSQLLocalDB"
dotnet test
```

They create and drop only their own objects and clean up after themselves. CI starts LocalDB on
a best-effort basis, so they run there too.

## What CI checks

Every PR runs on `windows-latest`:

1. `dotnet build -warnaserror` — **the build is warning-free and must stay that way**
2. the full test suite
3. a self-contained single-file publish, proving the shipping artifact still builds

## Expectations for a change

- New logic in `Services/` or `Models/` comes with tests. These are the parts that decide which
  backups form a restore chain, so they carry the most risk.
- **All generated T-SQL goes through `Services/TSql.cs`.** Identifiers use `QuoteName`, string
  literals use `EscapeLiteral`. Do not hand-roll escaping.
- If you change restore-chain logic, say in the PR what a DBA would see differently.
- No credentials, SAS tokens, or real server/database names in code, comments, tests, or
  screenshots. Use `MyDb`, `SRV01`, `mycluster01$My-AG1`.

## Reporting bugs

Include the app version (Help → About), Windows and SQL Server versions, your backup layout
(path pattern or Ola naming), and — where relevant — the generated script with identifying
details replaced.

For anything security-sensitive, please follow [SECURITY.md](SECURITY.md) rather than opening a
public issue.
