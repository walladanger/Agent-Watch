using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

// Agent Watch mascot eyes - moods and animation: idle (blink, squint, look-around, follow the mouse),
// bored, falling asleep / waking, mad + rage shake, poked, nervous (bot active), danger. See README.md.
namespace AgentWatch
{
    enum EyeMode { Idle, Nervous, Danger }   // driven by the app: nothing / bot detected / danger rule hit
    enum Mood { Normal, Bored, Mad }          // the eyes' own idle moods (only while EyeMode.Idle)

    partial class Eyes
    {
        public void PreviewMood(Mood m, double seconds) { forceMood = m; forceUntil = DateTime.Now.AddSeconds(seconds); forcePending = true; if (m != Mood.Bored) sleepStart = -1; }
        public void PreviewSleep() { PreviewMood(Mood.Bored, SleepLength + 3); sleepPending = true; }
        public void Poke() { pokePending++; }

        public void Frame(double t)
        {
            double dt = lastT > 0 ? Math.Min(0.2, t - lastT) : 0.04; lastT = t;
            if (forcePending) { forcePending = false; if (forceMood == Mood.Mad) { madStart = t; lastMadAt = t; } }
            bool forced = DateTime.Now < forceUntil;
            while (pokePending > 0)
            {
                pokePending--; pokes.Add(t); pokes.RemoveAll(x => x < t - 4); pokeLevel = pokes.Count; pokeAt = t;
                if (Mode == EyeMode.Idle && pokeLevel >= 3) { madStart = t; lastMadAt = t; pokeMad = true; }
            }
            if (pokeMad && (madStart < 0 || t - madStart > MadLength)) pokeMad = false;
            bool poking = t - pokeAt < 3.5;

            EyeNative.POINT cp; EyeNative.GetCursorPos(out cp);
            bool movedMouse = cp.X != lastCx || cp.Y != lastCy;
            if (movedMouse)
            {
                if (lastCx != int.MinValue && !forced && !poking && (CurrentMood != Mood.Normal)) surpriseStart = t;
                cursorStill = 0; lastCx = cp.X; lastCy = cp.Y;
                if (!forced && !pokeMad) { madStart = -1; lastMadAt = -1e9; }
            }
            else cursorStill += dt;

            Mood mood;
            if (forced) mood = forceMood == Mood.Mad && madStart >= 0 && t - madStart > MadLength ? Mood.Bored : forceMood;
            else if (Mode != EyeMode.Idle) { mood = Mood.Normal; madStart = -1; pokeMad = false; }
            else if (pokeMad) mood = Mood.Mad;
            else
            {
                if (cursorStill >= MadAfter && t - lastMadAt >= MadAfter) { madStart = t; lastMadAt = t; }
                if (madStart >= 0 && t - madStart <= MadLength) mood = Mood.Mad;
                else mood = cursorStill >= BoredAfter ? Mood.Bored : Mood.Normal;
            }
            if (sleepPending) { sleepPending = false; sleepStart = t; }
            if (mood == Mood.Bored && lastMood != Mood.Bored && sleepStart < 0) nextSleep = t + Rand(12, 20);
            if (mood != Mood.Bored) sleepStart = -1;
            else if (sleepStart < 0 && nextSleep > 0 && t > nextSleep && !forced) sleepStart = t;
            if (sleepStart >= 0 && t - sleepStart >= SleepLength)
            {
                // snap awake: jolt, widen, quick double blink, then carry on being bored
                sleepStart = -1; nextSleep = t + Rand(25, 40); joltAt = t; surpriseStart = t;
                blinkStart = t + 0.4; doubleBlink = true; blinkDur = 0.1;
            }
            lastMood = mood;
            CurrentMood = mood;
            if (shownMode != Mode || shownMood != mood) Recolor();

            Point eyeScreen;
            try { eyeScreen = host.PointToScreen(new Point(HW / 2, HH / 2)); } catch { return; }
            double dx, dy, dist;
            if (Mode != EyeMode.Idle && Target.HasValue)
            {
                var r = Target.Value; dx = r.X + r.Width / 2 - eyeScreen.X; dy = r.Y + r.Height / 2 - eyeScreen.Y;
                dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 300); dx = dx / dist * k; dy = dy / dist * k; }
            }
            else if (Mode != EyeMode.Idle) { dx = 0.75; dy = 0.15; } // look at the facts to the right
            else if (Mode == EyeMode.Idle && t - pokeAt < 3.2)
            {
                dx = cp.X - eyeScreen.X; dy = cp.Y - eyeScreen.Y; dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 260); dx = dx / dist * k * 0.8; dy = dy / dist * k * 0.8; }
            }
            else if (mood == Mood.Bored)
            {
                if (t > nextLook) { nextLook = t + Rand(3, 6); tx = Rand(-0.7, 0.7); ty = Rand(0.15, 0.6); }
                dx = tx; dy = ty;
                if (sleepStart >= 0) { dx = tx * 0.3; dy = 0.7; }
            }
            else if (Follow && (cursorStill < 5 || mood == Mood.Mad))
            {
                dx = cp.X - eyeScreen.X; dy = cp.Y - eyeScreen.Y; dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 420); dx = dx / dist * k; dy = dy / dist * k; }
                if (mood == Mood.Mad) { dx *= 0.4; dy *= 0.4; }
            }
            else
            {
                if (t > nextLook) { nextLook = t + Rand(1.4, 3.2); if (rnd.NextDouble() < 0.3) { tx = 0; ty = 0; } else { double a = Rand(0, Math.PI * 2), m = Rand(0.4, 1); tx = Math.Cos(a) * m; ty = Math.Sin(a) * m * 0.7; } }
                dx = tx; dy = ty;
            }
            if (Mode != EyeMode.Idle)
            {
                if (t > nextJit) { nextJit = t + (Mode == EyeMode.Danger ? Rand(0.12, 0.35) : Rand(0.18, 0.5)); jx = Rand(-0.14, 0.14); jy = Rand(-0.12, 0.12); }
                dx += jx; dy += jy;
            }
            double rate = Mode != EyeMode.Idle ? 16 : mood == Mood.Bored ? 1.4 : 9;
            gx += (dx * 22 - gx) * Math.Min(1, dt * rate);
            gy += (dy * 10 - gy) * Math.Min(1, dt * rate);

            // blinks: squash the slit to a line
            if (blinkStart < 0 && t > nextBlink && mood != Mood.Mad && sleepStart < 0)
            {
                blinkStart = t; doubleBlink = rnd.NextDouble() < (Mode == EyeMode.Nervous ? 0.45 : Mode == EyeMode.Danger ? 0.3 : 0.12);
                blinkDur = Mode == EyeMode.Nervous ? 0.12 : Mode == EyeMode.Danger ? 0.15 : mood == Mood.Bored ? 0.75 : 0.22;
            }
            double blink = 0;
            if (blinkStart >= 0)
            {
                double u = (t - blinkStart) / blinkDur;
                if (u >= 1)
                {
                    if (doubleBlink) { doubleBlink = false; blinkStart = t + 0.07; }
                    else
                    {
                        blinkStart = -1;
                        nextBlink = t + (Mode == EyeMode.Danger ? Rand(0.9, 2.2) : Mode == EyeMode.Nervous ? Rand(0.35, 1.3) : mood == Mood.Bored ? Rand(4, 8) : Rand(2.5, 7));
                    }
                }
                else if (u >= 0) blink = mood == Mood.Bored ? Math.Pow(Math.Sin(Math.PI * u), 0.7) : Math.Sin(Math.PI * u);
            }

            double sx = 1, sy = 1, rot = 0, to = 0, ti = 0, bo = 0, sk = 0, glowOp = 0.22, rsx = 0, rsy = 0;
            if (Mode == EyeMode.Danger) { sx = 1.2; sy = 1.16; double p = 0.5 + 0.5 * Math.Sin(t * 6); glowOp = 0.45 + 0.55 * p; }
            else if (Mode == EyeMode.Nervous) { sx = 0.9; sy = 0.94; to = ti = 0.06; glowOp = 0.55 + 0.15 * Math.Sin(t * 9); }
            else if (mood == Mood.Bored)
            {
                to = ti = 0.2 + 0.03 * Math.Sin(t * 0.9); sy = 0.62; glowOp = 0.1;
                if (sleepStart >= 0)
                {
                    double u = t - sleepStart;
                    if (u < 2.6) { double d = Ease(u / 2.6); to = ti = 0.2 + 0.25 * d; sy = 0.62 * (1 - 0.93 * d); }       // droop, then fully shut
                    else
                    {
                        double k = 1 - 0.88 * Ease((u - 2.6) / (SleepLength - 2.6));                           // drift off: shrink away
                        to = ti = 0.45; sy = 0.62 * 0.07 * k; sx = k; glowOp = 0.1 * k;
                    }
                }
            }
            else if (mood == Mood.Mad)
            {
                double u = t - madStart, a = Ease(u / 0.45) * (1 - Ease((u - (MadLength - 0.6)) / 0.6));
                // rage: very thin, sharp V, squinting hard
                rot = 24 * a; to = 0.08 * a; ti = 0.46 * a; sy = 1 - 0.3 * a; sx = 1 - 0.45 * a; glowOp = 0.25 + 0.45 * a;
                double su = u - 0.6;
                if (AllowShake && su >= 0 && su <= 1.0)
                {
                    double amp = 11 * Math.Pow(1 - su, 1.5);                                                 // the whole window shakes
                    rsx = (rnd.NextDouble() * 2 - 1) * amp; rsy = (rnd.NextDouble() * 2 - 1) * amp * 0.6;
                    sk = (1 - su) * 3 * Math.Sin(su * Math.PI * 2 * 14);
                }
            }
            else if (squintStart >= 0 || t > nextSquint)
            {
                if (squintStart < 0) squintStart = t;
                double u = (t - squintStart) / 1.6;
                if (u >= 1) { squintStart = -1; nextSquint = t + Rand(14, 40); }
                else { double q = 0.32 * Math.Sin(Math.PI * u); to = ti = q * 0.45; sy = 1 - 0.45 * q; }
            }
            // poke overlay: wince (squeeze shut + flinch), then a narrowed glare at the cursor
            double pu = t - pokeAt, recoil = 1, recoilY = 0;
            if (pu >= 0 && pu < 0.32)
            {
                double w = pu < 0.22 ? Math.Min(1, pu / 0.05) : 1 - (pu - 0.22) / 0.1;
                blink = Math.Max(blink, w); sx *= 1 + 0.18 * w; recoil = 1 - 0.08 * w; recoilY = -3 * w;
                to *= 1 - w; ti *= 1 - w; bo *= 1 - w;
            }
            else if (Mode == EyeMode.Idle && !pokeMad && pokeLevel < 3)
            {
                double end = pokeLevel >= 2 ? 3.1 : 2.5;
                if (pu >= 0.32 && pu < end)
                {
                    double g = Ease((pu - 0.32) / 0.18) * (1 - Ease((pu - (end - 0.45)) / 0.45));
                    if (pokeLevel >= 2) { rot = Math.Max(rot, 11 * g); ti = Math.Max(ti, 0.34 * g); to = Math.Max(to, 0.06 * g); sy *= 1 - 0.25 * g; }
                    else { ti = Math.Max(ti, 0.16 * g); to = Math.Max(to, 0.16 * g); sy *= 1 - 0.4 * g; }
                    sx *= 1 + 0.06 * g; glowOp = Math.Max(glowOp, 0.22 + 0.2 * g);
                }
            }
            double su2 = t - surpriseStart;
            if (su2 >= 0 && su2 < 0.5) { double k = Math.Pow(1 - su2 / 0.5, 2); sx *= 1 + 0.3 * k; sy *= 1 + 0.2 * k; to *= 1 - k; ti *= 1 - k; bo *= 1 - k; blink *= 1 - k; }
            sy *= 1 - 0.9 * blink;

            double targetScale = Mode == EyeMode.Danger ? 1.06 : 1;
            curScale += (targetScale - curScale) * Math.Min(1, dt * 6);
            double ju = t - joltAt;
            if (ju >= 0 && ju < 0.3) recoilY += -4 * (1 - ju / 0.3) * Math.Cos(ju * 40);
            RootShakeX = rsx; RootShakeY = rsy;
            double fs = curScale * recoil, shy = sk * 0.3 + recoilY;
            if (Math.Abs(scale.ScaleX - fs) > 0.002) { scale.ScaleX = fs; scale.ScaleY = fs; }
            if (Math.Abs(shake.X - sk) > 0.05 || Math.Abs(shake.Y - shy) > 0.05) { shake.X = sk; shake.Y = shy; }
            foreach (var e in eyes)
            {
                if (Math.Abs(e.T.X - gx) > 0.05 || Math.Abs(e.T.Y - gy) > 0.05) { e.T.X = gx; e.T.Y = gy; e.GT.X = gx; e.GT.Y = gy; }
                if (Math.Abs(e.S.ScaleX - sx) > 0.003) e.S.ScaleX = sx;
                if (Math.Abs(e.S.ScaleY - sy) > 0.003) e.S.ScaleY = sy;
                double r = rot * e.Side; if (Math.Abs(e.R.Angle - r) > 0.05) e.R.Angle = r;
                SetLids(e, to, ti, bo);
                if (Math.Abs(e.Glow.Opacity - glowOp) > 0.01) e.Glow.Opacity = glowOp;
            }
        }
        }
}
