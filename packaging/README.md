# Packaging Rhinomon (Yak)

This directory holds the Yak package manifest (`manifest.yml`) used to build
the distributable `.yak` package for food4Rhino / Rhino's Package Manager.

> Exact CLI flags can shift between Yak/Rhino SDK versions. Before a real
> release, cross-check the steps below against `yak help build`,
> `yak help push`, and the current docs at
> https://developer.rhino3d.com/guides/yak/ — treat this file as the
> standard workflow, not a guaranteed-current CLI reference.

## Prerequisites

- Rhino 8 installed. It ships the `Yak.exe` CLI, typically at:
  `%ProgramFiles%\Rhino 8\System\Yak.exe`
  (add it to your `PATH`, or invoke it by full path below.)
- A McNeel/Rhino account for `yak login` (required before pushing).
- A Release build of the plugin producing `Rhinomon.rhp`
  (see `src/Rhinomon`, per `docs/CONTRACT.md`).

## What goes into the package

A Yak package is built from a staging folder that contains, side by side:

- `manifest.yml` (this directory's Yak package manifest)
- `rhinomon.png` — the package icon referenced by `manifest.yml`
- `Rhinomon.rhp` — the Release build output
- any non-GAC runtime dependencies the plugin needs alongside the `.rhp`
  (none expected beyond RhinoCommon, which is provided by Rhino itself per
  `docs/CONTRACT.md`)

Before packaging, confirm in `manifest.yml`:

- `version` matches the release you're cutting (semantic versioning,
  `x.y.z`), and matches the built assembly's version
- `authors` — placeholder, needs a real name/organization
- `url` — placeholder, needs the real project/repo/landing page
- `icon` — path resolves to an actual icon file staged next to the manifest

## Build

From the staging folder (containing `manifest.yml`, the icon, and the
`.rhp`):

```
yak build
```

This produces a file named per Yak's convention:

```
rhinomon-<version>-rh8-win.yak
```

e.g. `rhinomon-0.1.0-rh8-win.yak` (newer Yak versions may include the exact
Rhino 8 service-release tag, such as `rh8_32-win`). The Rhino/platform tag
communicates the distribution target — Rhino 8, Windows — matching the naming
called out in `PRD.md` §8.

## Sanity-check before publishing

Install the built `.yak` locally and confirm it works before pushing it
anywhere public:

- Drag-and-drop the `.yak` file onto an open Rhino window, **or**
- `yak install` it against a local/test source

Then run the `Rhinomon` command in that Rhino session and confirm the pet
appears and the `Pet` / `Scale` / `Activity` / `Hide` options all work.

## Publish

1. Authenticate once per machine:
   ```
   yak login
   ```
2. Push to the **test** server first and verify the listing there:
   ```
   yak push --source https://test.yak.rhino3d.com rhinomon-<version>-rh8-win.yak
   ```
3. Once verified, push to production:
   ```
   yak push rhinomon-<version>-rh8-win.yak
   ```
4. Confirm the package shows up in Rhino's Package Manager, and update the
   food4Rhino listing (see `docs/food4rhino-listing.md`) with the new
   version / changelog / media.

## Versioning

Use semantic versioning (`x.y.z`). Bump `manifest.yml`'s `version` together
with the plugin's assembly version for every release; keep the two in sync
so the package file name always reflects what's actually inside it.
