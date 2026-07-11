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
# Copy every page except the repo-facing docs (README.md, BUILD-PLAN.md)
for f in docs/wiki/*.md; do
  b=$(basename "$f")
  [ "$b" = "README.md" ] && continue
  [ "$b" = "BUILD-PLAN.md" ] && continue
  cp "$f" "/tmp/eve-wiki/$b"
done
cd /tmp/eve-wiki
git add -A && git commit -m "Sync wiki from docs/wiki" && git push
```

- Page file names map to titles: `Getting-Started.md` → **Getting Started**.
- `_Sidebar.md` is the navigation shown on every wiki page.
- Images are **not** copied here — they live in `docs/images/` in the main repo and
  are referenced from wiki pages by absolute raw URL
  (`https://raw.githubusercontent.com/kernoeve/EveCortex/develop/docs/images/<file>`;
  `develop` is where doc images land first — see BUILD-PLAN.md).
