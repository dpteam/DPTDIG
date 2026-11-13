# Branding and debranding

The repository ships a **neutral** identity. Nothing in `public/` names a
deployment; every visible string, colour and URL comes from `config.ini`. That is
what lets you (or anyone) run the same code under a different name without a fork.

## What lives where

| Thing | Location | Editable at runtime |
|---|---|---|
| site name, tagline, description | `[site]` | restart |
| logo mark (1–3 chars) | `[site] shortName` | restart |
| colours (light / dark / OLED) | `[branding]` | restart |
| logo image, favicon | `[branding] logoUrl`, `faviconUrl` | restart |
| canonical URL, OpenGraph | `[site] baseUrl` | restart |
| footer note, MOTD, contacts | `[site]` | restart |
| default language and theme | `[site]` | restart |

`bin\config.ini` is generated on first start from `config.example.ini` and is
git-ignored, so your palette never ends up in the repository.

## Minimal rebrand

```ini
[site]
name=MyDig
shortName=M
tagline=Search the swarm
description=An index of torrents found on the public DHT.
baseUrl=https://dig.example.com

[branding]
accent=#e8590c
accentHi=#ff8a3d
accentLo=#b23f00
darkAccent=#ff8a3d
darkAccentHi=#ffb37a
darkAccentLo=#c2580f
```

That is enough for a coherent light + dark theme: `/brand.css` derives hover
states, the top bar gradient, soft accent washes and panel shades from those six
values. Missing values are mixed automatically from the accent, so you only need
the ones you care about.

## How the palette is generated

`Core/Branding.cs` renders CSS custom properties scoped per theme:

```css
html[data-theme="dark"]{
  --accent:#ff8a3d; --accent-hi:#ffb37a; --accent-lo:#c2580f;
  --link:#ffb37a; --accent-soft:rgba(255,138,61,0.16);
  --topbar:linear-gradient(...); --head-strong:linear-gradient(...);
}
```

Rules of thumb baked into the generator:

* on dark themes the top bar is *desaturated* toward black — a saturated bar
  swallows the whole layout;
* the link colour is the light accent on dark themes, the base accent on light;
* `--accent-soft` is the same hue at 11–16 % alpha, used for focus rings and
  selection;
* if only `accent` is given, `accentHi` mixes 22 % toward white and `accentLo`
  28 % toward black.

Valid inputs: `#rgb`, `#rrggbb`, with or without `#`. Anything else is ignored and
the neutral value stays — a typo cannot break the theme.

## Logo

Two options:

1. **Letter mark** (default): `[site] shortName=D` renders a gradient rounded
   square. No image needed, works on every DPI.
2. **Image**: `[branding] logoUrl=https://cdn.example.com/logo.svg` (or a path
   under `public/`, e.g. `/assets/img/logo.svg`). Recommended 34×34 px viewBox,
   square.

`faviconUrl` overrides the shipped `public/favicon.svg` (a magnifier over a
peer-dot grid — neutral, no letters).

## Language

`[site] defaultLanguage` accepts `ru` and `en`. Strings live in
`Core/Lang.cs`; the header has a `RU`/`EN` switch that sets a `diglang` cookie.
Adding a third language is ~120 lines in that one file — no build tooling, no
resource DLLs.

## Removing *our* traces

If you fork this and want to be sure nothing of the original identity remains:

```bash
grep -ri "dig" --include="*.cs" --include="*.html" --include="*.css" src public | grep -v "digest\|digit"
grep -ri "dartpower" . --exclude-dir=.git
```

Expected hits: the assembly name `DigServer.exe`, the `dig-*.log` file prefix, the
`diglang`/`digadm` cookies, the `--DG0100-` peer id and the `dig` namespace. Those
are internal identifiers, not branding — but rename them if your deployment
requires it. The site name, colours and URLs never appear in code.

## Theme switching

Three themes ship: `light`, `dark`, `oled`. The toggle cycles them, the choice is
stored in `localStorage`, and `public/assets/js/theme.js` applies it before first
paint so there is no flash. The default is `[site] defaultTheme`.

`oled` is pure black with muted panels for OLED screens; it inherits the dark
accent unless `[branding] oledAccent` overrides it.
