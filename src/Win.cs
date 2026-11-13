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
    partial class MainWin : Window
    {
        public Engine eng = new Engine();
        public DangerWatch danger = new DangerWatch();
        Grid root; Eyes eyes;
        ListBox rows, hist; StackPanel det, appearBody, dangerBody; Border appear, dangerPop, confirm, scrim, statePill, headerGlow;
        TranslateTransform layoutShake = new TranslateTransform();
        TextBlock headMain, headSub, stateText, stateSince, titleStatus, maxGlyph; WrapPanel chips; Rectangle gradLayer; Ellipse titleDot;
        Button btnMax; ColumnDefinition detCol; RowDefinition histRow; Grid layout;
        ObservableCollection<VM> rowItems = new ObservableCollection<VM>(), histItems = new ObservableCollection<VM>();
        Dictionary<string, VM> pool = new Dictionary<string, VM>(), hpool = new Dictionary<string, VM>();
        HashSet<string> collapsedProcs = new HashSet<string>();
        List<GroupInfo> groups = new List<GroupInfo>(), allGroups = new List<GroupInfo>();
        DispatcherTimer tick = new DispatcherTimer(DispatcherPriority.Background), frame = new DispatcherTimer(DispatcherPriority.Render);
        Stopwatch clock = Stopwatch.StartNew();
        public string headlineText = "";
        IntPtr hwnd;
        WF.NotifyIcon tray; SD.Icon icIdle, icGreen, icYellow, icRed, icRedBright; string trayState = ""; bool blinkOn; DispatcherTimer blink = new DispatcherTimer();
        DateTime lastBalloon = DateTime.MinValue; bool exiting;
        long selfLastCpu; DateTime selfLastAt = DateTime.Now; double selfCpu; double selfCpuLong = -1; long selfLongCpu = -1; DateTime selfLongAt = DateTime.Now; DateTime lastStatus = DateTime.MinValue;
        double tickMs; long ticks; int frames; DateTime frameCountAt = DateTime.Now; double fps;
        string detKey = null; DateTime detAt = DateTime.MinValue; TextBlock detLive;
        EyeMode preview = EyeMode.Idle; DateTime previewUntil = DateTime.MinValue; DangerHit shownDanger;
        Action confirmYes;
        int histSig = -1;
        public static Brush RedB = Frz("#F43F5E"), YelB = Frz("#FBBF24"), GrnB = Frz("#34D399"), UnkB = Frz("#94A3B8"), Dark = Frz("#1E1B4B"), White = Frz("#FFFFFF"),
            RedRow = Frz("#38F43F5E"), AmberRow = Frz("#2EF59E0B"), Clear = Frz("#00000000"), Amber = Frz("#F59E0B");
        public static Brush AppRow = Frz("#14FFFFFF"), AmberText = Frz("#FCD34D"), RedText = Frz("#FDA4AF");
        static Brush Frz(string h) { var b = new SolidColorBrush(Theme.Hex(h)); b.Freeze(); return b; }
        static Brush R(string k) { return (Brush)Application.Current.Resources[k]; }
        static Style St(string k) { return (Style)Application.Current.Resources[k]; }

        T F<T>(string n) where T : class { return root.FindName(n) as T; }

        public MainWin()
        {
            Words.Eng = eng;
            Title = "Agent Watch"; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize;
            Background = Brushes.Transparent; MinWidth = 900; MinHeight = 560;
            Width = Math.Max(900, Settings.W); Height = Math.Max(560, Settings.H);
            if (!double.IsNaN(Settings.X)) { WindowStartupLocation = WindowStartupLocation.Manual; Left = Settings.X; Top = Settings.Y; } else WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // window / taskbar icon: the multi-size Eyes\AgentWatch.ico embedded in the exe (same file as the shortcut icon)
            try { Icon = EyeIcon.WindowIcon(); } catch (Exception ex) { Logger.Error("icon: " + ex.Message); }
            UpdateChrome();
            root = (Grid)XamlReader.Parse(Res("Main.xaml"));
            Content = root;
            rows = F<ListBox>("Rows"); hist = F<ListBox>("Hist"); det = F<StackPanel>("Det"); appearBody = F<StackPanel>("AppearBody"); dangerBody = F<StackPanel>("DangerBody");
            appear = F<Border>("AppearPanel"); dangerPop = F<Border>("DangerPop"); confirm = F<Border>("Confirm"); scrim = F<Border>("Scrim"); statePill = F<Border>("StatePill"); headerGlow = F<Border>("HeaderGlow");
            headMain = F<TextBlock>("HeadMain"); headSub = F<TextBlock>("HeadSub"); stateText = F<TextBlock>("StateText"); stateSince = F<TextBlock>("StateSince"); titleStatus = F<TextBlock>("TitleStatus");
            maxGlyph = F<TextBlock>("MaxGlyph"); chips = F<WrapPanel>("FactChips"); gradLayer = F<Rectangle>("GradLayer"); titleDot = F<Ellipse>("TitleDot");
            btnMax = F<Button>("BtnMax"); detCol = F<ColumnDefinition>("DetCol"); histRow = F<RowDefinition>("HistRow"); layout = F<Grid>("Layout");
            rows.ItemsSource = rowItems; hist.ItemsSource = histItems;
            detCol.Width = new GridLength(Math.Max(280, Settings.DetW)); histRow.Height = new GridLength(Math.Max(70, Settings.HistH));
            foreach (var n in new[] { "BtnGear", "BtnMin", "BtnMax", "BtnClose" }) WindowChrome.SetIsHitTestVisibleInChrome(F<Button>(n), true);
            F<Button>("BtnMin").Click += delegate { WindowState = WindowState.Minimized; };
            btnMax.Click += delegate { ToggleMax(); };
            F<Button>("BtnClose").Click += delegate { HideMain(); };
            F<Button>("BtnGear").Click += delegate { ToggleAppearance(); };
            F<Button>("BtnAppearClose").Click += delegate { ToggleAppearance(); };
            F<Button>("BtnExpand").Click += delegate { foreach (var g in groups) Settings.Expanded.Add(g.App); collapsedProcs.Clear(); Settings.Save(); RefreshRows(); };
            F<Button>("BtnCollapse").Click += delegate { Settings.Expanded.Clear(); Settings.Save(); RefreshRows(); };
            F<Button>("BtnLogs").Click += delegate { OpenLogs(); };
            F<Button>("BtnRules").Click += delegate { try { Process.Start("notepad.exe", "\"" + Explainer.FilePath + "\""); } catch { } };
            BindSwitch("ChkModels", Settings.ShowModelServers, v => Settings.ShowModelServers = v);
            BindSwitch("ChkHelpers", Settings.ShowHelpers, v => Settings.ShowHelpers = v);
            BindSwitch("ChkMcp", Settings.ShowMcp, v => Settings.ShowMcp = v);
            F<Button>("ConfirmNo").Click += delegate { CloseConfirm(); };
            F<Button>("ConfirmYes").Click += delegate { var a = confirmYes; CloseConfirm(); if (a != null) a(); };
            scrim.MouseLeftButtonDown += delegate { if (confirm.Visibility == Visibility.Visible) CloseConfirm(); else if (appear.Visibility == Visibility.Visible) ToggleAppearance(); };

            rows.SelectionChanged += delegate { if (rows.SelectedItem != null) hist.SelectedItem = null; ShowDetails(true); };
            hist.SelectionChanged += delegate { if (hist.SelectedItem != null) { rows.SelectedItem = null; ShowDetails(true); } };
            rows.PreviewMouseLeftButtonDown += RowsMouseDown;
            rows.MouseDoubleClick += delegate { var vm = rows.SelectedItem as VM; if (vm != null && vm.HasKids) Toggle(vm); };
            rows.PreviewKeyDown += RowsKey;

            eyes = new Eyes(F<Canvas>("EyeHost")); eyes.Follow = Settings.EyesFollow; eyes.AllowShake = Settings.AllowShake;
            layout = F<Grid>("Layout"); if (layout != null) layout.RenderTransform = layoutShake;
            ApplyTheme();
            BuildAppearance();

            eng.LoadHistory();
            tick.Interval = TimeSpan.FromMilliseconds(2500); tick.Tick += delegate { Refresh2(); };
            frame.Interval = TimeSpan.FromMilliseconds(40); frame.Tick += delegate { Frame(); };
            SourceInitialized += OnSource;
            StateChanged += delegate
            {
                maxGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
                layout.Margin = WindowState == WindowState.Maximized ? new Thickness(16, 6, 16, 16) : new Thickness(10, 0, 10, 10);
                if (WindowState != WindowState.Minimized) { Settings.Maximized = WindowState == WindowState.Maximized; }
                UpdateAnim();
            };
            IsVisibleChanged += delegate { UpdateAnim(); if (IsVisible) RefreshRows(); };
            Closing += delegate (object s, System.ComponentModel.CancelEventArgs e) { if (!exiting) { e.Cancel = true; HideMain(); } };
            SizeChanged += delegate { if (WindowState == WindowState.Normal) { Settings.W = Width; Settings.H = Height; } };
            LocationChanged += delegate { if (WindowState == WindowState.Normal) { Settings.X = Left; Settings.Y = Top; } };
            SetupTray();
            Refresh2();
            tick.Start();
        }

        static string Res(string name)
        {
            var a = System.Reflection.Assembly.GetExecutingAssembly();
            using (var s = a.GetManifestResourceStream(name)) using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        void BindSwitch(string name, bool val, Action<bool> set)
        {
            var c = F<CheckBox>(name); c.IsChecked = val;
            c.Checked += delegate { set(true); Settings.Save(); RefreshNow(); };
            c.Unchecked += delegate { set(false); Settings.Save(); RefreshNow(); };
        }

        // ---------- window chrome / DWM ----------
        void UpdateChrome()
        {
            var ch = new WindowChrome { CaptionHeight = 40, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(0), UseAeroCaptionButtons = false,
                GlassFrameThickness = Theme.BackdropType != 0 ? new Thickness(-1) : new Thickness(0, 0, 0, 1) };
            WindowChrome.SetWindowChrome(this, ch);
        }
        void ApplyBackdrop()
        {
            if (hwnd == IntPtr.Zero) return;
            N2.Dwm(hwnd, 20, 1);  // immersive dark mode
            N2.Dwm(hwnd, 33, 2);  // round corners
            int bt = Theme.BackdropType;
            N2.Dwm(hwnd, 38, bt == 0 ? 1 : bt); // DWMWA_SYSTEMBACKDROP_TYPE: 1 none, 2 Mica, 3 Acrylic
            N2.Dwm(hwnd, 34, unchecked((int)0xFFFFFFFE)); // no coloured border
            var src = HwndSource.FromHwnd(hwnd);
            if (src != null && src.CompositionTarget != null) src.CompositionTarget.BackgroundColor = bt != 0 ? Colors.Transparent : Theme.Bg1;
        }
        void OnSource(object s, EventArgs e)
        {
            hwnd = new WindowInteropHelper(this).Handle;
            ApplyBackdrop();
            HwndSource.FromHwnd(hwnd).AddHook(Hook);
            if (Settings.Maximized) WindowState = WindowState.Maximized;
        }
        bool OverMax(IntPtr lp)
        {
            long v = lp.ToInt64(); int x = (short)(v & 0xFFFF), y = (short)((v >> 16) & 0xFFFF);
            try
            {
                if (!btnMax.IsVisible) return false;
                var p0 = btnMax.PointToScreen(new Point(0, 0)); var p1 = btnMax.PointToScreen(new Point(btnMax.ActualWidth, btnMax.ActualHeight));
                return x >= p0.X && x < p1.X && y >= p0.Y && y < p1.Y;
            }
            catch { return false; }
        }
        IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            // Windows 11 snap layouts: report the maximize button as HTMAXBUTTON
            if (msg == 0x0084) { if (OverMax(lp)) { handled = true; btnMax.Tag = "hot"; return new IntPtr(9); } if (btnMax.Tag != null) btnMax.Tag = null; }
            else if (msg == 0x00A1 && wp.ToInt64() == 9) { handled = true; return IntPtr.Zero; }
            else if (msg == 0x00A2 && wp.ToInt64() == 9) { handled = true; btnMax.Tag = null; ToggleMax(); return IntPtr.Zero; }
            else if (msg == 0x02A2) { if (btnMax.Tag != null) btnMax.Tag = null; }
            return IntPtr.Zero;
        }
        void ToggleMax() { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; Settings.Maximized = WindowState == WindowState.Maximized; Settings.Save(); }

        public bool IsShown { get { return IsVisible && WindowState != WindowState.Minimized; } }
        public void ShowMain()
        {
            Show(); if (WindowState == WindowState.Minimized) WindowState = Settings.Maximized ? WindowState.Maximized : WindowState.Normal;
            Activate(); if (hwnd != IntPtr.Zero) Native.SetForegroundWindow(hwnd);
            if (!Settings.WindowOpen) { Settings.WindowOpen = true; Settings.Save(); }
            RefreshNow();
        }
        void HideMain() { Hide(); Settings.WindowOpen = false; SaveLayout(); }
        void ToggleMain() { if (IsShown) HideMain(); else ShowMain(); }
        void SaveLayout() { try { if (detCol.ActualWidth > 0) Settings.DetW = detCol.ActualWidth; if (histRow.ActualHeight > 0) Settings.HistH = histRow.ActualHeight; } catch { } Settings.Save(); }

        // ---------- theme / animation ----------
        public void ApplyTheme()
        {
            Theme.Apply(Application.Current.Resources);
            gradLayer.Fill = Theme.Grad;
            gradLayer.Opacity = Theme.IsLight ? 0.18 : 0.26; // fixed, very subtle
            AppRow = Frz(Theme.IsLight ? "#0F000000" : "#14FFFFFF");
            AmberText = Frz(Theme.IsLight ? "#B45309" : "#FCD34D"); RedText = Frz(Theme.IsLight ? "#BE123C" : "#FDA4AF");
            eyes.SetPalette(Theme.Accent, Theme.Accent2, Theme.IsLight);
            titleDot.Fill = new SolidColorBrush(Theme.Accent);
            UpdateChrome(); ApplyBackdrop();
            histSig = -1; pool.Clear(); hpool.Clear(); rowItems.Clear(); histItems.Clear(); detKey = "~";
            if (IsLoaded) RefreshRows();
        }

        void UpdateAnim()
        {
            bool run = IsShown;
            if (run && !frame.IsEnabled) frame.Start();
            else if (!run && frame.IsEnabled) frame.Stop();
        }

        // rare, faint diagonal sheen - like sunlight catching a tilting surface. Off with reduced motion.
        Rectangle glint; TranslateTransform glintT = new TranslateTransform(); double glintStart = -1, nextGlint = -1, glintLen = 2; Random grnd = new Random();
        void Glint(double t)
        {
            if (glint == null)
            {
                var root = Content as Grid; if (root == null || layout == null) return;
                var gb = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
                gb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0));
                gb.GradientStops.Add(new GradientStop(Color.FromArgb(18, 255, 255, 255), 0.5));
                gb.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 1)); gb.Freeze();
                glint = new Rectangle { Width = 260, Fill = gb, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
                var tg = new TransformGroup(); tg.Children.Add(new SkewTransform(-28, 0)); tg.Children.Add(glintT); glint.RenderTransform = tg;
                root.Children.Insert(root.Children.IndexOf(layout) + 1, glint);
                nextGlint = t + 8 + grnd.NextDouble() * 20;
            }
            if (glintStart < 0)
            {
                if (t < nextGlint) return;
                if (!Settings.AllowShake) { nextGlint = t + 30; return; }
                glintStart = t; glintLen = 1.5 + grnd.NextDouble(); glint.Visibility = Visibility.Visible;
            }
            double u = (t - glintStart) / glintLen;
            if (u >= 1 || !Settings.AllowShake) { glint.Visibility = Visibility.Collapsed; glintStart = -1; nextGlint = t + 20 + grnd.NextDouble() * 20; return; }
            double w = ActualWidth, e = u * u * (3 - 2 * u);
            glintT.X = -400 + e * (w + 800);
            glint.Opacity = Math.Sin(Math.PI * u) * (Theme.IsLight ? 0.9 : 0.7);
        }

        void Frame()
        {
            double t = clock.Elapsed.TotalSeconds;
            frames++;
            if ((DateTime.Now - frameCountAt).TotalSeconds >= 5) { fps = frames / (DateTime.Now - frameCountAt).TotalSeconds; frames = 0; frameCountAt = DateTime.Now; }
            try { eyes.Frame(t); } catch (Exception ex) { Logger.Error("eyes: " + ex.Message); }
            if (layout != null)
            {
                double rx = eyes.RootShakeX, ry = eyes.RootShakeY;
                if (rx != 0 || ry != 0 || layoutShake.X != 0 || layoutShake.Y != 0) { layoutShake.X = rx; layoutShake.Y = ry; }
            }
            Glint(t);
            // calm states need fewer frames: drop to ~12 fps while bored / asleep with no bot around
            var want = TimeSpan.FromMilliseconds(eyes.Mode == EyeMode.Idle && eyes.CurrentMood == Mood.Bored && glintStart < 0 ? 80 : 40);
            if (frame.Interval != want) frame.Interval = want;
            if (eyes.Mode == EyeMode.Danger)
            {
                double s = 0.5 + 0.5 * Math.Sin(t * 5); headerGlow.Opacity = 0.35 + 0.5 * s;
                var fx = dangerPop.Effect as DropShadowEffect; if (fx != null && dangerPop.Visibility == Visibility.Visible) fx.BlurRadius = 28 + 22 * s;
            }
        }

        // ---------- tray ----------
        static SD.Icon MakeIcon(SD.Color fill, SD.Color eye, bool warn) { return EyeIcon.Tray(fill, warn); } // drawing lives in ..\Eyes\EyeIcon.cs

        class TrayColors : WF.ProfessionalColorTable
        {
            static SD.Color C(int r, int g, int b) { return SD.Color.FromArgb(r, g, b); }
            public override SD.Color ToolStripDropDownBackground { get { return C(42, 38, 112); } }
            public override SD.Color ImageMarginGradientBegin { get { return C(42, 38, 112); } }
            public override SD.Color ImageMarginGradientMiddle { get { return C(42, 38, 112); } }
            public override SD.Color ImageMarginGradientEnd { get { return C(42, 38, 112); } }
            public override SD.Color MenuItemSelected { get { return C(99, 102, 241); } }
            public override SD.Color MenuItemBorder { get { return C(129, 140, 248); } }
            public override SD.Color MenuBorder { get { return C(79, 70, 229); } }
            public override SD.Color SeparatorDark { get { return C(79, 70, 229); } }
            public override SD.Color SeparatorLight { get { return C(42, 38, 112); } }
            public override SD.Color MenuItemSelectedGradientBegin { get { return C(99, 102, 241); } }
            public override SD.Color MenuItemSelectedGradientEnd { get { return C(99, 102, 241); } }
        }
        void SetupTray()
        {
            icIdle = MakeIcon(SD.Color.FromArgb(124, 92, 246), SD.Color.FromArgb(224, 231, 255), false);
            icGreen = MakeIcon(SD.Color.FromArgb(16, 185, 129), SD.Color.FromArgb(236, 253, 245), false);
            icYellow = MakeIcon(SD.Color.FromArgb(245, 158, 11), SD.Color.FromArgb(255, 251, 235), false);
            icRed = MakeIcon(SD.Color.FromArgb(220, 38, 38), SD.Color.FromArgb(255, 228, 230), true);
            icRedBright = MakeIcon(SD.Color.FromArgb(255, 200, 0), SD.Color.FromArgb(255, 80, 80), true);
            tray = new WF.NotifyIcon { Icon = icIdle, Text = "Agent Watch", Visible = true };
            var m = new WF.ContextMenuStrip { Renderer = new WF.ToolStripProfessionalRenderer(new TrayColors()), ForeColor = SD.Color.FromArgb(224, 231, 255), ShowImageMargin = false };
            m.Items.Add("Open Agent Watch", null, delegate { ShowMain(); });
            m.Items.Add("Appearance...", null, delegate { ShowMain(); if (appear.Visibility != Visibility.Visible) ToggleAppearance(); });
            m.Items.Add("Preview: bot detected", null, delegate { ShowMain(); StartPreview(EyeMode.Nervous); });
            m.Items.Add("Preview: danger alert", null, delegate { ShowMain(); StartPreview(EyeMode.Danger); });
            m.Items.Add("Preview: bored eyes", null, delegate { ShowMain(); eyes.PreviewMood(Mood.Bored, 12); });
            m.Items.Add("Preview: mad eyes", null, delegate { ShowMain(); eyes.PreviewMood(Mood.Mad, 8); });
            m.Items.Add("Preview: poke the eyes", null, delegate { ShowMain(); eyes.Poke(); });
            m.Items.Add("Preview: falling asleep", null, delegate { ShowMain(); eyes.PreviewSleep(); });
            m.Items.Add("Preview: rage shake", null, delegate { ShowMain(); eyes.PreviewMood(Mood.Mad, 8); });
            m.Items.Add(new WF.ToolStripSeparator());
            m.Items.Add("Open logs folder", null, delegate { OpenLogs(); });
            m.Items.Add("Exit Agent Watch", null, delegate { Shutdown(); Application.Current.Shutdown(); });
            tray.ContextMenuStrip = m;
            tray.MouseClick += delegate (object s, WF.MouseEventArgs e) { if (e.Button == WF.MouseButtons.Left) ToggleMain(); };
            tray.BalloonTipClicked += delegate { ShowMain(); };
            blink.Interval = TimeSpan.FromMilliseconds(600);
            blink.Tick += delegate { blinkOn = !blinkOn; tray.Icon = blinkOn ? icRedBright : icRed; };
        }
        void Balloon(string t, string text, WF.ToolTipIcon icon, bool force)
        {
            if (!force && (DateTime.Now - lastBalloon).TotalSeconds < 8) return;
            lastBalloon = DateTime.Now;
            try { tray.ShowBalloonTip(8000, t.Length > 60 ? t.Substring(0, 60) : t, string.IsNullOrEmpty(text) ? " " : (text.Length > 250 ? text.Substring(0, 250) : text), icon); } catch { }
        }
        public void Shutdown() { exiting = true; SaveLayout(); tray.Visible = false; Logger.Event("===== Agent Watch stopped ====="); }
        void OpenLogs() { try { Directory.CreateDirectory(Logger.Dir); Process.Start("explorer.exe", "\"" + Logger.Dir + "\""); } catch { } }
    }
}
