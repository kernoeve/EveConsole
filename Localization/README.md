# Interface languages

EVE Console's own text — labels, headers, tooltips, messages — lives here, one file per part of the
app, so it can be shown in more than one language. This page is for developers adding text and for
translators.

## How it works

- **The English** is `<Area>Text.resx` — `ShellText.resx` (the main window and its navigation),
  `SettingsText.resx` (the Settings window). At build time each becomes a class,
  `EveConsole.Localization.ShellText`, with one property per entry.
- **A translation** is the same file with a language code: `ShellText.zh-Hans.resx`. The build puts
  it in `zh-Hans/EveConsole.resources.dll` beside the app, and `ShellText` reads from it when the
  interface is in Chinese. Anything a translation leaves out falls back to English.
- **The language** is chosen in *Settings → Other → Language*. It is kept for this machine only, in
  `config.json` as `ui.language`, and used from the next start: every label is read once, when its
  window is built. *System default* picks the system's own language when a finished translation of
  it exists, and English otherwise.
- **Numbers and dates** follow the machine's regional format — unless that format is in another
  language than the interface (a Chinese interface on an English-region PC): then they follow the
  interface language, so no English month names sit among the Chinese. Date patterns a person
  reads are entries too (`CommonText.Date*`, ".NET date format patterns"), so a translation can
  put the parts in its own order. Text the AI model reads is formatted invariantly and stays
  English.

## Languages

| Code      | Language   | State |
|-----------|------------|-------|
| `en`      | English    | The source |
| `zh-Hans` | 简体中文    | **Preview**: the first screens, as machine drafts awaiting a native speaker's review ([#125](https://github.com/kernoeve/EveConsole/issues/125)) |

EVE's own languages are the candidates for the rest: German (`de`), French (`fr`), Spanish (`es`),
Russian (`ru`), Japanese (`ja`) and Korean (`ko`). A language is added to `Languages.All` in
[Languages.cs](Languages.cs) once its files exist, with the fonts to try first for Chinese,
Japanese and Korean — see the note there on why.

A language marked `Preview` is offered in Settings but never picked automatically, so nobody is
dropped into a half-translated app because of their system's language.

## Adding text (developers)

New text goes into the area's `.resx`, never straight into the XAML or the code.

```xml
<!-- the window declares  xmlns:loc="using:EveConsole.Localization" -->
<TextBlock Text="{x:Static loc:SettingsText.ThemeLabel}" />
```

```csharp
Title = ShellText.NavOverview;
Status = string.Format(SettingsText.RestartFailed, error);
```

A mistyped name is a build error, not a blank label.

- **Whole sentences, with placeholders.** `"Found {0} items in {1}"`, never `"Found " + n + " items"`:
  other languages put the words in a different order.
- **Counted phrases** are a family named for their forms: `ItemsOne` and `ItemsOther` in English,
  plus `ItemsFew` and `ItemsMany` where Russian needs them. Format them with
  `Plurals.Format(OrdersText.ResourceManager, nameof(OrdersText.ItemsOther), count)`; the number is `{0}`.
- **Add a comment** wherever a translator could misread an entry: what `{0}` is, or whether
  "Order" means a market order or a sort order.
- **Never match on what is displayed.** Select a tab, find a column or compare a choice by an id,
  or by the same resource the label was built from — the displayed words change with the language.
- **Keys** are PascalCase, named for where they appear: `Nav…` for navigation, `Tab…` for tabs,
  `Tip…` for tooltips, `…Label`, `…Header`, `…Note`.
- **One file per area.** `OrdersText.resx` for the Order Tracker, and so on: files stay a size a
  translator can review, and work on two parts of the app does not collide in one file.

## Translating

- Copy the English entries into `<Area>Text.<code>.resx`, or edit in Rider's resource editor,
  which shows every language side by side.
- **Keep every placeholder** — `{0}`, `{1:N0}` — exactly as written. You may move them; you may not
  drop or add one. A missing one would stop that label from showing at all.
- **Use the game's own words** in your language, as the EVE client shows them.
- **Leave names alone:** EVE Console, ESI, SDE, Tranquility, CCP, zKillboard.
- **Leave out what you have not translated.** It falls back to English. An empty entry is an
  error, not a fallback.
- Chinese labels use full-width punctuation: `主题：`, `（预览）`.

## Checking

- **`tools/LocCheck`**, run on every pull request, fails on any lost or extra placeholder, broken
  braces, a key the English does not have, an empty entry, or a file named for a language .NET does
  not know. It also reports how much of each language is done, and how much text is still written
  into the XAML. Locally: `dotnet run --project tools/LocCheck/LocCheck.csproj`.
- **`--pseudo-loc`** runs the app in a fake language: every string from these files accented,
  bracketed and about a third longer, like `[Šéţţíñĝš ~~~]`. Plain English that is left shows text
  not moved here yet, and a label cut short will be cut short in German and Russian too.
- **Linux** needs a CJK font for Chinese, Japanese or Korean: `noto-fonts-cjk` on Arch,
  `fonts-noto-cjk` on Debian and Ubuntu. Windows and macOS have them already.
