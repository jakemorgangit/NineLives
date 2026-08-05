## What does this PR do?

<!-- A short description of the change and the motivation for it. -->

<!-- Target `dev`, not `main` — see CONTRIBUTING.md. `main` only receives release PRs from `dev`. -->

## Checklist

- [ ] Targets `dev` (release PRs from `dev` → `main` are the exception)
- [ ] `dotnet build NineLives.sln -c Release` succeeds with **no new warnings**
- [ ] `dotnet test` passes locally
- [ ] New logic in `Services/` or `Models/` comes with tests
- [ ] Any generated T-SQL goes through `Services/TSql.cs` (`QuoteName` / `EscapeLiteral`)
- [ ] UI changes include a screenshot (dark theme)
- [ ] No credentials, SAS tokens, or real server/database names in code, comments, or images
