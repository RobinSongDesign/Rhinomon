# Rhinomon

*A pixel-art desktop pet that lives in your Rhino 8 viewport — and never gets in your way.*

<!-- HERO GIF: idle pet (Clawd) sitting in the bottom-right corner of a Rhino
     viewport while the user models normally around it, to show it does not
     interrupt work -->

> **Status:** documentation draft tracking the v1.0 feature set defined in
> [`PRD.md`](./PRD.md). Screenshots and GIFs below are placeholders — final
> art/media will be captured once the corresponding milestones ship.

Rhinomon adds a small, low-key companion to your modeling viewport. It walks
around, climbs onto the geometry you build, reacts when you click it or when
you create/delete/undo things, and naps when you're away — all drawn purely
through Rhino's display pipeline (`DisplayConduit`). **It never writes any
geometry, layers, or objects into your document**, and it is built to get out
of the way automatically the moment your scene gets demanding.

Core promise: **the pet is company, not a burden. It must never interfere
with your modeling.**

---

## Meet the Pets

<!-- SCREENSHOT: the three pets (Clawd, Crab, Nova) shown side by side at the
     same scale for a style/size comparison -->

All pets are 32×32 logical-pixel sprites with 7 animation sets (idle, walk,
climb, sleep, petted, surprised, happy). You can switch between them anytime
without restarting Rhino.

- **Clawd** — the classic, a round four-point orange star with bean-shaped
  eyes. The default pet.
- **Crab** — a crab-clawed variant that walks sideways.
- **Nova** — a deep-blue "night" variant with subtle star/sparkle accents,
  designed to stay readable on dark viewport backgrounds.

---

## Installation

### Via Yak (recommended)

1. In Rhino 8, run the `PackageManager` command (or **Tools ▸ Package
   Manager**).
2. Search for **Rhinomon**.
3. Click **Install**, then restart Rhino if prompted.

### Manual (.rhp)

1. Download the `Rhinomon.rhp` file for your Rhino 8 version.
2. If the file was downloaded from the internet, right-click it ▸
   **Properties** ▸check **Unblock** (Windows sometimes flags downloaded
   plugin files).
3. Drag and drop the `.rhp` file onto an open Rhino window, **or** go to
   **Options ▸ Plug-ins ▸ Install...** and browse to the file.

**Requirements:** Rhino 8, Windows. (macOS is not supported or tested in v1.)

---

## Usage

Run the **`Rhinomon`** command.

- Press **Enter** with no options → toggles the pet on/off.
- Click an option on the command line before pressing Enter to change a
  setting:

| Option | Values | Effect |
|---|---|---|
| `Pet` | `Clawd` / `Crab` / `Nova` | Choose which pet is active |
| `Scale` | `1` / `2` / `3` | Sprite size (logical 32×32 px, ×1/×2/×3 nearest-neighbor scaling; default ×2) |
| `Activity` | `Lively` / `Normal` / `Chill` | How quickly the pet gets bored and moves through its idle timeline (default: `Lively`) |
| `Hide` | — | Kill switch: permanently disables the pet (asks for confirmation). Run `Rhinomon` again anytime to bring it back |

All settings persist across Rhino restarts via `PlugIn.Settings`.

<!-- SCREENSHOT: the Rhinomon command line showing the Pet / Scale / Activity
     / Hide options -->

### Idle timeline

When you stop interacting with Rhino, the pet works through a short idle
timeline. The pace depends on the `Activity` setting:

| Activity | Walks after | Climbs geometry after | Falls asleep after |
|---|---|---|---|
| **Lively** (default) | 10 s | 30 s | 2 min |
| **Normal** | 30 s | 90 s | 5 min |
| **Chill** | 2 min | 10 min | 30 min |

<!-- GIF: idle timeline — pet walks along the bottom edge, climbs onto a box,
     then falls asleep on top of it -->

While the pet is anchored on a piece of geometry, orbiting or panning the
view leaves it in place (it re-projects onto the anchor point every frame).
Starting a command or editing the document wakes it immediately and sends it
back home to the bottom-right corner of the viewport. If the object it's
perched on is deleted, or scrolls off-screen, it plays a small "fall"
animation and returns home.

<!-- GIF: view orbiting while the pet stays anchored on top of a box -->

### Reactions

- **Click the pet** (only works when no command is running, and only inside
  its bounding box) → a happy "petted" animation with a heart icon. During an
  active command, clicks pass straight through untouched.
- **Create objects** → happy reaction with a sparkle icon.
- **Delete 10+ objects at once** → surprised reaction with an exclamation
  icon.
- **Undo** → confused reaction with a question-mark icon.

Reactions are batched per command and rate-limited to one every 8 seconds, so
a flurry of edits doesn't turn into a flurry of pop-ups.

