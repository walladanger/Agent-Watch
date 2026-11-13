using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

// Agent Watch mascot eyes - rendering, geometry and shading. See README.md in this folder for the design rules.
// Small API used by the app:
//   var eyes = new Eyes(canvas);            // canvas ~176x82, the eyes draw themselves into it
//   eyes.SetPalette(accent, accent2, light) // call when the theme changes
//   eyes.Mode = EyeMode.Idle/Nervous/Danger; eyes.Target = screenRect or null; eyes.Follow; eyes.AllowShake
//   eyes.Frame(seconds)                     // call ~25x a second while visible (stop the timer when hidden)
//   eyes.RootShakeX / RootShakeY            // apply to the window layout for the rage shake (0 when calm)
//   eyes.Poke(), eyes.PreviewMood(mood, s), eyes.PreviewSleep(), eyes.CurrentMood
namespace AgentWatch
{

    // Mascot eyes: two soft black-shaded capsule slits on an accent face plate. No pupils, irises or whites -
    // every expression comes from the slit shape (height, width, tilt, lids) and where the slits sit.
    partial class Eyes
    {
        class One
        {
            public Grid Slit; public Rectangle Body, Shine; public Ellipse Glow; public int Side;
            public ScaleTransform S = new ScaleTransform(1, 1); public RotateTransform R = new RotateTransform(0);
            public TranslateTransform T = new TranslateTransform(), GT = new TranslateTransform();
            public double cTo = -1, cTi = -1, cB = -1;
        }
        const double SW = 22, SH = 54, HW = 176, HH = 82;
        Canvas host; Rectangle plate; One[] eyes = new One[2];
        ScaleTransform scale = new ScaleTransform(1, 1); TranslateTransform shake = new TranslateTransform();
        Random rnd = new Random();
        public EyeMode Mode = EyeMode.Idle; EyeMode shownMode = (EyeMode)(-1); Mood shownMood = (Mood)(-1);
        public Mood CurrentMood = Mood.Normal;
        public Rect? Target; // screen px
        public bool Follow = true;
        double gx, gy, tx, ty, blinkStart = -1, nextBlink = 2, squintStart = -1, nextSquint = 20, nextLook = 0, jx, jy, nextJit = 0, curScale = 1;
        double cursorStill = 0, lastT = 0, madStart = -1, lastMadAt = -1e9, surpriseStart = -1e9, blinkDur = 0.22;
        bool doubleBlink; int lastCx = int.MinValue, lastCy = int.MinValue;
        Mood forceMood = Mood.Normal; DateTime forceUntil = DateTime.MinValue; bool forcePending;
        // poking the eyes: wince, then an annoyed glare; repeated pokes escalate to the mad stomp
        List<double> pokes = new List<double>(); int pokePending, pokeLevel; double pokeAt = -1e9; bool pokeMad;
        // falling asleep while bored: droop shut, shrink away, then jolt awake
        double sleepStart = -1, nextSleep = -1, joltAt = -1e9; bool sleepPending; Mood lastMood = Mood.Normal;
        public const double SleepLength = 7.5;
        // whole-window rage shake offset (applied by the window to its layout); 0 when calm
        public double RootShakeX, RootShakeY; public bool AllowShake = true;
        public const double BoredAfter = 50, MadAfter = 150, MadLength = 4.2;

        // ---- palette supplied by the host app (accent colours + whether the theme is light) ----
        Color pAccent = Color.FromRgb(0x81, 0x8C, 0xF8), pAccent2 = Color.FromRgb(0xC0, 0x84, 0xFC); bool pLight;
        public void SetPalette(Color accent, Color accent2, bool lightTheme) { pAccent = accent; pAccent2 = accent2; pLight = lightTheme; Recolor(); }

        public Eyes(Canvas c)
        {
            host = c; var tg = new TransformGroup(); tg.Children.Add(scale); tg.Children.Add(shake);
            host.RenderTransform = tg; host.RenderTransformOrigin = new Point(0.5, 0.5);
            plate = new Rectangle { Width = HW - 8, Height = HH - 2, RadiusX = 26, RadiusY = 26, IsHitTestVisible = false };
            Canvas.SetLeft(plate, 4); Canvas.SetTop(plate, 1); host.Children.Add(plate);
            eyes[0] = Build(HW / 2 - 30, HH / 2, -1); eyes[1] = Build(HW / 2 + 30, HH / 2, 1);
            Recolor();
            host.Background = Brushes.Transparent; host.Cursor = Cursors.Hand;
            host.ToolTip = "Don't poke the bot in the eye.";
            host.MouseLeftButtonDown += delegate (object sender, MouseButtonEventArgs ev) { Poke(); ev.Handled = true; };
        }


        One Build(double cx, double cy, int side)
        {
            var e = new One { Side = side };
            e.Glow = new Ellipse { Width = 62, Height = 82, IsHitTestVisible = false, RenderTransform = e.GT };
            Canvas.SetLeft(e.Glow, cx - 31); Canvas.SetTop(e.Glow, cy - 41); host.Children.Add(e.Glow);
            e.Slit = new Grid { Width = SW, Height = SH, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
            var tg = new TransformGroup(); tg.Children.Add(e.S); tg.Children.Add(e.R); tg.Children.Add(e.T); e.Slit.RenderTransform = tg;
            e.Body = new Rectangle { RadiusX = SW / 2, RadiusY = SW / 2 };
            // DESIGN RULE: every eye keeps a lighter shaded centre (never flat solid black) in every state.
            // The shading lives in the body brush (dark edges, lit middle) and a soft centre glint, both scale with the slit.
            e.Shine = new Rectangle { Width = SW * 0.34, Height = SH * 0.42, RadiusX = SW * 0.17, RadiusY = SW * 0.17, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, -SH * 0.06, 0, 0) };
            var sh = new RadialGradientBrush(Color.FromArgb(30, 255, 255, 255), Color.FromArgb(0, 255, 255, 255)); sh.Freeze(); e.Shine.Fill = sh;
            e.Slit.Children.Add(e.Body); e.Slit.Children.Add(e.Shine);
            Canvas.SetLeft(e.Slit, cx - SW / 2); Canvas.SetTop(e.Slit, cy - SH / 2); host.Children.Add(e.Slit);
            return e;
        }


