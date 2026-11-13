using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Shell;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using Path = System.IO.Path;

namespace AgentWatch
{
    partial class MainWin
    {
        // ---------- confirm overlay + kill (always asks) ----------
        void ShowConfirm(string title, string text, string yes, Action onYes, bool dangerous)
        {
            F<TextBlock>("ConfirmTitle").Text = title; F<TextBox>("ConfirmText").Text = text;
            var y = F<Button>("ConfirmYes"); y.Visibility = yes == null ? Visibility.Collapsed : Visibility.Visible; y.Content = yes ?? "";
            y.Style = St(dangerous ? "DangerBtn" : "Btn");
            F<Button>("ConfirmNo").Content = yes == null ? "OK" : "Cancel";
            confirmYes = onYes; scrim.Visibility = Visibility.Visible; scrim.Opacity = 0.6; confirm.Visibility = Visibility.Visible;
            F<Button>("ConfirmNo").Focus();
        }
        void CloseConfirm() { confirm.Visibility = Visibility.Collapsed; if (appear.Visibility != Visibility.Visible) scrim.Visibility = Visibility.Collapsed; confirmYes = null; }

        bool KillOne(PInfo p, List<string> errs)
        {
            try
            {
                var proc = Process.GetProcessById(p.Pid); long c = 0;
                try { c = proc.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch { }
                if (p.Create > 0 && c > 0 && Math.Abs(c - p.Create) > 10000000) { errs.Add(p.Name + " " + p.Pid + ": already exited (PID reused), skipped"); return false; }
                proc.Kill();
                Logger.Event(string.Format("KILLED  | by user from Agent Watch | {0} > {1} | PID {2} | {3}", Engine.AppOf(p), p.Name, p.Pid, p.Path));
                return true;
            }
            catch (ArgumentException) { return false; }
            catch (Exception ex) { errs.Add(p.Name + " " + p.Pid + ": " + ex.Message); return false; }
        }
        void KillList(List<PInfo> list)
        {
            var errs = new List<string>(); int n = 0;
            for (int i = list.Count - 1; i >= 0; i--) if (KillOne(list[i], errs)) n++;   // children first
            Refresh2();
            ShowConfirm(n + " process" + (n == 1 ? "" : "es") + " stopped", errs.Count == 0 ? "Done. The app may restart some of them." : "Some could not be stopped:\n" + string.Join("\n", errs), null, null, false);
        }
        void AskKill(PInfo p, bool tree)
        {
            var list = new List<PInfo>(); if (tree) Subtree(p, list, 0); else list.Add(p);
            string what = p.AgentApp ? p.AppName : p.E != null && p.E.Friendly != null ? p.E.Friendly : p.Name;
            string text = string.Format("{0}\nPID {1} - part of {2}\nPath: {3}\n\n{4}", what, p.Pid, Engine.AppOf(p), p.Path ?? "(hidden)",
                tree ? "This stops it and the " + (list.Count - 1) + " process" + (list.Count == 2 ? "" : "es") + " it started. Anything unsaved in them is lost." : "This ends only this one process. Anything unsaved in it is lost, and the app may start it again.");
            ShowConfirm(tree ? "Kill " + p.Name + " and everything it started?" : "Kill " + p.Name + " (PID " + p.Pid + ")?", text, tree ? "Yes, kill " + list.Count : "Yes, kill it", delegate { KillList(list); }, true);
        }
        void AskKillApp(string app)
        {
            GroupInfo g = null; foreach (var x in allGroups) if (x.App == app) g = x;
            if (g == null) return;
            var list = new List<PInfo>(); foreach (var r in g.Roots) Subtree(r, list, 0);
            ShowConfirm("Kill all of " + app + "?", string.Format("This stops all {0} processes of {1}, including any work its agents are doing. Unsaved work is lost.\n\nMain process: {2}", list.Count, app, g.Roots.Count > 0 ? g.Roots[0].Path : "?"), "Yes, kill " + list.Count, delegate { KillList(list); }, true);
        }

        // ---------- appearance panel ----------
        void ToggleAppearance()
        {
            var tt = (TranslateTransform)appear.RenderTransform;
            bool open = appear.Visibility != Visibility.Visible;
            if (open)
            {
                appear.Visibility = Visibility.Visible; scrim.Visibility = Visibility.Visible;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(400, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 0.5, TimeSpan.FromMilliseconds(260)));
            }
            else
            {
                var an = new DoubleAnimation(0, 400, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
                an.Completed += delegate { appear.Visibility = Visibility.Collapsed; if (confirm.Visibility != Visibility.Visible) scrim.Visibility = Visibility.Collapsed; };
                tt.BeginAnimation(TranslateTransform.XProperty, an);
                Settings.Save();
            }
        }

        TextBlock Lbl(string t) { return new TextBlock { Text = t, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) }; }
        TextBlock Hint(string t) { return new TextBlock { Text = t, Foreground = R("Dim"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -4, 0, 6) }; }
        UIElement Sl(string label, double v, Action<double> set, bool retheme)
        {
            var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            var s = new Slider { Value = v }; g.Children.Add(s);
            var val = new TextBlock { Text = ((int)v).ToString(), Foreground = R("Dim"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) }; Grid.SetColumn(val, 1); g.Children.Add(val);
            var deb = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            deb.Tick += delegate { deb.Stop(); if (retheme) ApplyTheme(); Settings.Save(); };
            s.ValueChanged += delegate { set(s.Value); val.Text = ((int)s.Value).ToString(); deb.Stop(); deb.Start(); };
            var sp = new StackPanel(); sp.Children.Add(Lbl(label)); sp.Children.Add(g); return sp;
        }
        CheckBox Sw(string text, bool v, Action<bool> set)
        {
            var c = new CheckBox { Content = text, Style = St("Switch"), IsChecked = v, Margin = new Thickness(0, 8, 0, 6) };
            c.Checked += delegate { set(true); Settings.Save(); }; c.Unchecked += delegate { set(false); Settings.Save(); };
            return c;
        }
        void BuildAppearance()
        {
            var b = appearBody; b.Children.Clear();
            b.Children.Add(Lbl("Colour preset"));
            var wp = new WrapPanel();
            foreach (var p in Theme.Presets)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Border { Width = 26, Height = 14, CornerRadius = new CornerRadius(7), Margin = new Thickness(0, 0, 7, 0), Background = new LinearGradientBrush(new GradientStopCollection { new GradientStop(p.Bg2, 0), new GradientStop(p.Accent, 0.55), new GradientStop(p.Accent2, 1) }, 0) });
                sp.Children.Add(new TextBlock { Text = p.Name, FontSize = 12 });
                var rb = new RadioButton { Content = sp, GroupName = "preset", Style = St("Chip"), IsChecked = p.Name == Settings.Preset };
                string name = p.Name;
                rb.Checked += delegate { if (Settings.Preset == name) return; Settings.Preset = name; Settings.AccentHex = null; Settings.PanelHex = null; Settings.TextHex = null; ApplyTheme(); Settings.Save(); Dispatcher.BeginInvoke(new Action(BuildAppearance)); };
                wp.Children.Add(rb);
            }
            b.Children.Add(wp);

