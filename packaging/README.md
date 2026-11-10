# Packaging Rhinomon (Yak)

This directory holds the Yak package manifest (`manifest.yml`) used to build
the distributable `.yak` package for food4Rhino / Rhino's Package Manager.

> Exact CLI flags can shift between Yak/Rhino SDK versions. Before a real
> release, cross-check the steps below against `yak help build`,
> `yak help push`, and the current docs at
> https://developer.rhino3d.com/guides/yak/ — treat this file as the
> standard workflow, not a guaranteed-current CLI reference.

## Releasing (automated)

Releases are cut by pushing a tag; `.github/workflows/release.yml` does the
rest on a Windows runner (no Rhino needed, the yak CLI is downloaded from
McNeel):

1. Bump the version in **both** `src/Rhinomon/Rhinomon.csproj` (`<Version>`)
   and `manifest.yml`, add a section to `CHANGELOG.md`, and merge to `main`.
   CI fails if the two versions differ.
2. Tag and push:
   ```
   git tag v0.2.0
   git push origin v0.2.0
   ```
   Use `v0.2.0-beta.1` for a prerelease: it is only offered to users who tick
   "Include pre-releases" in the Package Manager, which makes it the safe way
   to try a build in your own Rhino first.
3. The workflow builds `rhinomon-<version>-rh8_0-win.yak`, attaches it to a
   GitHub release, and pushes it to yak.rhino3d.com **if** the `YAK_TOKEN`
   repository secret is set. Without the secret it stops after the GitHub
   release and says so in the run summary.

One-time setup of the secret, on any machine with the yak CLI (it ships with
Rhino as `Yak.exe`):

```
yak login --ci
```

Sign in with the McNeel account that owns the `rhinomon` package, copy the key
it prints, and store it as the repository secret `YAK_TOKEN` (GitHub ▸
Settings ▸ Secrets and variables ▸ Actions, or `gh secret set YAK_TOKEN`).

The same build can be run locally with `pwsh packaging/build-yak.ps1`; the
package lands in `dist/`. Yak never lets a pushed version be replaced or
deleted (only yanked), so anything wrong means rolling the version forward.

The package is built against the oldest Rhino 8 SDK (see the `RhinoCommon`
pin in the csproj), which is what gives it the `rh8_0` tag and lets every
Rhino 8 install it. The script refuses to produce a package with any other
tag.

The sections below describe the same steps done by hand.

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
