# Agent Watch

A tiny Windows tray app that answers one question: **which AI agent is running on my PC right now, what is it doing, and is anything controlling my screen?**

If a computer-use window suddenly pops up, Agent Watch tells you who it was (Codex? OpenBot's Gemini agent? Claude?), when, and which process did it — and keeps a history so you can look back afterwards.

- Single C# file, built with the .NET Framework compiler that ships with Windows (`csc.exe`). No installs, no admin.
- Low overhead: one `NtQuerySystemInformation` snapshot every 2.5 s (~0.1–0.5% CPU, ~20 MB RAM).
- Deep indigo/blue/purple theme.

## What you see

- **Apps an agent opened, front and centre**: any program started by an agent (e.g. Blender launched by OpenBot's Gemini agent through PowerShell) is attributed to the specific sub-agent by walking the parent chain. The banner and sentence read *"OpenBot's Gemini agent opened Blender at 9:55 PM"*, the *Doing now* column reads *"Using Blender (opened 9:55 PM by OpenBot (Gemini))"*, a toast says *"OpenBot (Gemini) opened Blender"*, and the history gets an *Opened Blender* row (several copies started together are grouped into one row). While a screen helper is active the banner says *"OpenBot (Gemini) is controlling Blender"* (the target is the foreground app, falling back to the app the agent launched most recently).
- **Shell commands in plain words**: an agent's PowerShell / cmd / bash processes show a short, redacted summary, e.g. *"Running a PowerShell script: render.ps1"* or *"Running a PowerShell command: Start-Process blender ..."*.
- **Banner – "Who's controlling the screen"**: turns red and names the app and agent (e.g. `OpenBot - OpenBot agent: Gemini (Antigravity)`) the moment a screen-control helper becomes active, with a toast notification. Otherwise shows the last time it happened.
- **Summary** (default): one row per AI agent app with a risk badge, a *Doing now* line (Idle / Working / Running tools / CONTROLLING THE SCREEN) and a plain-English sentence.
- **Show details**: the full process tree for every agent (PID, start time, CPU, RAM, command line with secrets redacted). Right-click or use *Kill selected process…* (always asks first; nothing is ever killed automatically).
- **Explanation pane**: select any row to read what it is, why it is running, how risky it is and what to do. Unknown processes get an *Add note* button that adds a stub rule to `explanations.json`.
- **Screen-control history**: every episode with start/end time, agent, process, what set it off, and what it was doing (OpenBot: `cua-driver.log` and chat-turn events from `trace.ndjson`).

## Risk levels

| Level | Meaning |
|---|---|
| **RED** | Something is controlling or scanning the screen right now (screen-control helper using CPU, spawning processes, or OpenBot's `cua-driver.log` written in the last ~30 s), or an incoming Remote Desktop session. |
| **YELLOW** | An app an agent opened (never green; red while a screen helper is active). Can act on your behalf: agent engines, shells an agent opened, MCP tool servers, network tunnels — or a screen-control helper that is *loaded but idle*. |
| **GREEN** | Normal helper (app windows, renderers, consoles). |

The tray icon follows the highest level: indigo (nothing), green, amber, blinking red.

## Run it

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1     # builds AgentWatch.exe next to the source
powershell -ExecutionPolicy Bypass -File .\install.ps1   # Startup + Desktop shortcuts (optional)
.\AgentWatch.exe                                         # opens the window; --hidden starts in the tray
```

- Click the tray icon to show/hide the window. Closing the window keeps it in the tray; use the tray menu to exit.
- If the window was open when Agent Watch last ran, it opens again on the next start (state in `settings.json`, local only).
- `AgentWatch.exe --screenshot` (while running) saves a screenshot of the window to the log folder.

## Autostart

`install.ps1` puts `Agent Watch.lnk` (`AgentWatch.exe --hidden`) in your user Startup folder. It is a windowless app, so no console flashes. Remove the shortcut to disable autostart.

## Logs

Default folder: `D:\DEBUGGING LOGS\AgentWatch` (change `Logger.Dir` in `AgentWatch.cs`).

- `events.log` – agent / screen-controller START, STOP, ACTIVE, IDLE, SCREEN START/END and KILLED lines with time, PID, parent chain, path and a short, redacted command line. Rotates at 5 MB.
- `events.log` also gets `APP | OPENED` / `APP | CLOSED` lines for apps agents launch.
- `screen-episodes.tsv` – one line per screen-control or app episode (reloaded into the history panel on start).
- `status.txt` – a snapshot rewritten every 30 s.

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
