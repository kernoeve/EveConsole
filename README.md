<p align="center">
  <img src="media/banner.png" alt="EVE Console" width="100%">
</p>

<p align="center"><em>A local-first, free, open-source desktop companion for EVE Online.</em></p>

<p align="center">
  🌐 <strong><a href="https://eveconsole.com/">Website</a></strong> ·
  📖 <strong><a href="https://docs.eveconsole.com/">Documentation</a></strong> ·
  💬 <strong><a href="https://discord.gg/H6NaAjJMar">Discord</a></strong>
</p>

**EVE Console** is a desktop companion app for [EVE Online](https://www.eveonline.com/), running locally on the players system.  Ultimately I run many tools for my day to date activities in Eve (i.e., Ravworks, jEveAssets, Excel, etc.), and was looking for a single tool, where all of my data stayed local, and was completely free with source.  While the tool does not do everything today, it does the things I need it to do.  There is an AI agent integrated into the application, and it was added as I needed to play around with it to get some better familiarity in agent integration for my job, so we ended up with it in this tool.  It does have access to view all of the data in the tools DB, so possibly can answer questions that the UI is not setup to do.  Agent does come with optional TTS and voice input.  Included a number of both external paid options, as well as local alternatives to provide a variety of options, and also for me to get a little exposure with each.  That all being said, it will not be active unless you actually set it up, so you can ignore it if you choose.

Runs natively on both Windows and Linux, which is why I went with Avalonia in the first place.  Linux ships as either an AppImage that keeps itself updated, or a plain tarball you extract and run.  I develop on Windows and test on both.

By default everything sits in a local SQLite file and that is all you need.  If you would rather run it against PostgreSQL you can point it at a server instead, and then you can have several clients open at once -- different machines, same data.  In that setup exactly one of them does the background work and the rest just read.  You can also run it with no window at all (`--headless`), or install it as a Windows service or a systemd user unit, so the polling keeps going when nothing is open.  None of that is required; the default is still one client and one file.

There are eight UI themes -- light, dark, and blue/pink/beige in both -- picked from Settings, applied immediately, and remembered per machine.  The interface also comes in eight languages: English, German, Spanish, French, Japanese, Korean, Russian and Simplified Chinese, with item, station and other game names in the same language the game client uses.

Keep in mind this application is still very green.  You are free to play around with it, but do expect issues during use.  Do not give up your old tools for this quite yet.  Needs a bit of a hardening period.

> **Status:** Beta (`0.9.x`). Actively developed — expect rough edges.

---

## Screenshots

<!-- Thumbnails are 3-across; click any image to view it full size. Add more as <td> cells, 3 per <tr>. -->
<table align="center">
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot11.png"><img src="media/screenshots/Screenshot11.png" alt="Overview dashboard" width="280"></a>
      <br><sub>Overview</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot19.png"><img src="media/screenshots/Screenshot19.png" alt="Worklist across all characters" width="280"></a>
      <br><sub>Worklist</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot12.png"><img src="media/screenshots/Screenshot12.png" alt="Universe map with tabs, security overlay and live-mark legend" width="280"></a>
      <br><sub>Universe Map</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot13.png"><img src="media/screenshots/Screenshot13.png" alt="Region view with security overlay" width="280"></a>
      <br><sub>Region View</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot14.png"><img src="media/screenshots/Screenshot14.png" alt="System view" width="280"></a>
      <br><sub>System View</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot15.png"><img src="media/screenshots/Screenshot15.png" alt="Jump Planner capital route" width="280"></a>
      <br><sub>Jump Planner</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot20.png"><img src="media/screenshots/Screenshot20.png" alt="Fitting tool with two fits side by side" width="280"></a>
      <br><sub>Fitting</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot4.png"><img src="media/screenshots/Screenshot4.png" alt="Market Overview dashboard" width="280"></a>
      <br><sub>Market Overview</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot16.png"><img src="media/screenshots/Screenshot16.png" alt="Sales Tracker" width="280"></a>
      <br><sub>Sales Tracker</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot17.png"><img src="media/screenshots/Screenshot17.png" alt="LP Market Values" width="280"></a>
      <br><sub>LP Market Values</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot18.png"><img src="media/screenshots/Screenshot18.png" alt="Sale Posting builder" width="280"></a>
      <br><sub>Sale Posting</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot2.png"><img src="media/screenshots/Screenshot2.png" alt="Item Browser price history" width="280"></a>
      <br><sub>Item Browser — Price History</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot1.png"><img src="media/screenshots/Screenshot1.png" alt="Item Browser market orders" width="280"></a>
      <br><sub>Item Browser — Market Orders</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot3.png"><img src="media/screenshots/Screenshot3.png" alt="Production Calculator" width="280"></a>
      <br><sub>Production Calculator</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot7.png"><img src="media/screenshots/Screenshot7.png" alt="Industry Opportunities" width="280"></a>
      <br><sub>Industry Opportunities</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot9.png"><img src="media/screenshots/Screenshot9.png" alt="Industry Jobs" width="280"></a>
      <br><sub>Industry Jobs</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot6.png"><img src="media/screenshots/Screenshot6.png" alt="Trade Opportunities" width="280"></a>
      <br><sub>Trade Opportunities</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot8.png"><img src="media/screenshots/Screenshot8.png" alt="Inventory Levels" width="280"></a>
      <br><sub>Inventory Levels</sub>
    </td>
  </tr>
  <tr>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot5.png"><img src="media/screenshots/Screenshot5.png" alt="Market price-source settings" width="280"></a>
      <br><sub>Market Settings</sub>
    </td>
    <td align="center" width="33%">
      <a href="media/screenshots/Screenshot10.png"><img src="media/screenshots/Screenshot10.png" alt="Corp Activity" width="280"></a>
      <br><sub>Corp Activity</sub>
    </td>
    <td align="center" width="33%"></td>
  </tr>
</table>

---

## Features

### Under the hood
- Background ESI pulls.  As long as application is running, data will refresh, and most UIs will automatically reflect those updates
- Definable market price definition, along with calculation and storage of price per item as market prices are refreshed.  This includes the ability to parse out lowball/highball prices, and base price calculations on % over build costs.  Useful for capitals and especially supers/titans.
- Ability to define detailed indy park, including different structures for different categories of items, as well as structure exceptions for specific items.  These are used for industry calculations.
- System calculates and stores the build cost for every craftable item in the game, and this updates as market prices are updated
- Build costs price blueprint copies off actual contracts (per run and by ME), and take the cheaper of building or buying each component
- Tranquility status sits in the header, and ESI polling pauses on its own while the server is down
- Database tab reports the size of every table, and can shrink, move or rename the database
- Data retention rules for the error log, killmails (your own characters' and corporations' kept on a separate window from everyone else's), price history, game logs and chat, swept in the background at least daily
- Optional zKillboard supplement.  ESI only hands you a killmail if you were the victim or got the final blow, so fleet participation is otherwise invisible
- Runs on SQLite by default, or PostgreSQL if you point it at a server.  On PostgreSQL you can open as many clients as you like against the same data, on as many machines as you like
- Exactly one client does the background work at a time.  It takes the job on a lease, and if you close it another one picks it up within a tick -- nothing is polled twice and nothing stops
- Can run with no window at all (`--headless`), or as a Windows service / systemd user unit, so the polling carries on when nothing is open.  Optional notification-area icon shows which client is doing the work
- Background Processes window shows every loop, its last and next run, and the live ESI call log -- from any client, not just the one doing the work
- Eight UI themes: light, dark, and blue/pink/beige in both.  Applies immediately, remembered per machine rather than per database
- Eight interface languages, picked under Settings > Other.  Game names (items, stations and the like) come out of the SDE in the chosen language, and search boxes find both that name and the English one
- UI scale from 50% to 200%, per machine
- The main window splits into two sides with tabs dragged between them, and any tool can be dragged out into a window of its own

### Industry / Trade
- Market Levels - Allows you to monitor a specific definable market for inventory of sell orders on a specific list of items
- Inventory Levels - Allow you to monitor your current inventory amounts (plus in build, buy, etc.) of a definable list of items (similar to jEveAssets stockpiles)
- Trade Opportunities - Compares markets for opportunities between them.  Results can be narrowed by item name without recalculating
- Net Worth - A Chart with lines to make you feel better/worse
- Income & Expense and Wallet - Where your ISK came from and went, by category, and the wallets themselves - journal, market transactions and corp divisions
- Planetary Industry - Every colony of your PI characters: when extractors end, when storage fills or inputs run out, what each colony imports and exports, what is sitting there waiting to be hauled, and profit per day.  Feeds Overview alerts, an alarm and worklist tasks
- Production Calculator - Accurate calculation of production jobs for the player.  Includes build costs, materials needed, etc.  Reports what you are missing and turns it into a shopping list
- Market Overview - What you have on the market, broken down by market group.  Sell and buy order units and ISK, alongside what has actually sold
- Worklist - One list of what to do next, rebuilt from live ESI data every refresh.  Eleven sources you can switch on and off independently - industry jobs, logistics, invention and copying, refining, material purchases, standing buy orders, inventory levels, corp projects, skill queues, asset safety and planetary industry
- Order Tracker - Track items you have agreed to supply.  Pending orders are matched against stock, active industry jobs and the contracts that deliver them, so an order completes itself when the contract is accepted
- Standing Buy Orders - Declare the buy orders you intend to keep standing at a station or structure, and see whether they are actually there, outbid, or nearly expired
- Sale Posting - Build shareable sale postings from your stock, with build/market/contract pricing and a configurable sale price.  Renders to Plain, Slack, Discord, Markdown, HTML or BBCode
- Sales Tracker - What has sold, for how much, and against what it cost you
- LP Market Values - What your loyalty points are actually worth, offer by offer, priced against the market
- Contracts - Browse your contracts and their items
- Industry Jobs - Every running job, with a check on whether it is actually getting the rig bonus you planned for
- Industry Opportunities - What is worth building right now.  Can be narrowed by item name, like Trade Opportunities
- Item Valuation - Paste a list (from the game client, in any language) and see what it is worth at any station: market, build and reprocessed values side by side, with stations compared
- Stores - Run your own store.  Buyers order by EVE mail, or on a small web site of your own on Cloudflare that the app deploys and keeps current, and orders land in the Order Tracker
- Price Overrides - Pin a price when you disagree with the market

### Ships
- Fitting - A fitting tool built on the game's own dogma rules out of the SDE.  DPS, capacitor simulation, tank and repairs, fleet boosts and remote support from other fits, mining yield, electronic warfare and lock times
- One fit per tab.  EFT paste and copy, drag and drop from a market-group item finder, damage profiles, cargo and market value
- Opens fits saved in EVE Console and your characters' in-game fittings from one picker, and saves back to where the fit came from
- Existing ships - Every assembled ship your characters and personal corps own, filtered by ship name, hull or system, with hull, fit and total value.  Opens one with its fit as a new fit, ready to Save As

### Universe / Navigation
- Universe Map - One continuous map of New Eden, from the whole cluster down to a single system.  Overlays colour systems by security, kills, jumps, industry indices, sovereignty (owner, ADM, standings and zones), faction warfare, incursions, stations, planetary output and intel sightings.  Maps open in tabs, two side by side if you like, and show your own characters and live hostiles from intel and recent kills
- System pages - Celestials, kills, industry indices, agents, graphs, intel sightings, sov campaigns, jump bridges and Thera/Turnur connections for any system, and which of your characters are there
- Route Planner - Routes through gates, jump bridges and Thera/Turnur (connections from EVE-Scout), with an avoid list.  Shows the route on the map and can set it as the in-game destination for a character who is online
- Jump Planner - Capital route planning with draggable waypoints and midpoints, jump-through structures, and the fuel and distance for each leg.  A tab of the map, beside a Jump range tab that rings every system a jump drive reaches
- Jump bridges - Ansiblexes read from ESI, or added by hand, drawn on the map and used for routes
- Sov campaigns - Every sovereignty campaign now and coming, in a map tab and on the map itself
- Structure Browser - Player structures pulled from the public structure list rather than only the ones you happen to have visited.  Links to Indy Parks so a park can name a real facility

### Intel / Logs
- Intel parsing - Reads your intel channel logs, works out who was seen where and in what (short system names, ship slang, counts like "3 camping", gate and flags), leaves your blues out, and puts the sightings on the map
- Game Log and Chat Log viewers - Search your local EVE logs, with past history importable in bulk
- Session tracking - Which characters are online, where they are, and what they are flying

### Corporation
- Activity monitor to track income from ratting, industry, etc., as well as mining activity, kills, and corp project activity
- Corp Projects - View active and historical corp project details
- Standing projects - Allows you to define projects you want to always maintain (i.e., destroy NPC projects in any system in region X with ADM below 4.0)
- Top 10 Lists - Produces activity top 10 lists based on corp ESI data

### Other
- Character Viewer - Similar to the one in game in case you are abstaining from logging your character in for some reason
- Item Browser - Full items browser, and description/attributes/etc. of every item in the game.  Also includes current market orders and price history for defined markets.
- Asset Viewer - Search across all personal and corp assets
- Killmail viewer - Corp and personal, or everything in New Eden if you turn the zKillboard feed on
- Player Entities - Every pilot, corp and alliance the app has met in a killmail, contract or chat log.  Shows zKillboard's stats for each, and pages in their whole kill and loss history from zKillboard
- NPC Entities - Agents, NPC corporations and factions out of the SDE.  Search an agent by name, or ask what is in a station
- Alerts - On the overview of the main screen alerts will show for things the tool believes you should look at (definable)
- Alarms - Conditions you define, checked on a timer, that tell you when something has happened.  Says it once rather than every time it checks.  Checks include intel reported within so many jumps or light-years of your characters or systems, undocks, a ship left sitting in space (wake-up call), game log events (decloaked, warp scrambled, under attack -- from a client in any language), PI colonies, market and contract prices, store orders and plain timers
- Eve Mail - Read and write eve mail... just because
- Notifications - Your in-game notifications, without logging in
- Slack - Optional.  Sends alerts to a direct message with yourself, and posts corp reports and sale postings to channels
- Discord - Optional.  Posts the Corp Top 10, monthly summary, sale postings and scheduled tasks to your channels through webhooks -- no bot, and it never pings anyone
- Scheduler - Posts messages and corp reports to Slack or Discord on a schedule you set
- ESI Explorer - Poke at the raw ESI data the app holds

### AI Agent ("Eden")
- Built-in conversational assistant with access to your character/corp data via tool calls
- Configurable to use external (Claude/OpenAI) or local (i.e., Ollama).  Models are picked from the service's own list, a role each (conversation, data questions, summaries), with a fallback when one fails
- Optional text-to-speech (Piper or Kokoro locally, your own speech server, OpenAI or ElevenLabs) and speech-to-text (local Whisper or OpenAI) with a push-to-talk key, for hands-free interaction
- Customizable name and voices
- AI Usage - What the agent has cost you, call by call

---

## Tech stack

- [Avalonia UI](https://avaloniaui.net/) 11 (cross-platform XAML UI framework) — Windows and Linux, `net9.0`
- .NET 9, [ReactiveUI](https://www.reactiveui.net/) (MVVM)
- EF Core 9, with SQLite for local persistence or PostgreSQL for a shared one
- [LiveChartsCore](https://livecharts.dev/) for charts
- CCP's [ESI API](https://esi.evetech.net/ui/) for all game data, with local caching of the Static Data Export (SDE)

---

## Getting started

### Requirements
- Windows 10/11, or Linux (developed against Arch; anything current should be fine)
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- On Linux only: **VLC**, which is the one thing the app cannot carry itself -- it loads the system copy for alarm sounds.  `pacman -S vlc` on Arch, `apt install libvlc-dev` on Debian/Ubuntu.  Everything else is in the build
- PostgreSQL is optional.  Only needed if you want several clients on one database

### Build & run

```bash
git clone https://github.com/kernoeve/EveConsole.git
cd EveConsole
dotnet restore
dotnet run
```

If you would rather not build it, grab a release: an installer on Windows, and on Linux either the `.AppImage` (`chmod +x` it and run -- it keeps itself updated) or the tarball, which you extract anywhere and run with `./EveConsole`.

On first launch, a **Welcome** dialog appears and the Settings window opens on the **ESI Tokens** tab — click **Add Character** there to authorize a character via EVE's SSO.

Your data stays on your machine.  By default that is a SQLite file at `%LOCALAPPDATA%\EVE Console Data\EveConsole.db` on Windows (kept apart from the program, which installing and updating replace) or `~/.local/share/EveConsole/EveConsole.db` on Linux.  The Database tab in Settings can move it, rename it, or switch you over to a PostgreSQL server if you want several clients sharing one.

See the [documentation](https://docs.eveconsole.com/getting-started/) for full install and setup steps.

---

## Contributing

This project uses a `develop` → `main` branching model:

- `main` is the protected release branch — every merge into it triggers an automated build and gets tagged with an auto-incrementing patch version (`vMAJOR.MINOR.PATCH`).
- `develop` is the integration branch — fork the repository, branch your work off `develop` (`feature/your-thing`, `fix/your-thing`) and open a pull request back into it.
- Periodic `develop → main` PRs cut a new release.

⚠️ A fresh clone puts you on `main`, and GitHub preselects `main` as the pull request base. Both need changing to `develop`.

**[CONTRIBUTING.md](CONTRIBUTING.md)** has the full walkthrough, what CI does and does not check, and two project rules that are easy to break. Please read it before your first pull request. See also the [Code of Conduct](CODE_OF_CONDUCT.md) and the [security policy](SECURITY.md).

---

## License

EVE Console is licensed under the [GNU General Public License v3.0](LICENSE).

---

*EVE Console is a third-party tool and is not affiliated with or endorsed by CCP Games. EVE Online and the EVE logo are trademarks of CCP hf.*
