# Agent Watch – Eyes

Everything about Agent Watch's mascot eyes lives in this folder, apart from the rest of the app, so it can't be lost by accident. A read-only mirror is kept at `D:\TOOLS\AgentWatch-Eyes-Safe\`, and the folder is in git.

| File | What it holds |
|---|---|
| `EyesControl.cs` | Rendering and geometry: face plate, the two capsule slits, shading brushes, glow, curved lids (clip), palette (`SetPalette`), colour helpers, cursor P/Invoke. |
| `EyeMoods.cs` | `EyeMode` / `Mood` enums and the whole animation state machine (`Frame`): blinks, squints, look-around, mouse-follow, bored, asleep/waking, mad + rage shake, poked, nervous, danger, previews. |
| `EyeIcon.cs` | Tray icons drawn at runtime with the same eyes, and the window/taskbar icon loaded from the embedded `AgentWatch.ico`. |
| `AgentWatch.ico` | Multi-size icon (16, 20, 24, 32, 40, 48, 64, 128, 256 px) used for the exe, window, taskbar and the Desktop/Startup shortcuts (`build.ps1` passes it to `csc /win32icon` and embeds it). |
| `make_icon.py` | Regenerates `AgentWatch.ico` (Pillow): `python make_icon.py [preview.png]`. |

## API used by the app

```csharp
var eyes = new Eyes(canvas);                 // canvas about 176 x 82; the eyes draw into it and handle clicks (pokes)
eyes.SetPalette(accent, accent2, isLight);   // on every theme change
eyes.Mode = EyeMode.Idle | Nervous | Danger; // set by the app from what it detects
eyes.Target = screenRect;                    // window to look at while Nervous/Danger (null = look at the facts)
eyes.Follow = true; eyes.AllowShake = true;  // settings: follow the mouse / reduced motion off
eyes.Frame(seconds);                         // ~25 fps while visible; stop the timer when hidden or minimized
layoutShake.X = eyes.RootShakeX; ...Y;       // rage shake offset for the window content (0 when calm)
eyes.Poke(); eyes.PreviewMood(Mood.Bored, 12); eyes.PreviewMood(Mood.Mad, 8); eyes.PreviewSleep(); // test alerts
eyes.CurrentMood                             // for status / frame-rate decisions
```

## Design rules (permanent)

- **Soft original centre shading, always.** Each eye is a near-black capsule with darker edges and a gentle, softly lighter middle (body gradient `#040307 → #121019 → #34303F → #121019 → #040307`) plus a faint centre glint (white, alpha 30). That holds in every state and in every icon. Keep this original intensity: don't make it stronger.
- **Never flat solid black.** That's what OpenBot / Grok Bot do; ours must stay distinct.
- **No pupils, irises, corneas or whites.** All emotion comes from the slit shape: height (blink = squash to a line), width, tilt, curved lids clipping top/bottom, and where the slits sit (the whole slit shifts toward the mouse or target window).
- Slits sit on an accent-coloured rounded face plate (lightened on light themes so the black stays readable); the glow colour follows the state.
- Motion respects the reduced-motion setting (`AllowShake = false` turns off the header stomp and the window shake).

## Moods and timings

| State | Trigger | Look |
|---|---|---|
| Idle | default | random blinks every 2.5–7 s (12% double), squint every 14–40 s (1.6 s), slits follow the cursor; after 5 s without mouse movement they look around (every 1.4–3.2 s) |
| Surprised | mouse moves while bored/mad | quick widen (+30% width, +20% height) easing out over 0.5 s |
| Bored | no bot and the mouse still for 50 s | half-lidded (height 62%, soft top lid), slow drift every 3–6 s, sigh-like 0.75 s blinks every 4–8 s |
| Falling asleep | 12–20 s into bored, then every 25–40 s | 0–2.6 s droop and close fully, 2.6–7.5 s shrink away to ~12%, then snap awake: jolt, widen, quick double blink (0.1 s) |
| Mad / rage | mouse still for 150 s (repeats every 150 s), or a 3rd poke | very thin (−45% width), red-tinted, sharp 24° V with the inner lids lower; at 0.6 s the whole window content shakes for 1 s (random offset ~11 px decaying to 0, restored exactly) plus a header stomp; lasts 4.2 s then back to bored |
| Poked | click the eyes | 0.32 s wince (squeeze shut, +18% width, flinch 8% smaller and 3 px up), then a narrowed glare at the cursor until 2.5 s; 2nd poke within 4 s: harder glare with an 11° V until 3.1 s; 3rd: full mad. During Nervous/Danger a poke only winces |
| Nervous (bot detected) | app sets `EyeMode.Nervous` | amber-tinted slits, slightly narrow, rapid blinks every 0.35–1.3 s (45% double), jitter every 0.18–0.5 s, pointing at the controlled/launched window |
| Danger | app sets `EyeMode.Danger` | slits +20% width / +16% height, eye group 6% bigger, red tint and a pulsing red glow (6 rad/s), pointing at the window; the app shows the kill popup |
