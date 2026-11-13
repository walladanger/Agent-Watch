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
        void RefreshNow() { BuildGroups(); if (IsShown) RefreshRows(); }
        void Refresh2()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                eng.Tick();
                danger.Check(eng);
                BuildGroups();
                UpdateTray();
                UpdateSelf();
                if (IsShown) RefreshRows();
                tickMs = tickMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1; ticks++;
            }
            catch (Exception ex) { Logger.Error("tick: " + ex); }
        }

        void CollectG(GroupInfo g, PInfo p, PInfo r, int depth)
        {
            g.Procs.Add(p); g.Cpu += p.Cpu; g.Ram += p.RamMB;
            if (p.C != null && p.C.Role == Role.Controller) { if (p.Active) g.ActiveCtrls.Add(p); else g.IdleCtrls.Add(p); }
            else if (p.AgentApp) g.Apps.Add(p);
            else if (p.Lv == Level.Yellow) g.Yellows.Add(p);
            if (depth < 40) foreach (var k in p.Kids) if (k.Root == r) CollectG(g, k, r, depth + 1);
        }

        void BuildGroups()
        {
            var d = new Dictionary<string, GroupInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in eng.Roots)
            {
                string app = Engine.AppOf(r);
                GroupInfo g; if (!d.TryGetValue(app, out g)) { g = new GroupInfo(); g.App = app; d[app] = g; }
                g.Roots.Add(r);
            }
            var list = new List<GroupInfo>(d.Values);
            foreach (var g in list)
            {
                g.Roots.Sort((a, b) => a.Create.CompareTo(b.Create));
                foreach (var r in g.Roots) CollectG(g, r, r, 0);
                g.Lv = Level.Green; foreach (var p in g.Procs) if (p.Lv > g.Lv) g.Lv = p.Lv;
                g.Sentence = Words.Sentence(g);
                var re = g.Roots.Count > 0 ? g.Roots[0].E : null;
                g.Category = re != null && !string.IsNullOrEmpty(re.Category) ? re.Category : (g.Roots.Count > 0 && g.Roots[0].C != null && g.Roots[0].C.Role == Role.Controller ? "screen-control" : "agent");
                g.Doing = Words.Doing(g);
            }
            allGroups = new List<GroupInfo>(list);
            var cats = eng.X.ShowCategories;
            list.RemoveAll(g => !(cats.Contains(g.Category) || (g.Category == "model-server" && Settings.ShowModelServers) || g.IdleCtrls.Count + g.ActiveCtrls.Count > 0));
            list.Sort((a, b) => { int c = b.Lv.CompareTo(a.Lv); if (c != 0) return c; return string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase); });
            groups = list;
            var red = new List<string>(); var idle = new List<string>(); int ai = 0;
            foreach (var g in groups) { if (g.ActiveCtrls.Count > 0) red.Add(g.App); if (g.ActiveCtrls.Count == 0 && g.IdleCtrls.Count > 0) idle.Add(g.App); if (g.Category == "agent") ai++; }
            string h;
            if (eng.RemoteSessions.Count > 0) h = "Someone is connected to this PC by Remote Desktop right now.";
            else if (red.Count > 0) h = Words.JoinAnd(red) + (red.Count == 1 ? " is" : " are") + " controlling or scanning your screen right now.";
            else h = "Nothing is controlling your screen right now.";
            h += string.Format(" {0} AI agent app{1} open.", ai, ai == 1 ? "" : "s");
            if (eng.AgentApps.Count > 0) h = string.Join(". ", Words.AppPhrases(eng.AgentApps)) + ". " + h;
            if (idle.Count > 0) h += string.Format(" {0} {1} a screen-control helper loaded but idle.", Words.JoinAnd(idle), idle.Count == 1 ? "has" : "have");
            headlineText = h;
        }

        void UpdateTray()
        {
            Level top = Level.None;
            foreach (var g in groups) if (g.Lv > top) top = g.Lv;
            var dh = danger.Active(eng);
            if (eng.RemoteSessions.Count > 0 || (dh != null && !dh.Test)) top = Level.Red;
            string st = top.ToString();
            if (st != trayState)
            {
                blink.Stop();
                if (top == Level.Red) { blink.Start(); tray.Icon = icRedBright; }
                else tray.Icon = top == Level.Yellow ? icYellow : top == Level.Green ? icGreen : icIdle;
                trayState = st;
            }
            string tip = headlineText.Length > 0 ? headlineText : "Agent Watch";
            foreach (var a in eng.AgentApps) { tip = a.AppAgent + " opened " + a.AppName; break; }
            foreach (var e in eng.OpenEpisodes.Values) { tip = e.Agent + " is controlling " + (e.Target ?? "the screen"); break; }
            if (dh != null && !dh.Test) tip = "DANGER: " + dh.Agent + " - " + dh.Title;
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
            if (tray.Text != tip) tray.Text = tip;

            foreach (var hit in danger.New)
            {
                var ep = new Episode { Kind = "danger", Start = hit.When, End = hit.When, Open = false, App = hit.App, Agent = hit.Agent, Proc = hit.ProcName, Pid = hit.Pid, Target = hit.Title, Trigger = "rule " + (hit.Rule != null ? hit.Rule.Id : "?"), Detail = hit.Snippet };
                eng.History.Insert(0, ep);
                try { File.AppendAllText(Path.Combine(Logger.Dir, "screen-episodes.tsv"), ep.ToLine() + Environment.NewLine, Encoding.UTF8); } catch { }
                Balloon("DANGER: " + hit.Agent + " - " + hit.Title, hit.ProcName + " PID " + hit.Pid + ". " + hit.Snippet, WF.ToolTipIcon.Error, true);
                if (!IsShown) ShowMain();
            }
            if (eng.FirstTick) return;
            foreach (var p in eng.NewControllers)
                Balloon("Screen-control helper loaded", string.Format("{0}: {1} (PID {2}) started{3}.", Engine.AppOf(p), Words.Fr(p), p.Pid, p.Parent != null ? " by " + p.Parent.Name : ""), WF.ToolTipIcon.Info, false);
            foreach (var e in eng.NewAppEps)
                Balloon(e.Agent + " opened " + e.Target, string.Format("At {0:h:mm:ss tt} ET ({1}, PID {2}). {3}", e.Start, e.Proc, e.Pid, e.Detail), WF.ToolTipIcon.Warning, true);
            foreach (var e in eng.NewEpisodes)
                Balloon(e.Agent + " is controlling " + (e.Target ?? "your screen"), string.Format("At {0:h:mm:ss tt} ET via {1} (PID {2}). {3}", e.Start, e.Proc, e.Pid, e.Trigger), WF.ToolTipIcon.Warning, true);
            if (eng.NewEpisodes.Count == 0) foreach (var p in eng.NewlyActive)
                Balloon(Engine.AppOf(p) + " is using your screen", string.Format("{0} (PID {1}) is active. Click for details.", Words.Fr(p), p.Pid), WF.ToolTipIcon.Warning, false);
        }

        bool firstBalloon;
        public void StartupBalloon() { if (firstBalloon) return; firstBalloon = true; Balloon("Agent Watch is running", headlineText, WF.ToolTipIcon.Info, true); }

        void UpdateSelf()
        {
            long cpu = eng.SelfCpu100ns; var now = DateTime.Now;
            double sec = (now - selfLastAt).TotalSeconds;
            if (sec > 0.5 && selfLastCpu > 0) selfCpu = (cpu - selfLastCpu) / 1e7 * 100.0 / (sec * Environment.ProcessorCount);
            selfLastCpu = cpu; selfLastAt = now;
            if (selfLongCpu < 0) { selfLongCpu = cpu; selfLongAt = now; }
            else if ((now - selfLongAt).TotalSeconds >= 60) { selfCpuLong = (cpu - selfLongCpu) / 1e7 * 100.0 / ((now - selfLongAt).TotalSeconds * Environment.ProcessorCount); selfLongCpu = cpu; selfLongAt = now; }
            if (IsShown) titleStatus.Text = string.Format("{0} processes  ·  Agent Watch uses {1:0.0}% of one core  ·  updated {2:h:mm:ss tt} ET{3}", eng.ProcessCount, (selfCpuLong >= 0 ? selfCpuLong : selfCpu) * Environment.ProcessorCount, now, eng.X.LoadError != null ? "  ·  " + eng.X.LoadError : "");
            if ((now - lastStatus).TotalSeconds >= 30) { lastStatus = now; WriteStatus(); }
        }

        void WriteStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Agent Watch v2 status " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ET");
            sb.AppendLine(string.Format("Self: CPU {0:0.00}% of machine = {1:0.0}% of one core (1-min avg {2}), private RAM {3:0} MB, processes {4}, avg tick {5:0.0} ms, ticks {6}, cores {7}",
                selfCpu, selfCpu * Environment.ProcessorCount, selfCpuLong >= 0 ? string.Format("{0:0.00}% machine / {1:0.0}% of one core", selfCpuLong, selfCpuLong * Environment.ProcessorCount) : "n/a", eng.SelfPrivBytes / 1048576.0, eng.ProcessCount, tickMs, ticks, Environment.ProcessorCount));
            sb.AppendLine(string.Format("UI: visible {0}, animation {1} ({2:0.0} fps), eyes {3}, preset {4}, finish {5}, backdrop {6}, transparency {7:0}, reduced motion {8}, glint {9}",
                IsShown, frame.IsEnabled ? "running" : "paused", fps, eyes.Mode + "/" + eyes.CurrentMood, Settings.Preset, Settings.Finish, Theme.BackdropType == 0 ? "none" : Theme.BackdropType == 2 ? "Mica" : "Acrylic", Settings.Transparency, !Settings.AllowShake, glintStart >= 0 ? "now" : "idle"));
            sb.AppendLine("Headline: " + headlineText);
            sb.AppendLine("Tray: " + trayState + (eng.X.LoadError != null ? " | " + eng.X.LoadError : string.Format(" | {0} explanation rules, {1} danger rules", eng.X.Rules.Count, eng.X.Dangers.Count)));
            sb.AppendLine("Apps:");
            foreach (var g in groups) sb.AppendLine(string.Format("  [{0}] {1} ({2} processes, {3:0.0}% CPU): {4} | doing: {5}", g.Lv.ToString().ToUpper(), g.App, g.Procs.Count, g.Cpu, g.Sentence, g.Doing));
            sb.AppendLine("Screen controllers:");
            foreach (var p in eng.Controllers) sb.AppendLine(string.Format("  {0} > {1} PID {2} CPU {3:0.00}% {4}", Engine.AppOf(p), p.Name, p.Pid, p.Cpu, p.Active ? "ACTIVE" : "idle (loaded)"));
            sb.AppendLine("History (newest first):");
            for (int i = 0; i < Math.Min(8, eng.History.Count); i++) { var e = eng.History[i]; sb.AppendLine(string.Format("  {0:yyyy-MM-dd HH:mm:ss} - {1} | {2} | {3} | {4} PID {5}", e.Start, e.Open ? "(now)" : e.End.ToString("HH:mm:ss"), e.Agent, e.What, e.Proc, e.Pid)); }
            sb.AppendLine("Danger hits: " + danger.Hits.Count + (danger.Hits.Count > 0 ? " (latest: " + danger.Hits[0].Title + " by " + danger.Hits[0].Agent + (danger.Hits[0].Test ? ", test" : "") + ")" : ""));
            sb.AppendLine("Agent-launched apps: " + string.Join("; ", eng.AgentApps.ConvertAll(a => a.AppName + " PID " + a.Pid + " by " + a.AppAgent)));
            sb.AppendLine("Hidden by category filter: " + string.Join(", ", allGroups.FindAll(g => !groups.Contains(g)).ConvertAll(g => g.App + " (" + g.Category + ")")));
            Logger.WriteText("status.txt", sb.ToString());
        }

        // ---------- rows (flattened, expandable tree) ----------
        VM Get(Dictionary<string, VM> pl, string key) { VM v; if (!pl.TryGetValue(key, out v)) { v = new VM { Key = key }; pl[key] = v; } return v; }
        static Brush LvBrush(Level l) { return l == Level.Red ? RedB : l == Level.Yellow ? YelB : l == Level.Green ? GrnB : UnkB; }

        bool HiddenProc(PInfo p)
        {
            string n = p.Name ?? "";
            if (p.Lv == Level.Red || p.AgentApp || (p.C != null && p.C.Role == Role.Controller)) return false;
            if (!Settings.ShowHelpers && n.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Settings.ShowMcp && p.E != null && p.E.Category == "mcp") return true;
            return false;
        }
        void VisibleKids(PInfo p, PInfo root, List<PInfo> outl, int depth)
        {
            if (depth > 40) return;
            var kids = new List<PInfo>(); foreach (var k in p.Kids) if (k.Root == root) kids.Add(k);
            kids.Sort((a, b) => a.Create.CompareTo(b.Create));
            foreach (var k in kids) { if (HiddenProc(k)) VisibleKids(k, root, outl, depth + 1); else outl.Add(k); }
        }

        void RefreshRows()
        {
            var list = new List<VM>();
            foreach (var r in eng.RemoteSessions)
            {
                var v = Get(pool, "rdp:" + r); v.Name = "Remote Desktop"; v.Sub = ""; v.Doing = r; v.Pill = "RED - REMOTE"; v.PillBack = RedB; v.PillFore = White; v.Stripe = RedB; v.RowBack = RedRow; v.Glyph = ""; v.Weight = FontWeights.SemiBold; v.RowH = 38; v.NameSize = 14; v.DotVis = Visibility.Visible; v.Dot = RedB; v.Pid = ""; v.Cpu = ""; v.Ram = ""; v.DoingFore = RedText;
                list.Add(v);
            }
            foreach (var g in groups)
            {
                var v = Get(pool, "g:" + g.App); v.G = g; v.IsApp = true; v.HasKids = true;
                v.Expanded = Settings.Expanded.Contains(g.App);
                v.Name = g.App; v.Sub = g.Procs.Count + (g.Procs.Count == 1 ? " process" : " processes");
                v.Doing = g.Doing; v.Tip = g.Sentence;
                bool active = g.ActiveCtrls.Count > 0, apps = g.Apps.Count > 0;
                v.DoingFore = active ? RedText : apps ? AmberText : R("Text");
                v.DoingWeight = active || apps ? FontWeights.SemiBold : FontWeights.Normal;
                v.Pill = g.Lv == Level.Red ? "RED - ACTIVE" : g.Lv == Level.Yellow ? (apps ? "YELLOW - USING APP" : g.IdleCtrls.Count > 0 ? "YELLOW - LOADED" : "YELLOW") : Words.LevelWord(g.Lv);
                v.PillBack = LvBrush(g.Lv); v.PillFore = g.Lv == Level.Red ? White : Dark;
                v.Stripe = LvBrush(g.Lv); v.RowBack = g.Lv == Level.Red ? RedRow : apps ? AmberRow : AppRow;
                v.Glyph = v.Expanded ? "\uE70D" : "\uE76C"; v.Weight = FontWeights.SemiBold; v.NameSize = 14.5; v.RowH = 38; v.IndentW = 0;
                v.DotVis = Visibility.Visible; v.Dot = LvBrush(g.Lv);
                v.Pid = g.Roots.Count == 1 ? g.Roots[0].Pid.ToString() : ""; v.Cpu = g.Cpu.ToString("0.0"); v.Ram = g.Ram.ToString("0");
                list.Add(v);
                if (v.Expanded)
                    foreach (var r in g.Roots)
                    {
                        if (HiddenProc(r)) { var ks = new List<PInfo>(); VisibleKids(r, r, ks, 0); foreach (var k in ks) AddProc(list, k, r, 1); }
                        else AddProc(list, r, r, 1);
                    }
            }
            Sync(rowItems, list);
            BuildHistory();
            UpdateHeader();
            if (detKey != null && (DateTime.Now - detAt).TotalSeconds > 4.5) ShowDetails(false);
        }

        void AddProc(List<VM> list, PInfo p, PInfo root, int depth)
        {
            var kids = new List<PInfo>(); VisibleKids(p, root, kids, 0);
            var v = Get(pool, "p:" + p.Key); v.P = p; v.G = null; v.IsApp = false; v.HasKids = kids.Count > 0;
            v.Expanded = v.HasKids && !collapsedProcs.Contains(p.Key);
            string fr = Words.Fr(p);
            v.Name = p.Name; v.Sub = fr != p.Name ? fr : "";
            Level lvl = p.Lv; string pill;
            bool unknown = p.E == null && p.C == null && !p.AgentApp;
            if (p.C != null && p.C.Role == Role.Controller) pill = p.Active ? "RED - CONTROLLING" : "YELLOW - IDLE";
            else if (p.AgentApp) pill = lvl == Level.Red ? "RED - DRIVEN" : "YELLOW - AGENT APP";
            else pill = unknown ? "UNKNOWN" : Words.LevelWord(lvl);
            v.Pill = pill; v.PillBack = unknown ? UnkB : LvBrush(lvl); v.PillFore = lvl == Level.Red ? White : Dark;
            string doing;
            if (p.C != null && p.C.Role == Role.Controller) doing = p.Active ? "Controlling the screen" + (string.IsNullOrEmpty(p.ActiveReason) ? "" : " (" + p.ActiveReason + ")") : "Loaded, waiting";
            else if (p.AgentApp) doing = string.Format("Opened by {0} at {1:h:mm tt}{2}", p.AppAgent, p.StartLocal, p.WinTitle != null ? " - \"" + p.WinTitle + "\"" : " (no window)");
            else if (fr.StartsWith("Running a") || fr.StartsWith("Claude's built-in")) doing = fr;
            else doing = p.Cpu >= 0.5 ? string.Format("Working ({0:0.0}% CPU)", p.Cpu) : "Idle";
            foreach (var dh in danger.Hits) if (dh.P == p && !dh.Dismissed && !dh.Test) { doing = "DANGER: " + dh.Title; v.Pill = "RED - DANGER"; v.PillBack = RedB; v.PillFore = White; lvl = Level.Red; break; }
            v.Doing = doing; v.DoingFore = lvl == Level.Red ? RedText : p.AgentApp ? AmberText : R("Text");
            v.DoingWeight = p.AgentApp || lvl == Level.Red ? FontWeights.SemiBold : FontWeights.Normal;
            v.Stripe = lvl == Level.Red ? RedB : p.AgentApp ? YelB : Clear; v.RowBack = lvl == Level.Red ? RedRow : p.AgentApp ? AmberRow : Clear;
            v.Glyph = v.HasKids ? (v.Expanded ? "\uE70D" : "\uE76C") : ""; v.Weight = p.AgentApp || depth == 1 ? FontWeights.SemiBold : FontWeights.Normal; v.NameSize = 13; v.RowH = 30;
            v.IndentW = 18 + (depth - 1) * 18; v.DotVis = Visibility.Collapsed;
            v.Pid = p.Pid.ToString(); v.Cpu = p.Cpu.ToString("0.0"); v.Ram = p.RamMB.ToString("0");
            v.Tip = (p.Path ?? p.Name) + (p.Cmd != null ? "\n" + Redactor.Clean(p.Cmd, 200) : "");
            list.Add(v);
            if (v.Expanded) foreach (var k in kids) AddProc(list, k, root, depth + 1);
        }

        static void Sync(ObservableCollection<VM> col, List<VM> list)
        {
            var want = new HashSet<VM>(list);
            for (int i = col.Count - 1; i >= 0; i--) if (!want.Contains(col[i])) col.RemoveAt(i);
            for (int i = 0; i < list.Count; i++)
            {
                if (i < col.Count && col[i] == list[i]) continue;
                int at = -1; for (int j = i + 1; j < col.Count; j++) if (col[j] == list[i]) { at = j; break; }
                if (at >= 0) col.Move(at, i); else col.Insert(i, list[i]);
            }
            while (col.Count > list.Count) col.RemoveAt(col.Count - 1);
        }

        void Toggle(VM v)
        {
            if (v.IsApp) { if (Settings.Expanded.Contains(v.G.App)) Settings.Expanded.Remove(v.G.App); else Settings.Expanded.Add(v.G.App); Settings.Save(); }
            else if (v.P != null) { if (collapsedProcs.Contains(v.P.Key)) collapsedProcs.Remove(v.P.Key); else collapsedProcs.Add(v.P.Key); }
            RefreshRows();
        }
        void RowsMouseDown(object s, MouseButtonEventArgs e)
        {
            for (var d = e.OriginalSource as DependencyObject; d != null && d != rows; d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D) ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            {
                var f2 = d as FrameworkElement;
                if (f2 != null && "exp".Equals(f2.Tag)) { var vm = f2.DataContext as VM; if (vm != null && vm.HasKids) { rows.SelectedItem = vm; Toggle(vm); e.Handled = true; } return; }
                if (d is ListBoxItem) return;
            }
        }
        void RowsKey(object s, KeyEventArgs e)
        {
            var vm = rows.SelectedItem as VM; if (vm == null) return;
            if (e.Key == Key.Right && vm.HasKids && !vm.Expanded) { Toggle(vm); e.Handled = true; }
            else if (e.Key == Key.Left && vm.HasKids && vm.Expanded) { Toggle(vm); e.Handled = true; }
            else if (e.Key == Key.Delete && vm.P != null) { AskKill(vm.P, false); e.Handled = true; }
        }

        void BuildHistory()
        {
            int sig = eng.History.Count * 31; for (int i = 0; i < Math.Min(5, eng.History.Count); i++) { var h = eng.History[i]; sig = sig * 17 + h.End.ToString().GetHashCode() + (h.Open ? 7 : 0) + (h.Detail ?? "").Length + (h.Target ?? "").GetHashCode(); }
            if (sig == histSig) return; histSig = sig;
            var list = new List<VM>(); var seenK = new HashSet<string>();
            foreach (var e in eng.History)
            {
                string k = "h:" + e.Kind + e.Start.Ticks + "|" + e.Pid + "|" + e.Target; if (!seenK.Add(k)) continue;
                var v = Get(hpool, k); v.Ep = e;
                v.Name = e.Start.ToString("MMM d  h:mm:ss tt"); v.Sub = e.Open ? "now" : e.End.ToString("h:mm:ss tt"); v.Pill = e.Agent; v.Doing = e.What;
                v.Cpu = e.Proc + " PID " + e.Pid + (string.IsNullOrEmpty(e.Detail) ? "" : "  ·  " + e.Detail);
                bool dangerK = e.Kind == "danger", red = e.Open && e.Kind != "app";
                v.DoingFore = red || dangerK ? RedText : e.Open ? AmberText : R("Dim");
                v.Weight = e.Open || dangerK ? FontWeights.SemiBold : FontWeights.Normal;
                v.Stripe = dangerK || red ? RedB : e.Open ? YelB : Clear; v.RowBack = red || dangerK ? RedRow : e.Open ? AmberRow : Clear;
                v.Tip = e.What + "\n" + e.Trigger + (string.IsNullOrEmpty(e.Detail) ? "" : "\n" + e.Detail);
                list.Add(v);
                if (list.Count >= 300) break;
            }
            Sync(histItems, list);
        }
    }
}
