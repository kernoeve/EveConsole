# Contributing to EVE Console

Thanks for taking an interest. This is a small project with one maintainer, so the process is
short — but there are a few things that are genuinely easy to get wrong, and they are all written
down below rather than left to be discovered in review.

Questions are welcome on [Discord](https://discord.gg/H6NaAjJMar). Bugs go in
[Issues](https://github.com/kernoeve/EveConsole/issues).

---

## How changes get in

Everyone works from a fork. Only the maintainer has push access to this repository, so the flow is
fork → branch → pull request.

```bash
# 1. Fork on GitHub, then clone YOUR fork
git clone https://github.com/YOUR-NAME/EveConsole.git
cd EveConsole
git remote add upstream https://github.com/kernoeve/EveConsole.git

# 2. Branch off develop -- see the warning below
git fetch upstream
git checkout -b feature/your-thing upstream/develop

# 3. Work, commit, push to your fork
git push -u origin feature/your-thing
```

Then open the pull request on GitHub.

### ⚠️ Two places `main` is the default and should not be

This is the mistake everyone makes first, including people who have read the paragraph above.

1. **A fresh clone lands you on `main`.** `git checkout -b … upstream/develop` in the snippet above
   is what avoids it. Branch off `main` and your PR carries `main`'s release merge commits along
   with your own work.
2. **The PR base defaults to `main`.** GitHub preselects the repository's default branch. Change it
   to `develop` in the dropdown before you open the PR.

It matters because `main` is the release branch: every merge into it triggers a build that is
tagged, published, and **auto-updates every installed copy of the app**. `develop` is where work
lands; releases are cut by a periodic `develop` → `main` pull request.

### Branch names

`feature/…`, `fix/…`, `chore/…`, `docs/…`. Nothing is enforced, but it keeps the history readable.

### Commit messages

A sentence saying what changed, often with the area first and the reason after — for example:

```
SDE: undo the import the way each engine can afford
Overview: drop the worklist expansion, which had stopped rendering
Read the release webhook from an environment secret, not a repository one
```

Not Conventional Commits. If the *why* is not obvious from the diff, put it in the body — this
codebase leans hard on explaining reasoning, and the commit log is part of that.

---

## What happens to your pull request

**CI runs `dotnet build -c Release` for `win-x64` and `linux-x64`.** Both must pass.

⚠️ **There is no test suite.** A green check means the code compiled on both platforms — nothing
more. Say in the PR what you actually exercised by hand, and on which database backend; that is the
only evidence a reviewer has.

**Every pull request needs an approving review from the maintainer.** You will not be able to merge
it yourself even after approval — that is expected, not a permissions bug.

**On your first contribution, CI will not start until the maintainer approves the workflow run.**
GitHub holds fork workflows from first-time contributors. If your PR sits with no checks, that is
why; it is one click at the other end.

---

## Two rules that are easy to break

Neither is obvious from reading the surrounding code, and both have caused real bugs.

### UI state is local, never the database

⚠️ `AppPreferences` is a table **in** the database. Several clients can share one PostgreSQL
server, so anything stored there is shared between them — collapsing a group on the desktop
collapsed it on the laptop, and declining an update on one machine hid the notice on another.

Anything that is *how a screen was left* — theme, UI scale, collapsed groups, last selected view —
belongs in `UiState` (which writes to the local `config.json`). Settings **about the data** — what
to build, what to price, what to poll — stay in the shared table where every client sees the same
answer.

The test: would two people, or one person at two machines, reasonably want different answers? Then
it is local.

### Schema changes must be additive

⚠️ **The schema only ever moves forward, and there is no way back.** There is no migration
framework and no down path: new columns are added to an existing database in place at startup, and
the app refuses to start — fatally, with nothing to click past — against a database that a newer
build has already changed.

So a dropped, renamed or repurposed column is not a change a user can back out of. Once they have
run the build that made it, the previous build will never open their database again, and their data
is wherever that change left it.

Add new columns instead. Give every `NOT NULL` column a `DEFAULT`, or a fresh install that builds
its schema with `EnsureCreated` fails on the first insert — which only shows up on a clean machine,
never on a developer's own database that got the column by `ALTER`.

(Several clients can share one PostgreSQL database, but they must all be on the same build; the
same check enforces that.)

---

## Building and running

See [Getting started](README.md#getting-started) in the README for requirements and the build
commands. In short: .NET 9 SDK, plus the system **VLC** package on Linux — it is the one dependency
the build cannot carry itself.

---

## Licence

EVE Console is [GPL-3.0](LICENSE). Contributions are accepted under the same licence.