<!-- GIF: clicking the pet for a "petted" reaction, and creating/deleting
     objects to trigger happy / surprised reactions -->

The pet lives globally — there is only one, and it follows you: it stays in
whichever viewport is currently active and "moves house" when you switch.

---

## Zero-Interruption Performance Design

Rhinomon is built around one rule: **it must never cost you modeling
performance.** Concretely, per the acceptance targets in `PRD.md` §7:

- Its own draw cost per frame is under **0.3 ms**.
- Total memory footprint added by the plugin is under **30 MB**.
- If your scene's redraw time goes over **25 ms**, the pet's animation
  automatically drops to **1–2 fps**.
- If redraw time goes over **60 ms**, its timer stops completely — it
  becomes a static, purely event-driven sprite until things speed back up.
- It adds **zero** to Rhino's startup time (the plugin loads on demand, only
  when the command first runs).
- While any command is running, the pet produces **zero** additional
  viewport redraws.
- Its event handlers do O(1) constant-time work with no heap allocation, so
  they don't add GC pressure even in large documents.
- The `Hide` kill switch fully unloads every timer, event subscription, and
  the display conduit — not just visually, but at the code level.

**Acceptance test:** on a 100,000-face benchmark model, comparing Rhino's
`TestMaxSpeed` viewport frame rate with the pet on vs. off must show a
difference of **less than 2%**.

<!-- SCREENSHOT (optional): TestMaxSpeed fps counter on the 100k-face
     benchmark scene, pet on vs. off, for a before/after comparison -->

---

## FAQ

**How do I turn it off completely — will anything keep running in the
background?**
Run `Rhinomon`, choose `Hide`, and confirm. This is a full kill switch: every
timer, event subscription, and the display conduit are unloaded, not just
hidden. Run `Rhinomon` again anytime to bring it back.

**Will it slow down large models?**
It's designed not to. Idle draw cost is under 0.3 ms/frame, it automatically
throttles to 1–2 fps as your scene gets heavier, and stops its timer entirely
above the 60 ms redraw threshold. During any active command it draws zero
extra frames. On a 100k-face benchmark scene, toggling the pet changes
measured viewport frame rate by less than 2%. See "Zero-Interruption
Performance Design" above.

**Does it write anything into my model or `.3dm` file?**
No. It's drawn entirely through `DisplayConduit` — no geometry, layers, or
document objects are ever created, and nothing is saved into your file.

**Does it collect any data / telemetry?**
No. Nothing is collected or transmitted, ever.

**Can it interfere with clicking on my own geometry?**
No — it only intercepts a left click when no command is running *and* the
click falls inside its own small bounding box. During any command, mouse
input passes straight through untouched.

**Is there Mac support?**
Not in v1. The architecture doesn't rule it out, but it isn't built or tested
for macOS yet.

**Does it make sounds?**
No, by design — v1 has no audio at all.

**Can I have more than one pet, or use it with Grasshopper?**
Not in v1 — see Known Limitations below.

---

## Known Limitations (v1)

These are deliberate scope decisions, not bugs:

- No sound.
- One global pet at a time (no multiples).
- No text/chat bubbles — reactions are pixel-icon only (❤ ❗ ❓ 💤 ✨), which
  also means there's nothing to translate.
- No Grasshopper integration.
- No telemetry or usage tracking of any kind.
- Mood is session-only and resets whenever Rhino restarts — no persistent
  "leveling up".
- Settings are command-line only (`Rhinomon` + options); no Eto UI panel yet.

---

## License

MIT — see [`LICENSE`](./LICENSE).

---

## 中文摘要

Rhinomon 是一款运行在 Rhino 8 视口内的像素风桌宠插件，完全通过显示管线
（DisplayConduit）绘制，**不向文档写入任何几何数据**。宠物有三种形态可选：
经典橙色的 **Clawd**、横着走的蟹钳变体 **Crab**、以及适合暗色主题的深蓝夜间
变体 **Nova**。可通过 Yak 包管理器或手动安装 `.rhp` 文件使用；运行
`Rhinomon` 命令即可开关宠物，并可设置 `Pet`（形态）、`Scale`（缩放）、
`Activity`（活跃度：Lively/Normal/Chill）、`Hide`（彻底关闭的一键开关）。

核心承诺是**零打扰建模**：单帧自绘耗时 <0.3ms，插件内存增量 <30MB；场景重
绘超过 25ms 时动画自动降到 1–2fps，超过 60ms 时定时器完全停止、宠物退化为
纯静态贴图；任何命令运行期间宠物不产生额外重绘；`Hide` 后所有定时器、事件
订阅与 conduit 完全卸载。以上指标均以 10 万面片场景下 `TestMaxSpeed` 测得
的帧率差异 <2% 作为验收标准（详见 `PRD.md` §7）。
