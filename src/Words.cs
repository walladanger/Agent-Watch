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
    class GroupInfo
    {
        public string App; public List<PInfo> Roots = new List<PInfo>(); public List<PInfo> Procs = new List<PInfo>();
        public Level Lv = Level.Green; public string Sentence; public double Cpu, Ram;
        public List<PInfo> ActiveCtrls = new List<PInfo>(), IdleCtrls = new List<PInfo>(), Yellows = new List<PInfo>(), Apps = new List<PInfo>();
        public string Category = "agent", Doing = "Idle";
    }

    // plain-English text helpers (ported from v1)
    static class Words
    {
        public static Engine Eng;
        public static string Fr(PInfo p)
        {
            if (p.AgentApp) return p.AppName + " (opened by " + p.AppAgent + ")";
            string n = (p.Name ?? "").ToLowerInvariant();
            if ((p.E != null && p.E.Id == "agent-shell") || (p.Root != null && (p.E == null || p.E.Id == "cmd-helper") && (n == "powershell.exe" || n == "pwsh.exe" || n == "cmd.exe" || n == "bash.exe")))
            { var sum = Engine.ShellSummary(p); if (sum != null) return sum; }
            return p.E != null && p.E.Friendly != null ? p.E.Friendly : (p.C != null ? p.C.Label : p.Name);
        }
        public static string AppPhrase(PInfo a, bool busy, int count)
        {
            return string.Format("{0} opened {1}{2} at {3:h:mm tt}{4}", Engine.Possessive(a.AppAgent), a.AppName, count > 1 ? " (" + count + " copies)" : "", a.StartLocal,
                busy ? " and is working in it" : (a.WinTitle == null ? " (running in the background, no window)" : " - it is still open"));
        }
        public static List<string> AppPhrases(List<PInfo> apps)
        {
            var order = new List<string>(); var first = new Dictionary<string, PInfo>(); var cnt = new Dictionary<string, int>(); var busy = new Dictionary<string, bool>();
            foreach (var a in apps)
            {
                string k = a.AppAgent + "|" + a.AppName;
                if (!first.ContainsKey(k)) { first[k] = a; cnt[k] = 0; busy[k] = false; order.Add(k); }
                cnt[k]++; if ((DateTime.Now - a.LastBusy).TotalSeconds < 20) busy[k] = true;
                if (a.Create < first[k].Create) first[k] = a;
            }
            var res = new List<string>(); foreach (var k in order) res.Add(AppPhrase(first[k], busy[k], cnt[k]));
            return res;
        }
        public static string JoinAnd(List<string> l)
        {
            if (l.Count == 0) return "";
            if (l.Count == 1) return l[0];
            if (l.Count == 2) return l[0] + " and " + l[1];
            return string.Join(", ", l.GetRange(0, l.Count - 1)) + " and " + l[l.Count - 1];
        }
        public static string LevelWord(Level l) { return l == Level.Red ? "RED" : l == Level.Yellow ? "YELLOW" : l == Level.Green ? "GREEN" : "UNKNOWN"; }
        public static string Dur(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return (int)t.TotalSeconds + "s";
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + "m " + t.Seconds + "s";
            return (int)t.TotalHours + "h " + t.Minutes + "m";
        }
        public static string Doing(GroupInfo g)
        {
            var eng = Eng;
            if (g.ActiveCtrls.Count > 0)
            {
                Episode ep; eng.OpenEpisodes.TryGetValue(g.App, out ep);
                string who = ep != null ? ep.Agent : eng.AgentShort(g.ActiveCtrls[0]);
                return "CONTROLLING " + (ep != null && ep.Target != null ? ep.Target.ToUpper() : "THE SCREEN") + " (" + who + ")";
            }
            if (g.Apps.Count > 0)
            {
                var a = g.Apps[g.Apps.Count - 1]; foreach (var x in g.Apps) if (x.Create > a.Create) a = x;
                int same = 0; foreach (var x in g.Apps) if (x.AppName == a.AppName) same++;
                int others = g.Apps.Count - same;
                return string.Format("Using {0}{1} (opened {2:h:mm tt} by {3}){4}", a.AppName, same > 1 ? " x" + same : "", a.StartLocal, a.AppAgent, others > 0 ? " +" + others + " other app" + (others > 1 ? "s" : "") : "");
            }
            var tools = new List<string>();
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var r in g.Roots) rootNames.Add(r.Name);
            foreach (var p in g.Procs)
            {
                if (rootNames.Contains(p.Name) || p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                bool fresh = p.Create > 0 && (DateTime.Now - p.StartLocal).TotalSeconds < 60;
                bool busyTool = p.Cpu >= 0.5 && p.E != null && (p.E.Category == "mcp" || p.E.Id == "agent-shell");
                if (fresh || busyTool) { string f = Fr(p); if (!tools.Contains(f)) tools.Add(f); }
            }
            if (tools.Count > 0) return tools.Count == 1 ? tools[0] : "Running tools (" + string.Join("; ", tools.GetRange(0, Math.Min(2, tools.Count))) + (tools.Count > 2 ? "; ..." : "") + ")";
            if (g.Cpu >= 0.5) return string.Format("Working / thinking ({0:0.0}% CPU)", g.Cpu);
            return "Idle";
        }
        public static string Sentence(GroupInfo g)
        {
            var eng = Eng;
            if (g.ActiveCtrls.Count > 0)
            {
                var p = g.ActiveCtrls[0];
                bool log = eng.OpenBotScanning && g.App == "OpenBot";
                Episode ep; eng.OpenEpisodes.TryGetValue(g.App, out ep);
                return string.Format("{0} is controlling {1} right now through {2} ({3:0.0}% CPU{4}).", ep != null ? ep.Agent : eng.AgentShort(p), ep != null && ep.Target != null ? ep.Target : "your screen", Fr(p), p.Cpu,
                    log ? ", scanning windows " + (int)(DateTime.Now - eng.CuaLogWrite).TotalSeconds + "s ago" : "");
            }
            var sb = new StringBuilder();
            foreach (var ph in AppPhrases(g.Apps)) sb.Append(ph).Append(". ");
            sb.Append(g.App).Append(" is open");
            if (g.IdleCtrls.Count > 0)
            {
                double c = 0; foreach (var p in g.IdleCtrls) c += p.Cpu;
                if (g.IdleCtrls.Count == 1) sb.AppendFormat("; its screen-control helper is loaded but idle ({0:0.0}% CPU)", c);
                else sb.AppendFormat("; {0} screen-control helpers are loaded but idle ({1:0.0}% CPU)", g.IdleCtrls.Count, c);
            }
            if (g.Yellows.Count > 0)
            {
                var names = new List<string>();
                foreach (var p in g.Yellows) { string f = Fr(p); if (!names.Contains(f)) names.Add(f); }
                int mcp = 0; foreach (var p in g.Yellows) if (Fr(p).Contains("MCP")) mcp++;
                if (mcp > 0) sb.AppendFormat("; {0} tool server{1} (MCP)", mcp, mcp == 1 ? "" : "s");
                int other = g.Yellows.Count - mcp;
                if (other > 0)
                {
                    var o = new List<string>(); foreach (var n in names) if (!n.Contains("MCP") && o.Count < 2) o.Add(n);
                    sb.AppendFormat("; {0} part{1} that can act for you ({2}{3})", other, other == 1 ? "" : "s", string.Join(", ", o), names.Count - (mcp > 0 ? 1 : 0) > 2 ? ", ..." : "");
                }
            }
            if (g.IdleCtrls.Count == 0 && g.Yellows.Count == 0 && g.Apps.Count == 0) sb.Append(" and running normally");
            sb.AppendFormat(". {0} process{1}.", g.Procs.Count, g.Procs.Count == 1 ? "" : "es");
            return sb.ToString();
        }
    }
}
