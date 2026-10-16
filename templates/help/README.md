# Mod help page template

`index.html` is a help page for your mod in the same look as the [Progenitor Help Guide](../../docs/guide/progenitor-guide.html): pages with Previous and Next, a contents sidebar, search and a glossary with hover definitions. It is one file that opens from disk with no server and no network.

## Use it

1. Copy `index.html` into your mod as `help/index.html`.
2. Edit only the block between `<!-- HELP-CONTENT START -->` and `<!-- HELP-CONTENT END -->`. Leave the style and script around it as they are.
3. Open the file in a browser to check it.

## The content block

```js
window.HELP = {
  id: "my-mod",            // remembers the reader's last page; use your plugin id
  title: "My Mod",
  subtitle: "Help",
  version: "1.0.0",
  footer: "One line for the footer.",
  logo: "",                // optional data:image/png;base64,... (64 x 64); empty shows a letter
  chapters: [
    { id: "start", title: "Getting started", pages: [
      { id: "welcome", title: "What My Mod does",
        lede: "Optional line under the title.",
        html: `<p>Shows your castle's [[upkeep|upkeep]].</p>{{code}}`,
        code: { lang: "c#", text: `// optional; lang is c#, xml, shell or any label` },
        callout: { title: "Tip", text: "Optional; warn: true makes it red." } }
    ] },
    { id: "glossary", title: "Glossary", pages: [ { id: "terms", title: "Glossary", glossary: true } ] }
  ],
  glossary: [
    { id: "upkeep", term: "Upkeep", def: "One sentence." }
  ]
};
```

- `html` is HTML. `[[term-id|words]]` underlines the words and shows that glossary entry on hover; a click opens the glossary.
- `{{code}}` places the page's code block; without it the block goes after the text. The code is plain text, not HTML.
- Links to other pages: `<a href="#chapter-id/page-id">`. Anything outside the page needs a full `https://` URL: the file ships on its own, so a relative link points at nothing.
- Optional paths: add `roles: [{ id, title, blurb }]`, give chapters a `role`, and add a page with `chooser: true`. Readers then pick a path, and Previous and Next stay on it. Chapters without a `role` are shared.

## Keep it short

One idea per page: a title and about 60 words, or three to five bullets. At most one code block and one callout. Lead with what to do. One sentence per glossary entry. Use the table class `num` on number columns so numbers align right.

## For Progenitor maintainers

This file holds the shared renderer. After changing anything outside its content block, run `node tools/help-guide/sync.mjs` to copy it into `docs/guide/progenitor-guide.html` and the mod template's `templates/progenitor-mod/help/index.html`; `--check` fails when the two differ or when either has a relative link, and CI runs it.
