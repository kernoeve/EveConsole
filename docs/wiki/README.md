# Wiki source

This folder is the **versioned source** for the project's [GitHub Wiki](https://github.com/kernoeve/EveCortex/wiki).
The wiki itself is a separate git repository (`EveCortex.wiki.git`) with no pull-request
review, so we keep an editable, reviewable copy here and sync it over.

## One-time: initialize the wiki

The wiki repo doesn't exist until the first page is created through the web UI:

1. Go to the repo's **Wiki** tab → **Create the first page** → Save anything.
2. After that, `https://github.com/kernoeve/EveCortex.wiki.git` becomes clonable.

## Syncing these files to the wiki

```bash
git clone https://github.com/kernoeve/EveCortex.wiki.git /tmp/eve-wiki
cp docs/wiki/*.md /tmp/eve-wiki/
cd /tmp/eve-wiki
git add -A && git commit -m "Sync wiki from docs/wiki" && git push
```

- Page file names map to titles: `Getting-Started.md` → **Getting Started**.
- `_Sidebar.md` is the navigation shown on every wiki page.
- Images are **not** copied here — they live in `docs/images/` in the main repo and
  are referenced from wiki pages by absolute raw URL
  (`https://raw.githubusercontent.com/kernoeve/EveCortex/main/docs/images/<file>`).
