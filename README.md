# Slide Colours

Matches your stage lighting to what's on screen. Slide Colours takes the dominant colour of what
ProPresenter is showing and streams it to your DMX rig — with a small floating on/off toggle you
can park anywhere on screen. It can follow either **the live slide** or **a display**, the latter
sampled many times a second so animated and video backgrounds drive the lights like a TV
backlight. It also doubles as a dead-simple standalone DMX colour controller when you're not
driving it from ProPresenter at all (see
[Using it as a plain DMX controller](#using-it-as-a-plain-dmx-controller)).

<p align="center">
  <img src="docs/images/pill.png" width="760"
       alt="The floating Slide Colours pill: close, settings cog, ten favourite-colour swatches, Follow slide and Follow screen buttons, an Output toggle, and the current-colour circle">
</p>

## Download

**[⬇ Download the latest release](https://github.com/tango7nz/slide-colours/releases/latest)** —
grab `SlideColours.exe` and double-click it. It's fully self-contained, so there's
**nothing to install** (no .NET runtime needed) — just 64-bit Windows 10/11.

> On first launch, Windows SmartScreen may warn about an unrecognised app (the exe is unsigned).
> Click **More info → Run anyway**.

## How it works

There are two ways to get the colour, and you switch between them on the pill:

**Follow slide** — the original approach, and the lightest.

1. Listens to ProPresenter's local HTTP API for slide changes (streaming, no polling).
2. When the slide changes, fetches that slide's thumbnail and finds its dominant colour —
   biased towards vivid, saturated pixels so white lyric text and black backgrounds are ignored.

**Follow screen** — for animated backgrounds.

1. Captures a display you choose (normally the one carrying the audience/main output) using
   Windows' Desktop Duplication API, downscaling each frame on the GPU so only a few KB come
   back to the CPU. It's event-driven, so a still slide costs nothing.
2. Runs the same colour extraction on every frame, then smooths the result over time so the
   lights glide with the picture instead of stepping between hues.

Either way, the stage colour fades smoothly and DMX is transmitted ~30×/second while the toggle
is on. When toggled off, it stops transmitting so your lighting desk is back in charge.

## Requirements

- Windows 10/11 (64-bit). The [downloadable release](#download) is self-contained and needs
  nothing else installed. (Building it yourself framework-dependent instead needs the **.NET 8
  Desktop Runtime** — see [Building from source](#building-from-source).)
- For **Follow slide**: **ProPresenter 7.9 or later** with the network API enabled:
  *ProPresenter → Preferences → Network → tick **Enable Network***, then note the **port** shown there.
- For **Follow screen**: nothing extra — it uses a display already attached to this PC. (The
  screen must be connected and awake; see [Good to know](#good-to-know).)
- A DMX route, one of:
  - **Art-Net** node/gateway on the network (default)
  - **sACN (E1.31)** node on the network
  - **Enttec DMX USB Pro** (or compatible) on a COM port

## Running it

Launch **SlideColours** (the exe you downloaded). A small floating pill appears (top-right by
default). From left to right:

- **✕ Close** — quits the app.
- **Cog** — opens *Settings…*.
- **Favourite colours** — ten preset swatches:
  - **Left-click** to send that colour to the lights (this switches to *manual* mode — see below).
  - **Right-click** to edit that swatch: a colour picker opens seeded with its current colour, and
    saving stores the new colour in that slot.
- **Follow slide** — track the live slide. When highlighted (blue), the stage colour changes with
  each new slide. It's **greyed out when ProPresenter isn't connected** (hover it to see why) —
  that's also your connection indicator.
- **Follow screen** — track a display continuously instead, so animated backgrounds and video
  come through. Highlighted blue while active; hover it to see which display it's watching, or
  why capture isn't working. Choose the display in **Settings**.
- Picking a favourite (or a manual colour) holds that colour and dims both buttons; click either
  one to start following again.
- **Output:** toggle — turns stage-lighting control on/off. This is the only control you need
  mid-service.
- **Colour circle** (far right) — shows the colour currently being sent (dimmed while off).

**Drag** any empty part of the pill to move it; the position is remembered (and kept on-screen).

### Colour modes

- **Follow slide** (default) — the stage colour is extracted from each live slide.
- **Follow screen** — the stage colour is sampled from a display many times a second, so it
  keeps moving while the background does. Use this for motion backgrounds, video, or any look
  whose colour isn't in the slide thumbnail.
- **Manual** — left-clicking a favourite, or saving a colour from the picker, holds that exact
  colour and ignores both sources until you press a Follow button again.

Whichever mode you leave it in is remembered for next launch.

#### Which one should I use?

**Follow slide** is the lighter option and reacts exactly when the slide changes — ideal if your
slides carry their own solid or gradient backgrounds. **Follow screen** costs a few percent of
one CPU core but is the only one that can see a moving background, and it doesn't care how the
look was built. If lyrics are transparent text over a motion background on ProPresenter's media
layer, **Follow screen** is the one you want — the slide thumbnail alone has no colour in it.

## Using it as a plain DMX controller

You don't need ProPresenter at all. Manual colour control talks straight to your DMX rig, so the
app works on its own as a tiny always-on-top colour controller — handy for a static wash, quick
colour checks, or venues that don't run ProPresenter:

- Point it at your fixture once in **Settings** (protocol, universe, start channel — same as below).
- **Left-click a favourite** to send that colour, or **right-click** one to pick any colour you like.
  These drive the lights immediately; there's no slide involved.
- Flip the **Output** toggle on to transmit, off to hand control back to your desk.

The only thing that needs ProPresenter is the **Follow slide** button — it stays greyed out when
ProPresenter isn't connected, but everything else keeps working. So you can install it purely as a
set-and-forget RGB controller and never enable the network API. **Follow screen** works without
ProPresenter too: point it at any display and the lights follow whatever is on it.

## First-time setup

Click the **cog** → **Settings…**

<p align="center">
  <img src="docs/images/settings.png" width="380"
       alt="The Slide Colours Settings window: ProPresenter connection, DMX output, colour behaviour, and update options">
</p>

| Setting | Notes |
|---|---|
| ProPresenter host/port | Usually `127.0.0.1` and the port from Preferences → Network |
| Protocol | Art-Net, sACN, or Enttec USB Pro |
| Target IP | Blank = broadcast (Art-Net) / multicast (sACN). Click **Scan…** to search the network (Art-Net discovery) and pick your node from a list |
| Universe | Art-Net counts from 0, sACN counts from 1 |
| Start channel | First channel of your RGB fixture (1–512). Layout is R, G, B — or Dimmer, R, G, B if you tick the master-dimmer box |
| Display | Which screen **Follow screen** samples. Leave on *Main display* to always use the primary screen, or pick a specific one — normally whichever shows the audience/main output |
| Smoothing | How quickly the lights chase a moving picture (ms). Higher is calmer, lower is snappier. 400 is a good starting point |
| Sample rate | Frames per second sampled from the display. 30 is plenty; drop to 15 on a weak PC |
| Fade time | How long a **slide change** takes to fade (ms). Follow screen uses Smoothing instead |
| Brightness | Master intensity of the output |
| Saturation boost | Makes washed-out slide colours punchier on stage |
| Full intensity | Always drive lights at full brightness regardless of how dark the slide is (recommended) |
| No colour found | What to do for black/white slides or screens: keep last colour (default), fade off, or warm white |

Then hit **Test DMX** (bottom-left of the Settings window) — the lights should run a rainbow sweep.
If they do, you're patched correctly.

## Good to know

- In **Follow slide**, colours come from the **slide thumbnail**. If your lyric slides are
  transparent text with the motion background triggered on ProPresenter's *media layer*, the
  slide itself has no colour and the "no colour found" fallback applies. Switch to
  **Follow screen** and it just works, because that samples the finished picture.
- **Follow screen** needs the display **connected and awake**. It can't capture a projector
  that's switched off, a screen that's asleep, or a machine you're only viewing over Remote
  Desktop. If capture drops out (resolution change, a full-screen app taking over, the lock
  screen), it reconnects by itself within a couple of seconds.
- On a laptop with switchable graphics, Windows may put Slide Colours on a different GPU from
  the one driving the output screen, which blocks capture. The hover tooltip on **Follow screen**
  says so if it happens; fix it in *Windows Settings → System → Display → Graphics*.
- When toggled **off**, the app stops sending DMX entirely (it doesn't send blackout), so a
  console merging via HTP/LTP will simply take back control.
- Settings and your ten favourite colours live in `%APPDATA%\SlideColours\settings.json`.

## Deploying to another PC

For almost everyone this just means: **download the self-contained exe from the
[latest release](https://github.com/tango7nz/slide-colours/releases/latest) and copy that one file
across.** It bundles the .NET 8 runtime, so it runs on a bare, clean Windows 10/11 with **nothing
preinstalled** — ideal for a shared or locked-down machine like a church booth PC. To update it,
replace the file with a newer release.

Prefer a tiny (~1 MB) download instead? Build the *framework-dependent* variant yourself (see
[Building from source](#building-from-source)). It needs the
[.NET 8 Desktop Runtime (x64)](https://dotnet.microsoft.com/download/dotnet/8.0) installed on the
target machine — Windows ships only the unrelated .NET Framework 4.8 — but then stays patched via
Windows Update.

## Building from source

Framework-dependent (small, needs the .NET 8 Desktop Runtime on the target machine):

```powershell
dotnet publish SlideColours\SlideColours.csproj -c Release -r win-x64 --self-contained false /p:PublishSingleFile=true -o dist
```

Fully self-contained single file (runs anywhere, no runtime install needed):

```powershell
dotnet publish SlideColours\SlideColours.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=none -o dist-standalone
```

## Feedback

Slide Colours is free for anyone to use — churches, venues, hobbyists, whoever finds it useful.
If you *do* use it, I'd genuinely love to hear about it: what you're running it on, how it's
working out, or anything you'd like it to do. There's no obligation at all — just drop me a line
at **[hughburns@outlook.co.nz](mailto:hughburns@outlook.co.nz)**. Bug reports and ideas are also
welcome via [GitHub issues](https://github.com/tango7nz/slide-colours/issues).

## Support this project

Slide Colours is free, and always will be. If you find it useful and would like to give something
back, you're welcome to make a small donation towards our church's mission:
**<https://lifechurch.nz/church-life/#giving>**. It's entirely optional and there's no catch —
just a thank-you if you've found it useful.

## License

Released under the [MIT License](LICENSE) — free to use, modify, and share, including
commercially, with no warranty. © 2026 Hugh Burns.
