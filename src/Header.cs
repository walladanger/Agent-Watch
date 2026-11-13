using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WF = System.Windows.Forms;
using SD = System.Drawing;
using Path = System.IO.Path;

namespace AgentWatch
{
    // tiny sparkline: one or two series, auto-scaled, drawn directly (no layout cost)
    class Spark : FrameworkElement
    {
        public List<double> A = new List<double>(), B = new List<double>();
        public double Floor = 1, FixedMax = -1; public Brush StrokeA, StrokeB, FillA;
        public const int Len = 45;
        public void Push(double a, double b) { A.Add(a); B.Add(b); while (A.Count > Len) A.RemoveAt(0); while (B.Count > Len) B.RemoveAt(0); InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight; if (w < 4 || h < 4 || A.Count < 2) return;
            double mx = FixedMax > 0 ? FixedMax : Floor; if (FixedMax <= 0) { foreach (var v in A) mx = Math.Max(mx, v); foreach (var v in B) if (!double.IsNaN(v)) mx = Math.Max(mx, v); }
            Draw(dc, A, mx, w, h, StrokeA, FillA);
            if (StrokeB != null && B.Count >= 2 && !double.IsNaN(B[B.Count - 1])) Draw(dc, B, mx, w, h, StrokeB, null);
        }
        static void Draw(DrawingContext dc, List<double> d, double mx, double w, double h, Brush stroke, Brush fill)
        {
            double step = w / (Len - 1), x0 = w - step * (d.Count - 1);
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                c.BeginFigure(new Point(x0, h - 1 - Math.Min(1, Math.Max(0, d[0] / mx)) * (h - 2)), fill != null, false);
                for (int i = 1; i < d.Count; i++) c.LineTo(new Point(x0 + i * step, h - 1 - Math.Min(1, Math.Max(0, d[i] / mx)) * (h - 2)), true, true);
                if (fill != null) { c.LineTo(new Point(w, h), false, false); c.LineTo(new Point(x0, h), false, false); }
            }
            g.Freeze();
            if (fill != null) dc.DrawGeometry(fill, null, g);
            var pen = new Pen(stroke, 1.4) { LineJoin = PenLineJoin.Round }; pen.Freeze();
            var g2 = new StreamGeometry();
            using (var c = g2.Open())
            {
                c.BeginFigure(new Point(x0, h - 1 - Math.Min(1, Math.Max(0, d[0] / mx)) * (h - 2)), false, false);
                for (int i = 1; i < d.Count; i++) c.LineTo(new Point(x0 + i * step, h - 1 - Math.Min(1, Math.Max(0, d[i] / mx)) * (h - 2)), true, true);
            }
            g2.Freeze(); dc.DrawGeometry(null, pen, g2);
        }
    }

    class Tile
    {
        public string Key; public Border B; public TextBlock Title, Value, Sub; public Spark Sp; public StackPanel Bars;
    }

    partial class MainWin
    {
        SysStats stats = new SysStats();
        DispatcherTimer statTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(2000) };
        UniformGrid tilesGrid; Grid headText; StackPanel headActions; Canvas glintHost; Border winEdge;
        Dictionary<string, Tile> tiles = new Dictionary<string, Tile>(); List<string> tileOrder = new List<string>();
        SysSample shownSample; bool paused; Button btnPause, btnKillSel; TextBlock pauseLabel, pauseIcon;

        // ---------- header actions ----------
        Button HeadBtn(string glyph, string text, string tip, bool danger, RoutedEventHandler click)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            var ic = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Width = 20, VerticalAlignment = VerticalAlignment.Center };
            var tx = new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(ic); sp.Children.Add(tx);
            var b = new Button { Content = sp, Style = St(danger ? "DangerBtn" : "Btn"), ToolTip = tip, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 0, 5) };
            if (click != null) b.Click += click; headActions.Children.Add(b);
            b.Tag = new object[] { ic, tx };
            return b;
        }

        void BuildHeader()
        {
            tilesGrid = F<UniformGrid>("Tiles"); headText = F<Grid>("HeadText"); headActions = F<StackPanel>("HeadActions"); glintHost = F<Canvas>("GlintHost"); winEdge = F<Border>("WinEdge");
            btnPause = HeadBtn("\uE769", "Pause watching", "Stop scanning for agents until you press Resume. Nothing is flagged while paused.", false, delegate { SetPaused(!paused); });
            var parts = (object[])btnPause.Tag; pauseIcon = (TextBlock)parts[0]; pauseLabel = (TextBlock)parts[1];
            var test = HeadBtn("\uE7BA", "Test alert", "Preview an alert, a mood or the glint. Nothing real is flagged or killed.", false, null);
            test.Click += delegate { TestMenu(test); };
            HeadBtn("\uE838", "Open logs", "Open " + Logger.Dir, false, delegate { OpenLogs(); });
            btnKillSel = HeadBtn("\uE711", "Kill selected...", "Kill the selected process or app. Always asks first.", true, delegate { KillSelected(); });
            HeadBtn("\uE722", "Snapshot", "Save a picture of this window and a status report to " + Logger.Dir, false, delegate { Snapshot(); });
            HeadBtn("\uE9D9", "Task Manager", "Open Windows Task Manager", false, delegate { try { Process.Start("taskmgr.exe"); } catch (Exception ex) { Logger.Error("taskmgr: " + ex.Message); } });
            HeadBtn("\uE790", "Appearance", "Colours, finish, transparency, blur and motion", false, delegate { ToggleAppearance(); });
            btnKillSel.IsEnabled = false;
            rows.SelectionChanged += delegate { UpdateKillSel(); }; hist.SelectionChanged += delegate { UpdateKillSel(); };

            headText.SizeChanged += delegate { LayoutTiles(); };
            statTimer.Tick += delegate { UpdateTiles(); };
            stats.Start();
            IsVisibleChanged += delegate { SyncStats(); LogWin(); };
            StateChanged += delegate { SyncStats(); LogWin(); };
        }
        void SyncStats()
        {
            bool on = IsShown; stats.SetActive(on);
            if (on && !statTimer.IsEnabled) { statTimer.Start(); UpdateTiles(); } else if (!on && statTimer.IsEnabled) statTimer.Stop();
        }
        string lastWinState = "";
        void LogWin()
        {
            string s = !IsVisible ? "hidden" : WindowState == WindowState.Minimized ? "minimized" : WindowState == WindowState.Maximized ? "maximized" : "shown";
            if (s != lastWinState) { lastWinState = s; Logger.Event("WINDOW " + s); }
        }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        // "--screenshot measure": shows the window for 30 s at the BOTTOM of the window stack without taking focus, measures our CPU, then puts things back
        public void MeasureCpu()
        {
            bool was = IsShown;
            if (!was) { ShowActivated = false; Show(); if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal; ShowActivated = true; }
            if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0013);
            var p = Process.GetCurrentProcess(); var t0 = p.TotalProcessorTime; var w0 = DateTime.Now; int f0 = frames;
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            t.Tick += delegate
            {
                t.Stop(); p.Refresh(); double sec = (DateTime.Now - w0).TotalSeconds;
                double pct = (p.TotalProcessorTime - t0).TotalSeconds * 100 / sec;
                Logger.Event(string.Format("CPU MEASURE window visible {0:0} s: {1:0.00}% of one core ({2:0.00}% of the machine), blur {3:0}, transparency {4:0}, size {5:0}x{6:0}", sec, pct, pct / Environment.ProcessorCount, Settings.Blur, Settings.Transparency, ActualWidth, ActualHeight));
                if (!was) Hide();
            };
            t.Start();
        }
        void UpdateKillSel()
        {
            var vm = rows.SelectedItem as VM;
            btnKillSel.IsEnabled = vm != null && (vm.P != null || (vm.IsApp && vm.G != null));
        }
        void KillSelected()
        {
            var vm = rows.SelectedItem as VM; if (vm == null) return;
            if (vm.P != null) AskKill(vm.P, false); else if (vm.IsApp && vm.G != null) AskKillApp(vm.G.App);
        }
        void TestMenu(Button anchor)
        {
            var cm = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom, Background = R("FlyoutBrush"), Foreground = R("Text"), BorderBrush = R("Line") };
            Action<string, Action> add = (t, a) => { var mi = new MenuItem { Header = t, Foreground = R("Text") }; mi.Click += delegate { a(); }; cm.Items.Add(mi); };
            add("Bot detected (20 s)", delegate { StartPreview(EyeMode.Nervous); });
            add("Danger alert", delegate { StartPreview(EyeMode.Danger); });
            add("Glint (one sweep)", delegate { StartGlint(); });
            add("Bored eyes", delegate { eyes.PreviewMood(Mood.Bored, 12); });
            add("Rage shake", delegate { eyes.PreviewMood(Mood.Mad, 8); });
            add("Falling asleep", delegate { eyes.PreviewSleep(); });
            add("Poke the eyes", delegate { eyes.Poke(); });
            add("Stop preview", delegate { StopPreview(); });
            cm.IsOpen = true;
        }
        void SetPaused(bool p)
        {
            paused = p;
            if (p) { tick.Stop(); Logger.Event("PAUSED watching (by you)"); }
            else { tick.Start(); Logger.Event("RESUMED watching"); Refresh2(); }
            pauseIcon.Text = p ? "\uE768" : "\uE769"; pauseLabel.Text = p ? "Resume watching" : "Pause watching";
            try { tray.Text = p ? "Agent Watch (paused)" : "Agent Watch"; } catch { }
            ApplyPausedHeader();
        }
        void ApplyPausedHeader()
        {
            if (!paused) return;
            headMain.Text = "Paused - Agent Watch is not watching right now.";
            headSub.Text = "Press Resume watching to start again. Nothing is scanned or flagged while paused.";
            statePill.Background = Amber; stateText.Text = "PAUSED"; stateText.Foreground = Dark; stateSince.Text = "since " + DateTime.Now.ToString("h:mm tt");
        }
        void Snapshot()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string png = Path.Combine(Logger.Dir, "snapshot-" + stamp + ".png");
            try { WriteStatus(); File.Copy(Path.Combine(Logger.Dir, "status.txt"), Path.Combine(Logger.Dir, "snapshot-" + stamp + "-status.txt"), true); } catch (Exception ex) { Logger.Error("snapshot status: " + ex.Message); }
            TakeScreenshot(png, "real:snapshot");
            Balloon("Snapshot saved", "snapshot-" + stamp + ".png and -status.txt in " + Logger.Dir, WF.ToolTipIcon.Info, true);
        }

        // ---------- tiles ----------
        Tile GetTile(string key, string title, bool bars)
        {
            Tile t; if (tiles.TryGetValue(key, out t)) return t;
            t = new Tile { Key = key };
            var g = new Grid();
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            t.Title = new TextBlock { Text = title, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = R("Dim"), TextTrimming = TextTrimming.CharacterEllipsis };
            t.Value = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) };
            t.Sub = new TextBlock { FontSize = 11, Foreground = R("Dim"), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(t.Value, 1);
            var bottom = new Grid(); Grid.SetRow(bottom, 2);
            if (bars) { t.Bars = new StackPanel { Margin = new Thickness(0, 2, 0, 0) }; bottom.Children.Add(t.Bars); }
            else
            {
                bottom.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); bottom.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                bottom.Children.Add(t.Sub);
                t.Sp = new Spark { Height = 18, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Bottom }; Grid.SetRow(t.Sp, 1); bottom.Children.Add(t.Sp);
            }
            g.Children.Add(t.Title); g.Children.Add(t.Value); g.Children.Add(bottom);
            t.B = new Border { CornerRadius = new CornerRadius(9), Background = R("Soft"), BorderBrush = R("Line"), BorderThickness = new Thickness(1), Padding = new Thickness(9, 5, 9, 5), Margin = new Thickness(0, 0, 6, 6), Height = 76, Child = g };
            tiles[key] = t; return t;
        }
        void StyleSpark(Spark sp, bool second)
        {
            var ac = Theme.Accent; var a2 = Theme.Accent2;
            sp.StrokeA = new SolidColorBrush(ac); sp.FillA = new SolidColorBrush(Color.FromArgb(0x30, ac.R, ac.G, ac.B));
            sp.StrokeB = second ? new SolidColorBrush(Color.FromArgb(0xC0, a2.R, a2.G, a2.B)) : null;
        }
        static string GB(double b) { return (b / 1073741824.0).ToString(b >= 107374182400 ? "0" : "0.0"); }
        static string Rate(double bps)
        {
            if (bps < 0) return "n/a";
            if (bps >= 1048576 * 100) return (bps / 1048576).ToString("0") + " MB/s";
            if (bps >= 1048576) return (bps / 1048576).ToString("0.0") + " MB/s";
            if (bps >= 1024) return (bps / 1024).ToString("0") + " KB/s";
            return bps.ToString("0") + " B/s";
        }
        UIElement DriveBar(DriveStat d)
        {
            var g = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new TextBlock { Text = d.Letter + ":", FontSize = 11, FontWeight = FontWeights.SemiBold });
            double used = d.Ok && d.Total > 0 ? 1 - d.Free / d.Total : 0;
            var bar = new Grid { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, used), GridUnitType.Star) }); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.001, 1 - used), GridUnitType.Star) });
            var bg = new Border { CornerRadius = new CornerRadius(3), Background = R("Soft2") }; Grid.SetColumnSpan(bg, 2); bar.Children.Add(bg);
            var fill = new Border { CornerRadius = new CornerRadius(3), Background = used > 0.9 ? RedB : used > 0.8 ? YelB : R("Accent") }; bar.Children.Add(fill);
            Grid.SetColumn(bar, 1); g.Children.Add(bar);
            var tx = new TextBlock { Text = d.Ok ? GB(d.Free) + " GB free" : "not ready", FontSize = 11, Foreground = R("Dim") }; Grid.SetColumn(tx, 2); g.Children.Add(tx);
            return g;
        }
        void UpdateTiles()
        {
            var s = stats.Last; if (s == null || s == shownSample) return; shownSample = s;
            var order = new List<string>();
            var cpu = GetTile("cpu", "CPU", false); order.Add("cpu");
            cpu.Value.Text = s.Cpu < 0 ? "..." : s.Cpu.ToString("0") + "%"; cpu.Sub.Text = Environment.ProcessorCount + " threads, all cores"; cpu.Sp.FixedMax = 100; cpu.Sp.Push(Math.Max(0, s.Cpu), double.NaN);
            var ram = GetTile("ram", "MEMORY", false); order.Add("ram");
            ram.Value.Text = GB(s.RamUsed) + " / " + GB(s.RamTotal) + " GB"; ram.Sub.Text = (s.RamTotal > 0 ? (s.RamUsed * 100 / s.RamTotal).ToString("0") : "0") + "% used"; ram.Sp.FixedMax = 100; ram.Sp.Push(s.RamTotal > 0 ? s.RamUsed * 100 / s.RamTotal : 0, double.NaN);
            if (s.Gpus.Count == 0)
            {
                var g0 = GetTile("gpu-none", "GPU", false); order.Add("gpu-none");
                g0.Value.Text = "n/a"; g0.Sub.Text = s.GpuNote ?? "No NVIDIA GPU found"; g0.Sp.Push(0, double.NaN);
            }
            foreach (var gs in s.Gpus)
            {
                string k = "gpu" + gs.Index + gs.Name; var t = GetTile(k, "GPU " + gs.Index + "  ·  " + gs.Name.ToUpper(), false); order.Add(k);
                double vram = gs.MemTotal > 0 ? gs.MemUsed * 100 / gs.MemTotal : 0;
                t.Value.Text = gs.Ok ? gs.Util.ToString("0") + "%" + (gs.Temp >= 0 ? "   " + gs.Temp.ToString("0") + "°C" : "") : "not answering";
                t.Sub.Text = "VRAM " + GB(gs.MemUsed) + " / " + GB(gs.MemTotal) + " GB (" + vram.ToString("0") + "%)";
                t.Sp.FixedMax = 100; t.Sp.Push(gs.Util, vram);
            }
            var net = GetTile("net", "NETWORK", false); order.Add("net");
            net.Value.Text = "↓ " + Rate(s.NetDown) + "   ↑ " + Rate(s.NetUp); net.Sub.Text = "all active adapters"; net.Sp.Floor = 10240; net.Sp.Push(s.NetDown, s.NetUp);
            var dk = GetTile("disk", "DISK I/O", false); order.Add("disk");
            dk.Value.Text = s.DiskRead < 0 ? "n/a" : "R " + Rate(s.DiskRead) + "   W " + Rate(s.DiskWrite); dk.Sub.Text = "all physical disks"; dk.Sp.Floor = 1048576; dk.Sp.Push(Math.Max(0, s.DiskRead), Math.Max(0, s.DiskWrite));
            var dr = GetTile("drives", "FREE SPACE", true); order.Add("drives");
            dr.Value.Text = ""; dr.Value.Visibility = Visibility.Collapsed; dr.Bars.Children.Clear(); foreach (var d in s.Drives) dr.Bars.Children.Add(DriveBar(d));
            dr.B.ToolTip = string.Join("\n", s.Drives.ConvertAll(d => d.Letter + ": " + (d.Ok ? GB(d.Free) + " GB free of " + GB(d.Total) + " GB" : "not ready")));
            // GPUs can come and go: rebuild the grid only when the set of tiles changes
            if (string.Join("|", order) != string.Join("|", tileOrder))
            {
                tileOrder = order; tilesGrid.Children.Clear();
                foreach (var k in order) tilesGrid.Children.Add(tiles[k].B);
                LayoutTiles(); RestyleTiles();
            }
        }
        void LayoutTiles()
        {
            if (tilesGrid == null || headText == null) return;
            int n = Math.Max(1, tilesGrid.Children.Count); double w = headText.ActualWidth; if (w < 50) return;
            int cols = Math.Max(2, Math.Min(n, (int)(w / 175)));
            int rowsN = (n + cols - 1) / cols; cols = (n + rowsN - 1) / rowsN; // balance the rows
            tilesGrid.Columns = cols;
        }
        void RestyleTiles()
        {
            foreach (var t in tiles.Values)
            {
                t.B.Background = R("Soft"); t.B.BorderBrush = R("Line"); t.Title.Foreground = R("Dim"); t.Sub.Foreground = R("Dim");
                if (t.Sp != null) { StyleSpark(t.Sp, t.Key != "cpu" && t.Key != "ram" && t.Key != "gpu-none"); t.Sp.InvalidateVisual(); }
            }
            shownSample = null;
        }

        // ---------- readability on very transparent panels ----------
        void ApplyReadability()
        {
            double t = Math.Max(0, Math.Min(100, Settings.Transparency));
            Effect fx = null;
            if (t > 40)
            {
                var d = new DropShadowEffect { ShadowDepth = 0, BlurRadius = 5, Opacity = Math.Min(0.95, 0.35 + (t - 40) / 60.0 * 0.6), Color = Theme.IsLight ? Colors.White : Colors.Black, RenderingBias = RenderingBias.Performance };
                d.Freeze(); fx = d;
            }
            foreach (var e in new FrameworkElement[] { rows, hist, det, headText, F<FrameworkElement>("Toolbar") }) if (e != null) e.Effect = fx;
        }

        // ---------- blur behind (acrylic accent) ----------
        [StructLayout(LayoutKind.Sequential)] struct AccentPolicy { public int State, Flags, Color, Anim; }
        [StructLayout(LayoutKind.Sequential)] struct WinCompData { public int Attr; public IntPtr Data; public int Size; }
        [DllImport("user32.dll")] static extern int SetWindowCompositionAttribute(IntPtr h, ref WinCompData d);
        int lastAccent = -1;
        void SetAccent(bool on)
        {
            if (hwnd == IntPtr.Zero) return;
            var c = Theme.Bg1;
            var ap = new AccentPolicy { State = on ? 4 : 0, Flags = on ? 2 : 0, Color = on ? (0x18 << 24) | (c.B << 16) | (c.G << 8) | c.R : 0 };
            int key = ap.State ^ ap.Color; if (key == lastAccent) return; lastAccent = key;
            int sz = Marshal.SizeOf(ap); IntPtr p = Marshal.AllocHGlobal(sz);
            try { Marshal.StructureToPtr(ap, p, false); var d = new WinCompData { Attr = 19, Data = p, Size = sz }; if (SetWindowCompositionAttribute(hwnd, ref d) == 0) Logger.Error("blur: Windows refused the blur-behind request"); }
            catch (Exception ex) { Logger.Error("blur: " + ex.Message); }
            finally { Marshal.FreeHGlobal(p); }
        }

        // ---------- frameless window helpers (per-pixel alpha window) ----------
        [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPos, MinTrack, MaxTrack; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
        [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int w, int h);
        void MinMax(IntPtr lp)
        {
            var mon = MonitorFromWindow(hwnd, 2); if (mon == IntPtr.Zero) return;
            var mi = new MONITORINFO { Size = Marshal.SizeOf(typeof(MONITORINFO)) }; if (!GetMonitorInfo(mon, ref mi)) return;
            var mm = (MINMAXINFO)Marshal.PtrToStructure(lp, typeof(MINMAXINFO));
            mm.MaxPos.X = mi.Work.L - mi.Monitor.L; mm.MaxPos.Y = mi.Work.T - mi.Monitor.T;
            mm.MaxSize.X = mi.Work.R - mi.Work.L; mm.MaxSize.Y = mi.Work.B - mi.Work.T;
            Marshal.StructureToPtr(mm, lp, false);
        }
        int rgnW = -1, rgnH = -1; bool rgnMax;
        void UpdateShape()
        {
            if (hwnd == IntPtr.Zero) return;
            bool max = WindowState == WindowState.Maximized;
            RECT r; if (!GetWindowRect(hwnd, out r)) return;
            int w = r.R - r.L, h = r.B - r.T;
            if (w == rgnW && h == rgnH && max == rgnMax) return; rgnW = w; rgnH = h; rgnMax = max;
            double sc = 1; var src = PresentationSource.FromVisual(this); if (src != null && src.CompositionTarget != null) sc = src.CompositionTarget.TransformToDevice.M11;
            int rad = (int)Math.Round(16 * sc);
            SetWindowRgn(hwnd, max ? IntPtr.Zero : CreateRoundRectRgn(0, 0, w + 1, h + 1, rad, rad), true);
            root.Clip = max ? null : new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 8, 8);
            if (winEdge != null) winEdge.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
        }

        // ---------- glint: a rare, soft, wide band of light ----------
        // every 90-180 s (never in the first minute), 4-5 s per sweep, random entry side (never the same twice) and angle 15-75 degrees.
        Rectangle glintBand; TranslateTransform glintCenter = new TranslateTransform(); RotateTransform glintRot = new RotateTransform(); TranslateTransform glintOrigin = new TranslateTransform();
        double glintStart = -1, nextGlint = -1, glintLen = 4.5; Random grnd = new Random(); int lastSide = -1;
        Vector glintFrom, glintTo; bool glintForced, sweepPreview;
        static readonly Vector[] Sides = { new Vector(1, 0), new Vector(-1, 0), new Vector(0, 1), new Vector(0, -1), new Vector(1, 1), new Vector(-1, 1), new Vector(1, -1), new Vector(-1, -1) };
        public static readonly string[] SideNames = { "left", "right", "top", "bottom", "top-left", "top-right", "bottom-left", "bottom-right" };
        public string LastGlint = "none yet";
        void EnsureGlint()
        {
            if (glintBand != null || glintHost == null) return;
            var gb = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            double[] pos = { 0, 0.15, 0.3, 0.42, 0.5, 0.58, 0.7, 0.85, 1 }, amt = { 0, 0.06, 0.28, 0.75, 1, 0.75, 0.28, 0.06, 0 };
            for (int i = 0; i < pos.Length; i++) gb.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(40 * amt[i]), 255, 255, 255), pos[i]));
            gb.Freeze();
            glintBand = new Rectangle { Fill = gb, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            var tg = new TransformGroup(); tg.Children.Add(glintOrigin); tg.Children.Add(glintRot); tg.Children.Add(glintCenter); glintBand.RenderTransform = tg;
            glintHost.Children.Add(glintBand);
        }
        public void StartGlint() { glintForced = true; nextGlint = 0; }
        // for screenshots: put a fresh random sweep exactly half-way across, right now
        public void GlintMid()
        {
            EnsureGlint(); if (glintBand == null) return;
            double t = clock.Elapsed.TotalSeconds; BeginGlint(t); sweepPreview = true; glintStart = t - glintLen * 0.5; Glint(t); root.UpdateLayout();
        }
        public void GlintMidDone() { if (!frame.IsEnabled && glintBand != null) { glintBand.Visibility = Visibility.Collapsed; glintStart = -1; } }
        void BeginGlint(double t)
        {
            double w = Math.Max(200, ActualWidth), h = Math.Max(200, ActualHeight);
            int side; do side = grnd.Next(Sides.Length); while (side == lastSide); lastSide = side;
            var d = Sides[side]; d.Normalize();
            // band tilt 15-75 degrees from horizontal, leaning either way, and never nearly parallel to the direction of travel
            Vector a = new Vector(1, 0); double ang = 45;
            for (int i = 0; i < 30; i++)
            {
                ang = 15 + grnd.NextDouble() * 60; double rad = ang * Math.PI / 180; a = new Vector(Math.Cos(rad), (grnd.Next(2) == 0 ? -1 : 1) * Math.Sin(rad));
                if (Math.Abs(a.X * d.X + a.Y * d.Y) < 0.82) break;
            }
            double bandW = 3.5 * 260; // old band was 260 px wide; this one is ~3.5x thicker with feathered edges
            double diag = Math.Sqrt(w * w + h * h);
            glintBand.Width = bandW; glintBand.Height = diag * 2 + bandW;
            glintOrigin.X = -bandW / 2; glintOrigin.Y = -glintBand.Height / 2;
            glintRot.Angle = Math.Atan2(-a.X, a.Y) * 180 / Math.PI; // turns the band's long (vertical) axis onto a
            double sinTo = Math.Sqrt(Math.Max(0.1, 1 - Math.Pow(a.X * d.X + a.Y * d.Y, 2)));
            double reach = (Math.Abs(d.X) * w + Math.Abs(d.Y) * h) / 2 + bandW / 2 / sinTo + 40;
            var c = new Vector(w / 2, h / 2);
            glintFrom = c - d * reach; glintTo = c + d * reach;
            glintLen = 4 + grnd.NextDouble();
            glintStart = t; glintBand.Visibility = Visibility.Visible;
            LastGlint = string.Format("{0:h:mm:ss tt} from the {1}, band at {2:0} degrees, {3:0.0} s", DateTime.Now, SideNames[side], ang, glintLen);
        }
        void Glint(double t)
        {
            EnsureGlint(); if (glintBand == null) return;
            if (nextGlint < 0) nextGlint = t + 90 + grnd.NextDouble() * 90; // first one: 90-180 s after launch
            if (glintStart < 0)
            {
                if (t < nextGlint) return;
                if (!Settings.AllowShake && !glintForced) { nextGlint = t + 90 + grnd.NextDouble() * 90; return; }
                sweepPreview = glintForced; glintForced = false; BeginGlint(t);
            }
            double u = (t - glintStart) / glintLen;
            if (u >= 1 || (!Settings.AllowShake && !sweepPreview))
            {
                glintBand.Visibility = Visibility.Collapsed; glintStart = -1; nextGlint = t + 90 + grnd.NextDouble() * 90; return;
            }
            double e = u * u * (3 - 2 * u);
            var p = glintFrom + (glintTo - glintFrom) * e;
            glintCenter.X = p.X; glintCenter.Y = p.Y;
            glintBand.Opacity = Math.Sin(Math.PI * u) * (Theme.IsLight ? 0.2 : 0.16); // about half the old peak
        }
    }
}
