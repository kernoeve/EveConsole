<!--
Thanks for the contribution. CONTRIBUTING.md has the detail; this is just the short check.
-->

## What this changes

<!-- What it does, and why. If the "why" is not obvious from the diff, it belongs here. -->

## How it was tested

<!--
⚠️ CI only runs `dotnet build` — there is no test suite, so a green check means it compiled.
What you exercised by hand is the only real evidence. Which screens, which backend?
-->

- [ ] SQLite
- [ ] PostgreSQL
- [ ] Windows
- [ ] Linux

## Checks

- [ ] Based on `develop`, and the PR base above is set to `develop` (**not** `main` — a merge into
      `main` cuts a release that auto-updates every install)
- [ ] No UI state written to `AppPreferences` — theme, scale, collapsed panels and the like go in
      `UiState` / `config.json`, because the preference table is shared between clients
- [ ] Schema changes are additive: no column deleted, renamed or repurposed, and every new
      `NOT NULL` column has a `DEFAULT`
- [ ] No credentials, tokens, connection strings or personal paths in the diff
