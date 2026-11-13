# Agent Watch

A tiny Windows tray app that answers one question: **which AI agent is running on my PC right now, what is it doing, and is anything controlling my screen?**

If a computer-use window suddenly pops up, Agent Watch tells you who it was (Codex? OpenBot's Gemini agent? Claude?), when, and which process did it — and keeps a history so you can look back afterwards.

- **v2 is a WPF app** (sources in `src/`), built with the .NET Framework compiler that ships with Windows (`csc.exe`). No installs, no admin.
- Low overhead: one `NtQuerySystemInformation` snapshot every 2.5 s. In the tray / minimized it uses about 0.5% of one core; with the window open and the eyes animating it costs a few % of one core (animations stop when the window is hidden or minimized).
- Frameless, fully themed window: custom title bar (drag, double-click to maximize, Windows 11 snap layouts on the maximize button), resizable edges, themed scrollbars, splitters and headers. The window is a per-pixel-alpha window with rounded corners (that is what lets the blur show through - see "Why the blur needed a layered window" below).

## v2 highlights

- **Drill-down tree**: every app row expands/collapses (arrow, double-click, Right/Left keys; *Expand all* / *Collapse all*). Click any row for full details: what it is, parent chain, command line (secrets blanked), CPU/RAM, open windows, network connections (IPv4), start time, risk and the reason. Actions: *Kill process*, *Kill whole app tree* (both always ask first in a themed confirm), *Open file location*, *Copy details*, *Bring window to front*.
- **Appearance panel** (gear icon or the Appearance button, saved to `settings.json`; the flyout has its own opaque backing so nothing shows through it): colour presets (Indigo Night default, Synthwave, Aurora, Neon Cyber, Ocean Deep, Ember, Forest Glow, Vaporwave, Midnight Gold, Ultraviolet, Toxic Lime, Rose Quartz, plus monochrome Pure Black, Charcoal, Graphite, Slate Gray, Silver, Light Gray, Paper White), surface finishes (Matte, Glossy, Satin, Glass/Acrylic), **panel transparency** (0 = nearly solid cards, 100 = cards almost clear; above 40 the text gets a soft shadow so it stays readable), **blur behind the window** (0 = off / solid background; 1-100 = Windows blurs whatever is behind the window and the slider sets how much of that blur shows through the background - 100 is mostly blur, low values are mostly solid tint; it stays on when the window is not focused), custom accent / panel / text colours, eyes follow the mouse, and a reduced-motion switch (turns off the shakes and the glint). The background gradient is fixed and very subtle. A **glint** - a wide, soft, feathered band of light - sweeps across every 90-180 s (never in the first minute), takes 4-5 s, comes in from a random side or corner (never the same one twice in a row) at a random 15-75 degree angle, and is about half as bright as before. Test alert > Glint shows one sweep on demand.
- **Live header** (about 2.5x taller than before): the eyes and the WATCHING / BOT ACTIVE / DANGER / PAUSED pill on the left, the status text, and a tidy grid of live tiles with small sparklines, refreshed every 2 s while the window is visible (the sampler sleeps when it is hidden or minimized):
  - **CPU** total %, **Memory** used / total.
  - **One tile per NVIDIA GPU** - utilisation %, temperature, VRAM used / total (sparkline: utilisation, plus VRAM % in the second colour). Read through NVML (`nvml.dll`, installed with the NVIDIA driver) - no `nvidia-smi` process and no WMI. A card that is removed simply disappears from the grid; no NVIDIA driver shows "No NVIDIA GPU found".
  - **Network** down / up across all active adapters (Hyper-V virtual adapters are skipped so traffic is not counted twice), **Disk I/O** read / write across all physical disks (two reused performance counters), and **Free space** bars for C:, D: and I: (checked every 20 s).
  - Action buttons on the right: **Pause / Resume watching**, **Test alert** (menu: bot detected, danger, glint, bored, rage, asleep, poke), **Open logs**, **Kill selected...** (always asks first), **Snapshot** (saves `snapshot-<time>.png` of the window plus `snapshot-<time>-status.txt` to the log folder), **Task Manager**, **Appearance**. Buttons glow when pressed.
- **Mascot eyes** in the header – two soft black-shaded capsule slits on an accent face plate (no pupils or whites); all emotion comes from slit height, width, tilt, lids and position:
  - *Idle*: random blinks, occasional squint, slits slide toward the mouse.
  - *Bored* (no bot activity and the mouse still for ~50 s): half-lidded, slow drift, sigh-like slow blinks.
  - *Falling asleep* (while bored): the slits droop and close fully, then slowly shrink away, and just before vanishing snap awake with a jolt, a widen and a quick double blink. Repeats every 25–40 s while you stay away.
  - *Mad* (still for ~2.5 min): slits go thin and red-tinted in a sharp angry V and the whole window content shakes hard for about a second (eased back exactly to rest; the OS window never moves), then back to bored. The reduced-motion switch turns the shake off. Moving the mouse snaps them back with a quick surprised widen.
  - *Poked* (click the eyes): wince + flinch, then a narrowed glare at the cursor; a 2nd poke within 4 s glares harder with a slight V, the 3rd goes full mad with the stomp. During a bot/danger state a poke only causes a brief wince.
  - *Bot detected*: amber-tinted slits, rapid jittery blinks, shifted toward the controlled/launched window; live facts beside the eyes (agent, target app and window, duration, process, CPU, latest action).
  - *Danger*: slits grow and glow pulsing red, pointing at the window, plus a popup with *Kill process* / *Kill app tree* (ask first), *Bring to front* and *Ignore*. Never kills anything on its own.
  - *Test alert* buttons (Appearance panel and tray menu) preview bot detected, danger, bored, falling asleep, rage shake and poked.
- **Danger rules** live in the `danger` array of `explanations.json` (browser login/cookie DBs, credential stores, mass delete/rename of user folders, turning Defender/firewall off, startup persistence, download-and-execute, remote-access tools, a screen controller driving a banking or password-manager window). They are checked against agent-tree command lines and process names, plus the foreground window title while a screen episode is open – conservative, command-line based (no file-handle scanning).

## What you see

- **Apps an agent opened, front and centre**: any program started by an agent (e.g. Blender launched by OpenBot's Gemini agent through PowerShell) is attributed to the specific sub-agent by walking the parent chain. The banner and sentence read *"OpenBot's Gemini agent opened Blender at 9:55 PM"*, the *Doing now* column reads *"Using Blender (opened 9:55 PM by OpenBot (Gemini))"*, a toast says *"OpenBot (Gemini) opened Blender"*, and the history gets an *Opened Blender* row (several copies started together are grouped into one row). While a screen helper is active the banner says *"OpenBot (Gemini) is controlling Blender"* (the target is the foreground app, falling back to the app the agent launched most recently).
- **Shell commands in plain words**: an agent's PowerShell / cmd / bash processes show a short, redacted summary, e.g. *"Running a PowerShell script: render.ps1"* or *"Running a PowerShell command: Start-Process blender ..."*.
- **Banner – "Who's controlling the screen"**: turns red and names the app and agent (e.g. `OpenBot - OpenBot agent: Gemini (Antigravity)`) the moment a screen-control helper becomes active, with a toast notification. Otherwise shows the last time it happened.
- **Summary** (default): one row per AI agent app with a risk badge, a *Doing now* line (Idle / Working / Running tools / CONTROLLING THE SCREEN) and a plain-English sentence.
- **Show details**: the full process tree for every agent (PID, start time, CPU, RAM, command line with secrets redacted). Right-click or use *Kill selected process…* (always asks first; nothing is ever killed automatically).
- **Explanation pane**: select any row to read what it is, why it is running, how risky it is and what to do. Unknown processes get an *Add note* button that adds a stub rule to `explanations.json`.
- **Screen-control history**: every episode with start/end time, agent, process, what set it off, and what it was doing (OpenBot: `cua-driver.log` and chat-turn events from `trace.ndjson`).

## Source layout

- `src/` – the app (engine, window, tree, details, appearance, danger rules UI).
- `Eyes/` – everything about the mascot eyes: rendering and shading (`EyesControl.cs`), moods and animations (`EyeMoods.cs`), tray/window icons (`EyeIcon.cs`), `AgentWatch.ico` and its generator `make_icon.py`. See `Eyes/README.md`. `build.ps1` compiles both folders.
- A read-only mirror of `Eyes/` is kept at `D:\TOOLS\AgentWatch-Eyes-Safe\` (outside the build), in addition to git.

## Design rules

- **The eyes always keep their shaded centre.** Each eye is a vertical capsule slit with dark edges and a lighter, softly lit middle plus a faint centre glint – in every state (idle, bored, asleep, mad/rage, nervous amber, danger red, poked) and in every icon (`Eyes/AgentWatch.ico`, the taskbar/window icon, the tray icon, the Desktop and Startup shortcuts). Never flat solid black: that is what OpenBot / Grok Bot do, and Agent Watch's eyes must stay distinct.
- No pupils, irises or whites: all emotion comes from the slit's height, width, tilt, lids and position.
- No white native chrome anywhere; scrollbars, headers, splitters and dialogs are themed.
- Never kill anything automatically; kill buttons always ask first.

## Risk levels

| Level | Meaning |
|---|---|
| **RED** | Something is controlling or scanning the screen right now (screen-control helper using CPU, spawning processes, or OpenBot's `cua-driver.log` written in the last ~30 s), or an incoming Remote Desktop session. |
| **YELLOW** | An app an agent opened (never green; red while a screen helper is active). Can act on your behalf: agent engines, shells an agent opened, MCP tool servers, network tunnels — or a screen-control helper that is *loaded but idle*. |
| **GREEN** | Normal helper (app windows, renderers, consoles). |

The tray icon (the same mascot eyes on a rounded tile) follows the highest level: violet (nothing), green, amber, blinking red.

## Run it

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1     # builds AgentWatch.exe next to the source
powershell -ExecutionPolicy Bypass -File .\install.ps1   # Startup + Desktop shortcuts (optional)
.\AgentWatch.exe                                         # opens the window; --hidden starts in the tray
```

- Click the tray icon to show/hide the window. Closing the window keeps it in the tray; use the tray menu to exit.
- If the window was open when Agent Watch last ran, it opens again on the next start (state in `settings.json`, local only).
- `AgentWatch.exe --screenshot [normal|details|appearance|nervous|danger|bored|asleep|waking|mad|rage|poked|poked2|glint|theme:<preset>[:<finish>]|blur:<0-100>[:<transparency>]]` (while running) saves `screenshot[-mode].png` of the window to the log folder. Prefix a mode with `real:` (for example `real:blur:100`) to grab the actual screen pixels instead, which is the only way to see the blur behind. `blur:` and `theme:` change the look for the picture only and put your settings back. `--screenshot measure` shows the window for 30 s at the bottom of the window stack (no focus taken) and logs Agent Watch's own CPU use to events.log.

## Autostart

`install.ps1` puts `Agent Watch.lnk` (`AgentWatch.exe --hidden`) in your user Startup folder. It is a windowless app, so no console flashes. Remove the shortcut to disable autostart.

## Logs

Default folder: `D:\DEBUGGING LOGS\AgentWatch` (change `Logger.Dir` in `src/Core.cs`).

- `events.log` – agent / screen-controller START, STOP, ACTIVE, IDLE, SCREEN START/END and KILLED lines with time, PID, parent chain, path and a short, redacted command line. Rotates at 5 MB.
- `events.log` also gets `APP | OPENED` / `APP | CLOSED` lines for apps agents launch.
- `screen-episodes.tsv` – one line per screen-control or app episode (reloaded into the history panel on start).
- `status.txt` – a snapshot rewritten every 30 s (own CPU as % of machine and % of one core, UI fps, eye state).
- `events.log` gets `DANGER | rule | title | agent > process PID | snippet` lines for danger hits.

## Extending `explanations.json`

The knowledge file sits next to the exe and is reloaded automatically when it changes. Rules are checked top to bottom; the first match wins, so put specific rules above general ones.

```json
{
  "id": "my-agent",
  "category": "agent",
  "name": "^myagent\\.exe$",
  "path": "\\\\MyAgent\\\\",
  "cmd": "--serve",
  "parent": "^explorer\\.exe$",
  "app": "My Agent",
  "agent": true,
  "friendly": "My Agent",
  "risk": "yellow",
  "what": "What it does, in plain English.",
  "why": "Why it is running.",
  "todo": "What to do about it."
}
```

- `name`, `path`, `cmd`, `parent`: optional, case-insensitive .NET regexes against the exe name, full path, command line and parent exe name. All given fields must match.
- `risk`: `green`, `yellow`, or `screen` (screen-control helper: Yellow while idle, Red when active).
- `category`: `agent`, `mcp`, `screen-control`, `model-server` or `service`. The top-level `showCategories` list chooses what is shown (default `agent`, `mcp`, `screen-control`); *Show model servers* adds `model-server`. Anything with a screen-control helper is always shown.
- `agent: true`: makes a matching process appear as its own app in the summary.

## Privacy

Command lines are redacted before they are shown or logged (anything that looks like a key, token, secret, password, bearer header, credential URL or long random string). Agent Watch never reads credential files and makes no network connections.


## Why the blur needed a layered window

The old version asked Windows 11 for its built-in Mica / Acrylic backdrop (DWMWA_SYSTEMBACKDROP_TYPE). On this PC that never showed anything: a normal WPF window paints its own opaque surface over the backdrop, and Windows also turns those system backdrops into a flat colour whenever the window is not focused - which for a monitor app is nearly always. A side-by-side test of five window types confirmed it: only a per-pixel-alpha (AllowsTransparency) window with the acrylic blur-behind accent actually showed the blurred desktop, focused or not. Agent Watch now uses exactly that. The blur slider controls how much of the blurred desktop comes through the window background; the panel transparency slider controls how much comes through the cards.