            b.Children.Add(Lbl("Surface finish"));
            var fp = new WrapPanel();
            foreach (var f in Theme.Finishes)
            {
                string fn = f;
                var rb = new RadioButton { Content = new TextBlock { Text = f == "Glossy" ? "Glossy / Shiny" : f == "Glass" ? "Glass / Acrylic" : f, FontSize = 12 }, GroupName = "finish", Style = St("Chip"), IsChecked = f == Settings.Finish };
                rb.Checked += delegate { Settings.Finish = fn; ApplyTheme(); Settings.Save(); };
                fp.Children.Add(rb);
            }
            b.Children.Add(fp);

            b.Children.Add(Sl("Panel transparency", Settings.Transparency, v => Settings.Transparency = v, true));
            b.Children.Add(Hint("How see-through the cards are. At high values the cards nearly vanish and the text gets a soft shadow so it stays readable."));
            b.Children.Add(Sl("Blur behind the window", Settings.Blur, v => Settings.Blur = v, true));
            b.Children.Add(Hint("0 = off (solid background). 1-100 = Windows blurs whatever is behind the window; the slider sets how much of that blur shows through the background (100 = mostly blur, low = mostly solid tint). It stays on even when the window is not focused. Raise panel transparency to see it behind the cards too."));
            b.Children.Add(Sw("Eyes follow the mouse", Settings.EyesFollow, v => { Settings.EyesFollow = v; eyes.Follow = v; }));
            b.Children.Add(Sw("Allow motion effects: shakes and the occasional glint (off = reduced motion)", Settings.AllowShake, v => { Settings.AllowShake = v; eyes.AllowShake = v; }));

