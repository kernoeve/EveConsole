# Getting Started

## Requirements

- Windows 10 or 11.
- An EVE Online account (you'll authorize characters via EVE's official Single Sign-On).

> Linux builds are planned for the 1.0 release. Today the app is built and tested on Windows (`net9.0-windows`).

## Download & install

Grab the latest build from the project's [Releases page](https://github.com/kernoeve/EveCortex/releases/latest). The links below always point at the **newest** release, so they don't go stale:

- **[Installer — `EveCortex-win-Setup.exe`](https://github.com/kernoeve/EveCortex/releases/latest/download/EveCortex-win-Setup.exe)** *(recommended)* — installs the app and enables automatic updates.
- **[Portable — `EveCortex-win-Portable.zip`](https://github.com/kernoeve/EveCortex/releases/latest/download/EveCortex-win-Portable.zip)** — no install; unzip and run `EveCortex.exe`.

Run the installer, then launch **Eve Cortex**.

!!! tip "Recommended: the installer"

    Use the installer unless you have a reason not to — only installed builds update themselves (see below). The portable build never touches your system, but you'll have to re-download it by hand for each new version.

Your data is stored locally at `%LOCALAPPDATA%\EveCortex\EveCortex.db`. Nothing is uploaded anywhere — the app talks only to CCP's ESI API to refresh your data.

## Staying up to date

You normally won't need to download the app again. An installed build **checks for updates on startup and once an hour**, and when a new version is available it **prompts you inside the app** — accepting downloads the update and restarts to apply it. Declining won't nag you again until the *next* version.

You can manage this under **Settings** (the **⚙** gear button, top-right) → **Updates**:

- **Automatically check for updates** — on by default; untick to only check manually.
- **Current version** / **Latest version** — what you're running vs. what's available.
- **Check Now** — check on demand.
- **Update Now** — appears when an update is available; downloads and restarts.

!!! note

    Automatic updates apply to the **installer** build only. The portable ZIP and source builds show *"n/a — not an installed build"* and must be updated manually by grabbing the latest release.

## Building from source

If you'd rather build it yourself (or want to contribute), you'll also need the
[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0):

```powershell
git clone https://github.com/kernoeve/EveCortex.git
cd EveCortex
dotnet restore
dotnet run
```

Source builds don't auto-update — pull and rebuild to get newer changes.

## First launch

On first launch you have no characters yet. Click **Log in with Eve**, which takes
you to EVE's official Single Sign-On (SSO) page in your browser — log in and
authorize the character. You can add more characters (and corporations) any time
from the ESI Tokens settings below.

## Managing ESI tokens (characters & corporations)

Everything the app knows comes from ESI tokens you authorize. Manage them under
**Settings ▸ ESI Tokens**, which has a **Characters** list and a **Corporations**
list, each with **Add**, **Update**, and **Remove** buttons.

### Adding a character

1. Under **Characters**, click **Add**.
2. Pick which **scopes** (permissions) to grant — they're grouped by category. Only
   the data you grant can be pulled.
3. Continue to EVE's SSO page in your browser and authorize the character.
4. The character appears in the list with its status and granted scopes. Selecting
   it shows when it was last authenticated and exactly which scopes it has.

**Update** re-runs the SSO flow for the selected character — use it to add or change
scopes, or to refresh an expired token. **Remove** deletes that character's token
and data.

### Adding a corporation

1. Under **Corporations**, click **Add**.
2. Pick the corporation scopes to grant.
3. In the browser, **log in as a character who holds the required corporation roles**
   (Director / Accountant) — corporation ESI data isn't available without them. The
   app uses that character only to resolve and authorize the corp; the corp's token
   is stored on the corporation itself.
4. The corporation appears in the list (ticker, name, status).

### Personal corporations

A corporation you own is a **personal corporation** — its activity and assets should
count as *yours*. Select a corp and tick **Personal Corporation** to mark it (a gold
dot flags personal corps in the list).

!!! info "What 'personal' changes"

    Personal corps count toward your **individual net worth**, and their activity and
    assets are treated as part of your own. Alliance or employer corporations — ones
    you've added for visibility but don't own — are treated as separate entities and
    are kept out of your personal totals.

## How data stays fresh

While Eve Cortex is running it performs background ESI pulls on a schedule. Most
screens update automatically as new data arrives, so you can leave it open in the
background. Some data (like market price history) is cached and refreshed on longer
intervals to respect ESI limits.

## Recommended next steps

Most tools depend on a little configuration first:

1. **[Configure a market](configuring-markets.md)** so the app knows where prices come from.
2. **[Set up an industry park](industry-parks.md)** so build costs and the Production Calculator are accurate.
3. Optionally, **[set up the AI agent](ai-agent-eden.md)**.

<!--
  SCREENSHOT SLOTS (add files to docs/images/, then uncomment):

  Login screen:
  ![Log in with Eve](images/login.png)

  ESI Tokens settings tab:
  ![ESI Tokens](images/esi-tokens.png)
-->
