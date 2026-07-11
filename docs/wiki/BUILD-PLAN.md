# Wiki build-out plan

Pickup doc for continuing the GitHub Wiki. If you're a fresh session, read this
top to bottom first — it's self-contained.

## 1. How the wiki works (architecture + sync)

- The GitHub Wiki is a **separate git repo**: `https://github.com/kernoeve/EveCortex.wiki.git`.
- The **source of truth** is `docs/wiki/*.md` in the main repo (reviewable via PR;
  the wiki repo has no PR review). Edit here, then sync.
- `_Sidebar.md` is the nav shown on every wiki page. File names map to titles:
  `Getting-Started.md` → **Getting Started**. Link pages as `[Getting Started](Getting-Started)`.
- `README.md` in `docs/wiki/` and **this file** are repo-facing only — **do not sync
  them** to the wiki.

### Image conventions

- All doc images live in `docs/images/` in the **main repo** (one source of truth).
- **README** references them by relative path: `<img src="docs/images/foo.png">`.
- **Wiki** references them by absolute raw URL on the `develop` branch:
  `![alt](https://raw.githubusercontent.com/kernoeve/EveCortex/develop/docs/images/foo.png)`
  (relative paths are unreliable in wikis; `develop` is where doc images land first).
- The header banner (`docs/images/banner.png`) is generated from
  `Assets/splash_background.png`; **no version is baked in** (deliberate — so it
  doesn't need regenerating each release).

### Sync command (run after editing `docs/wiki/`)

```bash
git clone https://github.com/kernoeve/EveCortex.wiki.git /tmp/eve-wiki   # or `git -C /tmp/eve-wiki pull`
for f in docs/wiki/*.md; do b=$(basename "$f"); [ "$b" = "README.md" ] && continue; [ "$b" = "BUILD-PLAN.md" ] && continue; cp "$f" "/tmp/eve-wiki/$b"; done
cd /tmp/eve-wiki && git add -A && git commit -m "Sync wiki from docs/wiki" && git push
```

Verify a raw image URL returns 200 before syncing pages that reference a new image.

## 2. Page inventory & status

| Page | File | Status |
|---|---|---|
| Home | `Home.md` | Written (banner header) |
| Getting Started | `Getting-Started.md` | Written; needs `login`, `overview` screenshots |
| Tools Reference | `Tools-Reference.md` | Written; per-tool screenshots optional |
| Configuring Markets | `Configuring-Markets.md` | **Outline only** — needs real steps + screenshots |
| Industry Parks | `Industry-Parks.md` | **Outline only** — needs real steps + screenshots |
| AI Agent (Eden) | `AI-Agent-Eden.md` | **Outline only** — needs real steps + screenshots |
| FAQ & Troubleshooting | `FAQ-and-Troubleshooting.md` | Written |

## 3. Screenshot manifest

Drop these into `docs/images/` with **exactly these names** — the slots (commented
`<img>`/`![]` blocks) already reference them; uncomment once the file exists.
Review each image for personal data before committing (it's public).

| Filename | Screen to capture |
|---|---|
| `login.png` | The "Log in with Eve" launch screen |
| `overview.png` | Main overview/dashboard after login (also used in README) |
| `market-config.png` | Market settings tab where a market/config is defined |
| `market-price-type.png` | Price-type + lowball/highball filtering + build-cost floor controls |
| `industry-park.png` | Industry park editor (per-category structures + item exceptions) |
| `agent-config.png` | Agent settings (provider, TTS/STT, name/voice) |
| `production-calculator.png` | Production Calculator with a plan loaded |

Add more as needed; keep names lowercase-hyphenated and describe what they show.

## 4. What to write, and where the truth is

For accurate step-by-step guides, read the actual views/viewmodels rather than
guessing. Key sources:

- **Configuring Markets** — market pricing config lives in the Settings window:
  `ViewModels/MarketSettingsViewModel.cs`, `ViewModels/PriceHistorySettingsViewModel.cs`,
  `Views/SettingsWindow.axaml(.cs)`. Order-book viewing: `Views/MarketViewerView.axaml`,
  `ViewModels/MarketViewerViewModel.cs`. Data model per earlier notes:
  `MarketDefaultSettings` (AssetValueConfigId, price type, markup %), `MarketItemPrices`,
  `ContractPrices`. NPC orders are excluded (Duration > 90 heuristic).
- **Industry Parks** — `Views/IndyParksView.axaml(.cs)`, `ViewModels/IndyParksViewModel.cs`.
  Parks feed `BuildCosts` and the Production Calculator.
- **AI Agent (Eden)** — `ViewModels/AgentSettingsViewModel.cs`, `Views/AgentPanel.axaml(.cs)`,
  `ViewModels/AgentPanelViewModel.cs`. Providers: external (Claude/OpenAI) or local (Ollama);
  TTS = Piper/ElevenLabs; STT = local Whisper/OpenAI.
- **Tools Reference / other tools** — corresponding `Views/*View.axaml` +
  `ViewModels/*ViewModel.cs` pairs (MarketLevel, IndustryOpportunities, IndustryBrowser,
  NetWorth, ProductionCalculator, etc.).

### Suggested order

1. Fill **Configuring Markets** first (most tools depend on it), then **Industry Parks**.
2. Wire screenshots into Getting Started + those two guides as they arrive.
3. Flesh out **AI Agent** setup.
4. Add per-tool screenshots to Tools Reference.
5. Re-sync the wiki after each batch.

## 5. Workflow reminders

- Branch from `develop` (`docs/...`), PR into `develop`, never push `develop`/`main` directly.
- Commit trailer: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- After merging doc changes to `develop`, run the sync command in §1 to update the live wiki.
