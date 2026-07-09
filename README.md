# Slide Colours

Matches your stage lighting to what's on screen. Slide Colours watches ProPresenter 7 on the
local machine, extracts the dominant colour of each slide as it goes live, and streams that
colour to your DMX rig — with a small floating on/off toggle you can park anywhere on screen.

## Download

**[⬇ Download the latest release](https://github.com/tango7nz/slide-colours/releases/latest)** —
grab `SlideColours-vX.Y.Z-win-x64.exe` and double-click it. It's fully self-contained, so there's
**nothing to install** (no .NET runtime needed) — just 64-bit Windows 10/11.

> On first launch, Windows SmartScreen may warn about an unrecognised app (the exe is unsigned).
> Click **More info → Run anyway**.

## How it works

1. Listens to ProPresenter's local HTTP API for slide changes (streaming, no polling).
2. When the slide changes, fetches that slide's thumbnail and finds its dominant colour —
   biased towards vivid, saturated pixels so white lyric text and black backgrounds are ignored.
3. Fades the stage colour smoothly to the new colour and transmits DMX ~30×/second while
   the toggle is on. When toggled off, it stops transmitting so your lighting desk is back in charge.

## Requirements

- Windows 10/11. The app needs the **.NET 8 Desktop Runtime** — either install it, or use the
  self-contained build that bundles it (see [Deploying to another PC](#deploying-to-another-pc)).
- **ProPresenter 7.9 or later** with the network API enabled:
  *ProPresenter → Preferences → Network → tick **Enable Network***, then note the **port** shown there.
- A DMX route, one of:
  - **Art-Net** node/gateway on the network (default)
  - **sACN (E1.31)** node on the network
  - **Enttec DMX USB Pro** (or compatible) on a COM port

## Running it

Run `dist\SlideColours.exe`. A small floating pill appears (top-right by default). From left to right:

- **✕ Close** — quits the app.
- **Cog** — opens *Settings…*.
- **Favourite colours** — ten preset swatches:
  - **Left-click** to send that colour to the lights (this switches to *manual* mode — see below).
  - **Right-click** to edit that swatch: a colour picker opens seeded with its current colour, and
    saving stores the new colour in that slot.
- **Follow slide** — the colour mode. When highlighted (blue), the stage colour tracks the live
  slide. Picking a favourite (or a manual colour) holds that colour and dims this button; click it
  again to go back to following the slide. It's **greyed out when ProPresenter isn't connected**
  (hover it to see why) — that's also your connection indicator.
- **Output:** toggle — turns stage-lighting control on/off. This is the only control you need
  mid-service.
- **Colour circle** (far right) — shows the colour currently being sent (dimmed while off).

**Drag** any empty part of the pill to move it; the position is remembered (and kept on-screen).

### Colour modes

- **Follow slide** (default) — the stage colour is extracted from each live slide.
- **Manual** — left-clicking a favourite, or saving a colour from the picker, holds that exact
  colour and ignores slide changes until you press **Follow slide** again.

## First-time setup

Click the **cog** → **Settings…**

| Setting | Notes |
|---|---|
| ProPresenter host/port | Usually `127.0.0.1` and the port from Preferences → Network |
| Protocol | Art-Net, sACN, or Enttec USB Pro |
| Target IP | Blank = broadcast (Art-Net) / multicast (sACN). Click **Scan…** to search the network (Art-Net discovery) and pick your node from a list |
| Universe | Art-Net counts from 0, sACN counts from 1 |
| Start channel | First channel of your RGB fixture (1–512). Layout is R, G, B — or Dimmer, R, G, B if you tick the master-dimmer box |
| Fade time | How long colour changes take (ms) |
| Brightness | Master intensity of the output |
| Saturation boost | Makes washed-out slide colours punchier on stage |
| Full intensity | Always drive lights at full brightness regardless of how dark the slide is (recommended) |
| Colourless slides | What to do for black/white slides: keep last colour (default), fade off, or warm white |

Then hit **Test DMX** (bottom-left of the Settings window) — the lights should run a rainbow sweep.
If they do, you're patched correctly.

## Good to know

- Colours come from the **slide thumbnail**. If your lyric slides are transparent text with the
  motion background triggered on ProPresenter's *media layer*, the slide itself has no colour —
  the "colourless slides" fallback rule applies. Slides/looks with built-in backgrounds work best.
- When toggled **off**, the app stops sending DMX entirely (it doesn't send blackout), so a
  console merging via HTP/LTP will simply take back control.
- Settings and your ten favourite colours live in `%APPDATA%\SlideColours\settings.json`.

## Deploying to another PC

A clean, fully-updated Windows 11 does **not** include what this app needs — it's a **.NET 8**
app, and Windows only ships with the unrelated **.NET Framework 4.8**. You have two choices:

- **`dist\SlideColours.exe`** (small, ~1 MB) needs the **.NET 8 Desktop Runtime (x64)** installed on
  the target machine — a one-time [download from Microsoft](https://dotnet.microsoft.com/download/dotnet/8.0)
  that then stays patched via Windows Update.
- **`dist-standalone\SlideColours.exe`** (~150 MB) is fully self-contained — it bundles the runtime,
  so it runs on a bare Windows 11 with **nothing preinstalled**. Just copy that one file across.
  You update it by re-copying the file (it won't get runtime patches automatically).

For a shared or locked-down machine (e.g. a church booth PC), the self-contained exe is usually the
least hassle.

## Building from source

Framework-dependent (small, needs the .NET 8 Desktop Runtime on the target machine):

```powershell
dotnet publish SlideColours\SlideColours.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o dist
```

Fully self-contained single file (runs anywhere, no runtime install needed):

```powershell
dotnet publish SlideColours\SlideColours.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=none -o dist-standalone
```
