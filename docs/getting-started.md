# Getting Started

## Requirements

- Windows 10 or 11.
- An EVE Online account (you'll authorize characters via EVE's official Single Sign-On).

> Linux builds are planned for the 1.0 release. Today the app is built and tested on Windows (`net9.0-windows`).

## Download & install

Grab the latest build from the project's [Releases page](https://github.com/kernoeve/EveCortex/releases/latest). The links below always point at the **newest** release, so they don't go stale:

- **[Installer — `EveCortex-win-Setup.exe`](https://github.com/kernoeve/EveCortex/releases/latest/download/EveCortex-win-Setup.exe)** — installs Eve Cortex (adds Start-menu and uninstall entries), then launch it.
- **[Portable — `EveCortex-win-Portable.zip`](https://github.com/kernoeve/EveCortex/releases/latest/download/EveCortex-win-Portable.zip)** — no install; extract it anywhere and run `EveCortex.exe`.

Both are the same app and **both keep themselves up to date automatically** (see below), so which you choose is a matter of preference — the installer if you'd like it integrated into Windows, the portable ZIP if you'd rather keep everything in a single folder you can move or delete.

Your data is stored locally at `%LOCALAPPDATA%\EveCortex\EveCortex.db`. Nothing is uploaded anywhere — the app talks only to CCP's ESI API to refresh your data.

## Staying up to date

You normally won't need to download the app again. Whether you installed it or run the portable build, Eve Cortex **checks for updates on startup and once an hour**, and when a new version is available it **prompts you inside the app** — accepting downloads the update and restarts to apply it. Declining won't nag you again until the *next* version.

You can manage this under **Settings** (the **⚙** gear button, top-right) → **Updates**:

- **Automatically check for updates** — on by default; untick to only check manually.
- **Current version** / **Latest version** — what you're running vs. what's available.
- **Check Now** — check on demand.
- **Update Now** — appears when an update is available; downloads and restarts.

!!! note

    Automatic updates apply to the released builds (installer **and** portable ZIP). Only a build you run **from source** can't self-update — the Updates tab shows *"n/a — not an installed build,"* and you update it by pulling and rebuilding.

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

The first time you launch Eve Cortex, it starts downloading the EVE game data it
needs (the Static Data Export and Hoboleaks data) **in the background**. A short
**Welcome** dialog explains this — item lookups, market pricing, and industry tools
fill in over a few minutes as the download completes, and you can watch progress on
the **SDE** tab in Settings.

Click **Get Started** on that dialog and the **Settings** window opens automatically
on the **ESI Tokens** tab. That's where you authorize your characters and
corporations — click **Add Character** to log in your first character through EVE's
Single Sign-On, as described next.

You can reopen ESI Tokens any time from **Settings** (the **⚙** gear button,
top-right); it's the first tab.

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

  Welcome dialog:
  ![Welcome dialog](images/welcome.png)

  ESI Tokens settings tab:
  ![ESI Tokens](images/esi-tokens.png)
-->