            b.Children.Add(Lbl("Custom colours"));
            b.Children.Add(ColorRow("Accent", Theme.Accent, h => Settings.AccentHex = h));
            b.Children.Add(ColorRow("Panel", Theme.Panel, h => Settings.PanelHex = h));
            b.Children.Add(ColorRow("Text", Theme.Text, h => Settings.TextHex = h));
            var reset = new Button { Content = "Reset to preset colours", Style = St("Btn"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
            reset.Click += delegate { Settings.AccentHex = null; Settings.PanelHex = null; Settings.TextHex = null; ApplyTheme(); Settings.Save(); BuildAppearance(); };
            b.Children.Add(reset);

            b.Children.Add(Lbl("Test alert"));
            b.Children.Add(Hint("Preview what the eyes, header and popup do. Nothing real is flagged and nothing is killed."));
            var tw = new WrapPanel();
            var t1 = new Button { Content = "Bot detected (20 s)", Style = St("Btn") }; t1.Click += delegate { ToggleAppearance(); StartPreview(EyeMode.Nervous); };
            var t2 = new Button { Content = "Danger alert", Style = St("DangerBtn") }; t2.Click += delegate { ToggleAppearance(); StartPreview(EyeMode.Danger); };
            var t4 = new Button { Content = "Bored eyes", Style = St("Btn") }; t4.Click += delegate { ToggleAppearance(); eyes.PreviewMood(Mood.Bored, 12); };
            var t5 = new Button { Content = "Rage shake", Style = St("Btn") }; t5.Click += delegate { ToggleAppearance(); eyes.PreviewMood(Mood.Mad, 8); };
            var t7 = new Button { Content = "Falling asleep", Style = St("Btn") }; t7.Click += delegate { ToggleAppearance(); eyes.PreviewSleep(); };
            var t8 = new Button { Content = "Glint", Style = St("Btn"), ToolTip = "One sweep of the glint now (works even with reduced motion)" }; t8.Click += delegate { ToggleAppearance(); StartGlint(); };
            var t3 = new Button { Content = "Stop preview", Style = St("Btn") }; t3.Click += delegate { StopPreview(); };
            var t6 = new Button { Content = "Poked (click again to escalate)", Style = St("Btn") }; t6.Click += delegate { eyes.Poke(); };
            tw.Children.Add(t1); tw.Children.Add(t2); tw.Children.Add(t4); tw.Children.Add(t5); tw.Children.Add(t7); tw.Children.Add(t8); tw.Children.Add(t6); tw.Children.Add(t3);
            b.Children.Add(tw);
            b.Children.Add(Hint("Settings are saved to settings.json next to AgentWatch.exe."));
        }
        static ControlTemplate swatchTpl;
        UIElement ColorRow(string label, Color cur, Action<string> set)
        {
            if (swatchTpl == null) swatchTpl = (ControlTemplate)XamlReader.Parse("<ControlTemplate TargetType=\"Button\" xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Border CornerRadius=\"10\" Background=\"{TemplateBinding Background}\" BorderBrush=\"#66FFFFFF\" BorderThickness=\"1\"/></ControlTemplate>");
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) }); g.ColumnDefinitions.Add(new ColumnDefinition());
            g.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
            var tb = new TextBox { Text = Theme.ToHex(cur), Width = 84, HorizontalAlignment = HorizontalAlignment.Left }; Grid.SetColumn(tb, 1); g.Children.Add(tb);
            var sw = new WrapPanel { VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(sw, 2); g.Children.Add(sw);
            string[] picks = label == "Text" ? new[] { "#FFFFFF", "#E0E7FF", "#F5F3FF", "#FEF3C7", "#ECFDF5", "#FFE4E6" }
                : label == "Panel" ? new[] { "#2A2670", "#3B1679", "#143E52", "#4A1C14", "#163B2B", "#4A1D3C" }
                : new[] { "#818CF8", "#C084FC", "#22D3EE", "#F472B6", "#FBBF24", "#4ADE80", "#FB7185" };
            Action<string> apply = t => { t = t.Trim(); if (System.Text.RegularExpressions.Regex.IsMatch(t, "^#?[0-9A-Fa-f]{6}$")) { set(t.StartsWith("#") ? t : "#" + t); ApplyTheme(); Settings.Save(); } };
            foreach (var h in picks)
            {
                string hx = h;
                var bt = new Button { Width = 20, Height = 20, Margin = new Thickness(0, 0, 4, 4), Cursor = Cursors.Hand, Background = new SolidColorBrush(Theme.Hex(h)), ToolTip = h, Template = swatchTpl };
                bt.Click += delegate { tb.Text = hx; apply(hx); };
                sw.Children.Add(bt);
            }
            tb.LostFocus += delegate { apply(tb.Text); };
            tb.KeyDown += delegate (object s, KeyEventArgs e) { if (e.Key == Key.Enter) apply(tb.Text); };
            return g;
        }

        // ---------- screenshot ----------
        public void TakeScreenshot(string path, string mode)
        {
            bool wasVisible = IsShown, wasMin = IsVisible && WindowState == WindowState.Minimized;
            ShowMain(); Topmost = true;
            // "real:<mode>" grabs the actual screen pixels (shows the blur behind); otherwise the WPF tree is rendered on its own
            bool real = mode.StartsWith("real:"); if (real) mode = mode.Substring(5);
            // "blur:<0-100>[:<transparency>]" temporarily changes blur (and panel transparency) for the picture only
            double oldBlur = double.NaN, oldTr = double.NaN;
            if (mode.StartsWith("blur:"))
            {
                var bp = mode.Split(':'); oldBlur = Settings.Blur; oldTr = Settings.Transparency;
                double v; if (bp.Length > 1 && double.TryParse(bp[1], out v)) Settings.Blur = v;
                if (bp.Length > 2 && double.TryParse(bp[2], out v)) Settings.Transparency = v;
                mode = bp.Length > 3 ? bp[3] : "normal"; ApplyTheme();
            }
            // "theme:<preset>[:<finish>]" previews a look for the picture only; the saved settings are restored afterwards
            string oldPreset = null, oldFinish = null;
            if (mode.StartsWith("theme:"))
            {
                var parts = mode.Substring(6).Split(':'); oldPreset = Settings.Preset; oldFinish = Settings.Finish;
                foreach (var pr in Theme.Presets) if (pr.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) Settings.Preset = pr.Name;
                if (parts.Length > 1) foreach (var fn in Theme.Finishes) if (fn.Equals(parts[1], StringComparison.OrdinalIgnoreCase)) Settings.Finish = fn;
                ApplyTheme();
            }
            if (mode == "normal" || mode == "details" || mode == "appearance" || mode.StartsWith("theme:")) eyes.PreviewMood(Mood.Normal, 4); // calm eyes for the picture even if you've been away
            if (mode == "nervous") StartPreview(EyeMode.Nervous);
            else if (mode == "danger") StartPreview(EyeMode.Danger);
            else if (mode == "appearance" && appear.Visibility != Visibility.Visible) ToggleAppearance();
            else if (mode == "bored") eyes.PreviewMood(Mood.Bored, 8);
            else if (mode == "mad") eyes.PreviewMood(Mood.Mad, 8);
            else if (mode == "asleep" || mode == "waking") eyes.PreviewSleep();
            else if (mode == "rage") eyes.PreviewMood(Mood.Mad, 8);
            else if (mode == "poked") eyes.Poke();
            else if (mode == "poked2") { eyes.Poke(); eyes.Poke(); }
            else if (mode == "details")
            {
                VM pick = null; foreach (var v in rowItems) if (v.IsApp && v.G != null && v.G.App == "OpenBot") pick = v;
                if (pick == null) foreach (var v in rowItems) if (v.IsApp) { pick = v; break; }
                if (pick != null) { if (!pick.Expanded) Toggle(pick); int i = rowItems.IndexOf(pick); rows.SelectedIndex = Math.Min(rowItems.Count - 1, i + 1); rows.ScrollIntoView(rows.SelectedItem); }
            }
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(mode.StartsWith("poked") ? 1300 : mode == "asleep" ? 5200 : mode == "waking" ? 7700 : mode == "rage" ? 1000 : mode == "glint" ? 900 : mode == "snapshot" ? 250 : 2400) };
            t.Tick += delegate
            {
                t.Stop();
                if (mode == "glint") GlintMid();
                if (real)
                {
                    try
                    {
                        RECT wr; GetWindowRect(hwnd, out wr);
                        using (var bmp = new SD.Bitmap(Math.Max(1, wr.R - wr.L), Math.Max(1, wr.B - wr.T)))
                        {
                            using (var gr = SD.Graphics.FromImage(bmp)) gr.CopyFromScreen(wr.L, wr.T, 0, 0, bmp.Size);
                            bmp.Save(path, SD.Imaging.ImageFormat.Png);
                        }
                        Logger.Event("SCREENSHOT (screen pixels) saved to " + path + " (" + mode + ")");
                    }
                    catch (Exception ex) { Logger.Error("screenshot: " + ex); }
                }
                else try
                {
                    // render the WPF tree itself, so other windows on top never end up in the picture
                    var root = (FrameworkElement)Content;
                    double w = root.ActualWidth, h = root.ActualHeight, sc = 1;
                    var src = PresentationSource.FromVisual(this);
                    if (src != null && src.CompositionTarget != null) sc = src.CompositionTarget.TransformToDevice.M11;
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(Theme.Bg1), null, new Rect(0, 0, w, h));
                    }
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(w * sc), (int)Math.Ceiling(h * sc), 96 * sc, 96 * sc, PixelFormats.Pbgra32);
                    rtb.Render(dv); rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using (var fs = File.Create(path)) enc.Save(fs);
                    Logger.Event("SCREENSHOT saved to " + path + " (" + mode + ")");
                }
                catch (Exception ex) { Logger.Error("screenshot: " + ex); }
                Topmost = false;
                if (mode == "glint") GlintMidDone();
                if (!double.IsNaN(oldBlur)) { Settings.Blur = oldBlur; Settings.Transparency = oldTr; ApplyTheme(); }
                if (oldPreset != null) { Settings.Preset = oldPreset; Settings.Finish = oldFinish; ApplyTheme(); }
                if (mode == "nervous" || mode == "danger" || mode == "bored" || mode == "mad" || mode == "asleep" || mode == "waking" || mode == "rage") StopPreview();
                if (mode == "appearance" && appear.Visibility == Visibility.Visible) ToggleAppearance();
                if (wasMin) WindowState = WindowState.Minimized; else if (!wasVisible) Hide();
            };
            t.Start();
        }
    }

    static class Program
    {
        public static bool StartHidden;
        // own taskbar identity, so Windows uses the window's (new mascot) icon instead of a stale cached exe icon
        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetCurrentProcessExplicitAppUserModelID(string id);
        [STAThread]
        static void Main(string[] args)
        {
            string mode = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].Equals("--hidden", StringComparison.OrdinalIgnoreCase)) StartHidden = true;
                if (args[i].Equals("--screenshot", StringComparison.OrdinalIgnoreCase)) mode = i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1].ToLowerInvariant() : "normal";
            }
            bool created;
            var mutex = new Mutex(true, "Local\\AgentWatch.Single", out created);
            var evShow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentWatch.Show");
            var evShot = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentWatch.Shot");
            string req = Path.Combine(Logger.Dir, "shot-request.txt");
            if (!created)
            {
                if (mode != null) { try { Directory.CreateDirectory(Logger.Dir); File.WriteAllText(req, mode); } catch { } evShot.Set(); }
                else if (!StartHidden) evShow.Set();
                return;
            }
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Logger.Error("fatal: " + e.ExceptionObject); };
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            try { SetCurrentProcessExplicitAppUserModelID("AgentWatch.Monitor.2"); } catch { }
            Settings.Load();
            if (StartHidden && Settings.WindowOpen) StartHidden = false;
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.DispatcherUnhandledException += delegate (object s, DispatcherUnhandledExceptionEventArgs e) { Logger.Error("ui: " + e.Exception); e.Handled = true; };
            using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("Theme.xaml"))
                app.Resources = (ResourceDictionary)XamlReader.Load(s);
            Logger.Event("===== Agent Watch v2 started =====");
            var win = new MainWin();
            if (!StartHidden) win.Show();
            var th = new Thread(delegate ()
            {
                var hs = new WaitHandle[] { evShow, evShot };
                while (true)
                {
                    int i = WaitHandle.WaitAny(hs);
                    try
                    {
                        if (i == 0) win.Dispatcher.BeginInvoke(new Action(delegate { win.ShowMain(); }));
                        else
                        {
                            string m = "normal"; try { m = File.ReadAllText(req).Trim(); File.Delete(req); } catch { }
                            string file = m == "normal" ? "screenshot.png" : "screenshot-" + System.Text.RegularExpressions.Regex.Replace(m, "[^a-z0-9]+", "-") + ".png";
                            if (m == "measure") win.Dispatcher.BeginInvoke(new Action(delegate { win.MeasureCpu(); }));
                            else win.Dispatcher.BeginInvoke(new Action(delegate { win.TakeScreenshot(Path.Combine(Logger.Dir, file), m); }));
                        }
                    }
                    catch { }
                }
            });
            th.IsBackground = true; th.Start();
            var once = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            once.Tick += delegate { once.Stop(); win.StartupBalloon(); if (mode != null) win.TakeScreenshot(Path.Combine(Logger.Dir, mode == "normal" ? "screenshot.png" : "screenshot-" + System.Text.RegularExpressions.Regex.Replace(mode, "[^a-z0-9]+", "-") + ".png"), mode); };
            once.Start();
            app.Run();
            GC.KeepAlive(mutex);
        }
    }
}
