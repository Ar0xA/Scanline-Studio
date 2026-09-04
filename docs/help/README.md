# Scanline Studio HTML help

In the application, open **Help → User guide**. During development, `index.html` can also be opened
directly in a browser. The guide is deliberately static and dependency-free, so search, navigation,
responsive layout, and printing work without a web server or internet access.

## Updating the guide

Most application changes require editing only `index.html`:

1. Find the matching `<article data-help-topic>` and update its ordinary HTML content.
2. Keep the article's `id` stable so old bookmarks continue to work.
3. Update `data-keywords` with synonyms that do not naturally appear in the visible text.
4. For a new topic, copy an existing article and set:
   - a unique, URL-safe `id`;
   - `data-help-topic`;
   - a `data-category` matching an existing category or naming a new one;
   - a concise `data-keywords` list;
   - one `h2`, which becomes the contents and search-result title.
5. Update the `help-version` meta value when the guide is released. The visible revision is filled from it automatically.

`help.js` builds the contents and search index from the articles at runtime. There is no generated
search-index file or hand-maintained contents list. Article order in `index.html` is the navigation
order; the first appearance of a category sets category order.

Screenshots are intentionally avoided. They become stale quickly, are hard to localize, and often
hide the control names users need to search. Add one only when a spatial relationship cannot be
explained clearly in text, and keep it in an `images/` subdirectory with useful alternative text.

## Explaining hard terms

Write for a technically comfortable but non-expert reader — plain wording first, before reaching
for a hover explanation. When a term genuinely needs defining, add it once to the `glossary`
article's `<dl>` with a stable `id="glossary-<term>"` on its `<dt>`, then link the term's first bare
occurrence per article with `<a class="term" href="#glossary-<term>">...</a>`. The tooltip (built in
`help.js`) reads the definition live from that `<dt>`/`<dd>` pair at hover/focus time — there is no
second copy to keep in sync, and the link itself is a working fallback with no JavaScript or on a
touch device with no hover. Don't link every occurrence of a term in the same article; the first is
enough.

## Update checklist

- **Any change that adds, renames, or removes a user-visible Options field, menu item, dialog, or
  workflow step must update this guide in the same change**, and pass the structural check below,
  before the change is considered done — a stale guide is a real regression, not a follow-up.
- Compare main menu items and tab names with `src/ScanlineStudio.UI/Views/MainWindow.axaml`.
- Compare Options sections with `src/ScanlineStudio.UI/Views/OptionsWindowView.axaml`.
- Compare visible wording with `assets/locale/en.json`.
- Describe shipped behavior only; do not document roadmap-only controls as available.
- Search the guide for renamed or removed UI labels.
- Open `index.html` through a `file://` URL and test search, keyboard navigation, mobile width, and print preview.
- Run the structural check documented below.

## Structural check

From the repository root:

```sh
node docs/help/check-help.mjs
```

The check validates topic IDs, internal fragment links, required metadata, local assets, and a small
set of search expectations. It uses only Node.js built-ins and does not download packages.

## Files

- `index.html` — semantic help content and release metadata.
- `styles.css` — visual design, responsive layout, and print rules.
- `help-search.js` — pure offline search and ranking shared by the browser and structural check.
- `help.js` — generated contents, search UI, keyboard behavior, active-topic tracking, and the hover/focus term-tooltip mechanism.
- `check-help.mjs` — dependency-free structural regression check.
