# Changelog

All notable changes to Rhinomon. Versions follow semantic versioning.

## [0.2.0] - 2026-11-10

A reliability release for the "never in your way" promise.

### Fixed
- Reaction icons (sparkle, heart, ...) could stay above the pet for as long as
  a command was running, or indefinitely on heavy scenes.
- The pet is no longer drawn in `ViewCaptureToFile` / `ViewCaptureToClipboard`
  output or in prints.
- World mode: a pet hidden behind geometry no longer takes the click meant for
  the object in front of it, and a pet behind the camera no longer leaves an
  invisible click area on screen.
- World mode: automatic size is now correct in meter, foot and inch files
  (small models used to get an oversized pet); an explicit `WorldSize` is used
  exactly as typed.
- With a custom pet active, the command-line `Pet` option now switches back to
  the built-in pets.
- After restarting Rhino, the first `Rhinomon` run only brings the pet back;
  pressing Enter again no longer dismisses it.
- Importing a large PNG as a custom pet no longer freezes Rhino for seconds.

### Changed
- The pet rests more quietly to save redraws: when idle it holds still and
  breathes or blinks every few seconds, after two minutes asleep it becomes
  completely static, and it does not animate while Rhino is in the background.
- World mode no longer walks the whole object table every 750 ms while idle.

## [0.1.0] - 2026-07-06

First public release: Clawd, Crab and Nova; Screen and World (2.5D) modes;
settings panel; custom pet import.