        void Recolor()
        {
            Color glow, tint; double tintK;
            if (Mode == EyeMode.Danger) { glow = EyeColor.Hex("#F43F5E"); tint = EyeColor.Hex("#E11D48"); tintK = 0.38; }
            else if (Mode == EyeMode.Nervous) { glow = EyeColor.Hex("#F59E0B"); tint = EyeColor.Hex("#F59E0B"); tintK = 0.3; }
            else if (CurrentMood == Mood.Mad) { glow = EyeColor.Hex("#F87171"); tint = EyeColor.Hex("#EF4444"); tintK = 0.28; }
            else { glow = EyeColor.Mix(pAccent, Colors.White, 0.45); tint = pAccent; tintK = 0; }
            Color pa = pAccent, pb = EyeColor.Mix(pAccent, pAccent2, 0.6);
            if (pLight) { pa = EyeColor.Mix(pa, Colors.White, 0.62); pb = EyeColor.Mix(pb, Colors.White, 0.5); } // keep the black slits readable on light presets
            Color a1 = EyeColor.Mix(pa, tint, tintK), a2 = EyeColor.Mix(pb, tint, tintK);
            var pg = new LinearGradientBrush(EyeColor.A(EyeColor.Mix(a1, Colors.White, 0.12), 240), EyeColor.A(EyeColor.Mix(a2, Colors.Black, 0.12), 240), 60); pg.Freeze();
            plate.Fill = pg;
            // black-shaded capsule: lit left edge, deep core; amber / red tint when a bot is active or in danger
            Color k0 = EyeColor.Hex("#3A3648"), k1 = EyeColor.Hex("#121019"), k2 = EyeColor.Hex("#040307");
            bool mad = Mode == EyeMode.Idle && CurrentMood == Mood.Mad;
            double bt = Mode == EyeMode.Nervous ? 0.42 : Mode == EyeMode.Danger ? 0.36 : mad ? 0.3 : 0;
            Color bc = Mode == EyeMode.Danger || mad ? EyeColor.Hex("#F43F5E") : EyeColor.Hex("#F59E0B");
            Color mid = EyeColor.Hex("#34303F"); // gentle, soft lighter middle (original intensity)
            var body = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            body.GradientStops.Add(new GradientStop(EyeColor.Mix(k2, bc, bt * 0.3), 0));
            body.GradientStops.Add(new GradientStop(EyeColor.Mix(k1, bc, bt * 0.55), 0.24));
            body.GradientStops.Add(new GradientStop(EyeColor.Mix(mid, bc, bt), 0.5));
            body.GradientStops.Add(new GradientStop(EyeColor.Mix(k1, bc, bt * 0.55), 0.76));
            body.GradientStops.Add(new GradientStop(EyeColor.Mix(k2, bc, bt * 0.3), 1));
            body.Freeze();
            var gb = new RadialGradientBrush(EyeColor.A(glow, 190), EyeColor.A(glow, 0)); gb.Freeze();
            foreach (var e in eyes) { e.Body.Fill = body; e.Glow.Fill = gb; }
            shownMode = Mode; shownMood = CurrentMood;
        }

        // lids: fractions of the slit height cut off at the outer-top, inner-top and bottom
        static void SetLids(One e, double to, double ti, double b)
        {
            if (Math.Abs(e.cTo - to) < 0.004 && Math.Abs(e.cTi - ti) < 0.004 && Math.Abs(e.cB - b) < 0.004) return;
            e.cTo = to; e.cTi = ti; e.cB = b;
            if (to < 0.004 && ti < 0.004 && b < 0.004) { e.Slit.Clip = null; return; }
            double yl = (e.Side < 0 ? to : ti) * SH, yr = (e.Side < 0 ? ti : to) * SH, yb = SH * (1 - b);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                // soft, slightly curved lid edges instead of hard straight cuts
                double dip = (to > 0.004 || ti > 0.004) ? 3.5 : 0, rise = b > 0.004 ? 3 : 0;
                c.BeginFigure(new Point(-2, yl), true, true);
                c.QuadraticBezierTo(new Point(SW / 2, (yl + yr) / 2 + dip * 2), new Point(SW + 2, yr), false, true);
                c.LineTo(new Point(SW + 2, yb), false, false);
                c.QuadraticBezierTo(new Point(SW / 2, yb - rise * 2), new Point(-2, yb), false, true);
            }
            g.Freeze(); e.Slit.Clip = g;
        }

        double Rand(double a, double b) { return a + rnd.NextDouble() * (b - a); }
        static double Ease(double u) { u = Math.Max(0, Math.Min(1, u)); return u * u * (3 - 2 * u); }

        // called ~25 times a second while the window is visible; t = seconds
    }

    static class EyeColor
    {
        public static Color Hex(string s) { try { return (Color)ColorConverter.ConvertFromString(s.StartsWith("#") ? s : "#" + s); } catch { return Colors.MediumSlateBlue; } }
        public static Color A(Color c, int a) { return Color.FromArgb((byte)Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }
        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
    }

    static class EyeNative
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    }
}
