# food4Rhino Listing Draft

Draft copy for the Rhinomon food4Rhino page. Adjust wording as needed when
submitting; items in `[brackets]` are placeholders to fill in, not final
text.

---

## One-liner (tagline)

> A pixel-art desktop pet for your Rhino viewport that never gets in the way
> of your modeling.

## Short description

Rhinomon adds a tiny, low-key companion to your Rhino 8 viewport — it walks,
climbs your geometry, and reacts when you click it or edit your model. It's
drawn entirely through the display pipeline and writes nothing to your
document, and it's engineered to automatically throttle down or disappear
rather than ever cost you modeling performance.

## Full description

Rhinomon lives quietly in the corner of your viewport while you work. Pick
one of three pets, and it will:

- Wander along the bottom of the viewport when you're idle
- Climb up onto objects you've modeled and rest on top of them
- Stay anchored to that spot as you orbit or pan the view
- Fall asleep after a while, and wake up the moment you start working again
- React with a small pixel icon when you click it (❤), create geometry (✨),
  delete a batch of objects (❗), or undo (❓)

It never writes any geometry, layers, or objects into your document — it's
drawn purely through Rhino's `DisplayConduit`. And it's built around one
non-negotiable rule: **it must never slow down your modeling.** Its own
per-frame draw cost is under 0.3 ms, it automatically drops to 1–2 fps (and
eventually stops its timer entirely) as your scene gets heavier to redraw,
and it produces zero extra redraws while any command is running. A single
`Rhinomon Hide` fully unloads it — timers, event hooks, and drawing — with
one command to bring it back.

### Features

- Three pets to choose from: **Clawd** (classic orange), **Crab** (walks
  sideways), **Nova** (dark-theme-friendly night variant)
- Idle wandering, climbing onto your own geometry, and sleeping, with three
  pacing presets (`Lively` / `Normal` / `Chill`)
- Reacts to being clicked, and to creating/deleting/undoing objects, with
  pixel-icon expressions (no text, no sound)
- Adjustable sprite scale (1× / 2× / 3×)
- Zero document writes — pet state lives only in the display pipeline
- Automatic performance throttling on heavy scenes, plus a one-command kill
  switch (`Rhinomon Hide`) that fully unloads the plugin's timers and hooks
- Single command (`Rhinomon`) with GetOption-style settings, persisted
  across Rhino restarts
- No telemetry — nothing is collected or transmitted

### System requirements

- Rhino 8
- Windows
- (macOS is not supported in this version)

### License

MIT — see the plugin's `LICENSE` file. *[TODO: confirm final copyright
attribution before publishing.]*

### Links

- Website / repository: `[TODO: URL]`
- Support / issues: `[TODO: URL or contact]`
- Changelog: `[TODO: link, or inline changelog below]`

### Changelog

- **0.1.0** — Initial release. `[TODO: fill in once M7 ships — three pets,
  idle/climb/sleep behavior, click and modeling reactions, performance
  governor, Yak package.]`

---

## Media assets checklist

None of the following exist yet — this is a shot list for whoever captures
the release media, not a claim that they've been produced. Suggested specs:
GIFs ≤ a few seconds, looping, no audio needed (the plugin has none); stills
as PNG.

1. **Hero GIF** — a pet (Clawd) idling in the bottom-right corner of a
   viewport while the user models normally around it. Purpose: prove at a
   glance that it doesn't get in the way.
2. **Idle timeline GIF** — pet walks along the bottom edge, then climbs onto
   a box and settles into its sleep pose. Purpose: show the core idle
   behavior loop (walk → climb → sleep).
3. **Camera-follow GIF** — pet perched on an object while the user orbits/
   pans the view; pet stays anchored to the object. Purpose: show it doesn't
   float around independent of your model.
4. **Click reaction GIF** — clicking the pet triggers the "petted"
   happy animation + heart icon. Purpose: show the interactive payoff.
5. **Modeling reaction GIF** — creating objects triggers a happy/sparkle
   reaction; deleting a batch (10+) triggers a surprised/exclaim reaction.
   Purpose: show it's aware of your modeling, not just decorative.
6. **Command options screenshot** — the Rhino command line showing the
   `Rhinomon` command's `Pet` / `Scale` / `Activity` / `Hide` options.
   Purpose: show how to configure it without needing extra UI.
7. **Three-pets comparison screenshot** — Clawd, Crab, and Nova shown
   side-by-side at the same scale. Purpose: help buyers pick a pet, and show
   Nova's dark-theme legibility.
8. **Performance proof screenshot (optional but persuasive)** — the 100k-face
   `TestMaxSpeed` benchmark scene with the pet on vs. off, showing the fps
   counter. Purpose: back up the "won't slow you down" claim with a visible
   number, per the acceptance test in `PRD.md` §7.
9. **Listing icon** — a square icon (recommended 128×128 PNG, transparent
   background) for the Package Manager / food4Rhino thumbnail. Needed for
   `packaging/manifest.yml`'s `icon` field as well.
