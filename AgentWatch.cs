// Agent Watch - shows live AI agents, their child processes, and screen-control helpers.
// Built with the .NET Framework csc.exe (C# 5). No admin, no installs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyTitle("Agent Watch")]
[assembly: System.Reflection.AssemblyProduct("Agent Watch")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace AgentWatch
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PROCESSENTRY32W
        {
            public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
            public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID; public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32FirstW(IntPtr h, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool Process32NextW(IntPtr h, ref PROCESSENTRY32W e);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long create, out long exit, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
        [DllImport("ntdll.dll")] public static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int retLen);
        [DllImport("ntdll.dll")] public static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);
        [StructLayout(LayoutKind.Sequential)]
        public struct PMC
        {
            public uint cb; public uint PageFaultCount; public UIntPtr PeakWorkingSetSize; public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage; public UIntPtr QuotaPagedPoolUsage; public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage; public UIntPtr PagefileUsage; public UIntPtr PeakPagefileUsage;
        }
        [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")] public static extern bool GetProcessMemoryInfo(IntPtr h, out PMC pmc, uint cb);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WTS_SESSION_INFO { public int SessionId; [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName; public int State; }
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] public static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr info, out int count);
        [DllImport("wtsapi32.dll")] public static extern void WTSFreeMemory(IntPtr p);
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        public const uint QUERY_LIMITED = 0x1000;
        public static readonly IntPtr INVALID = new IntPtr(-1);

        public static string GetCmd(IntPtr h)
        {
            int size = 2048;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int ret;
                    int st = NtQueryInformationProcess(h, 60, buf, size, out ret);
                    if (st == 0)
                    {
                        int l = (ushort)Marshal.ReadInt16(buf);
                        IntPtr p = Marshal.ReadIntPtr(buf, IntPtr.Size);
                        if (p == IntPtr.Zero || l == 0) return "";
                        return Marshal.PtrToStringUni(p, l / 2);
                    }
                    if (ret > size && ret < (1 << 20)) { size = ret + 16; continue; }
                    return null;
                }
                catch { return null; }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
    }

    enum Role { None, Agent, Controller, Harmless }

    class Cls
    {
        public string App; public string Label; public Role Role;
        public Cls(string app, string label, Role role) { App = app; Label = label; Role = role; }
    }

    enum Level { None = 0, Green = 1, Yellow = 2, Red = 3 }

    class PInfo
    {
        public int Pid, Ppid; public string Name, Path, Cmd; public long Create;
        public Cls C, C0; public Expl E; public int EVer = -1; public bool CmdTried; public Level Lv;
        public long LastCpu, LastStamp; public double Cpu; public double RamMB;
        public PInfo Parent; public List<PInfo> Kids = new List<PInfo>();
        public PInfo MatchedParent; public PInfo Root;
        public bool Active; public DateTime LastActive = DateTime.MinValue; public string ActiveReason = "";
        public string Key { get { return Pid + ":" + Create; } }
        public DateTime StartLocal { get { return Create > 0 ? DateTime.FromFileTimeUtc(Create).ToLocalTime() : DateTime.MinValue; } }
    }

    static class Classifier
    {
        static readonly HashSet<string> Interp = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "node.exe","python.exe","pythonw.exe","python3.exe","bun.exe","deno.exe","npx.exe","uv.exe","java.exe","javaw.exe","py.exe","pyw.exe" };

        public static bool NeedsCmd(string name) { return Interp.Contains(name) || name.StartsWith("python", StringComparison.OrdinalIgnoreCase); }

        public static Cls Classify(PInfo p)
        {
            string n = (p.Name ?? "").ToLowerInvariant();
            string pa = (p.Path ?? "").ToLowerInvariant();
            string c = (p.Cmd ?? "").ToLowerInvariant();

            // ---- screen controllers / remote access ----
            if (n == "cua-driver.exe") return new Cls(pa.Contains("\\openbot\\") ? "OpenBot" : null, "Computer-use driver (sees + controls screen)", Role.Controller);
            if (n.StartsWith("codex-computer-use")) return new Cls("Codex", "Codex computer-use (controls screen)", Role.Controller);
            if (n.Contains("computer-use") || n.Contains("computeruse") || n.Contains("computer_use")) return new Cls(null, "Computer-use helper", Role.Controller);
            if ((n == "node.exe") && (c.Contains("unified-computer-use") || c.Contains("computer-use"))) return new Cls("Codex", "Codex computer-use plugin", Role.Controller);
            if (n.StartsWith("autohotkey") || n.StartsWith("nircmd")) return new Cls("Automation", "Input automation (" + p.Name + ")", Role.Controller);
            if (n == "msrdc.exe")
            {
                if (pa.Contains("\\wsl\\") || pa.Length == 0 && c.Contains("wslg")) return new Cls("WSL graphics (WSLg)", "Harmless: WSL Linux-app windows (local only)", Role.Harmless);
                return new Cls("Remote Desktop", "Remote Desktop client session", Role.Controller);
            }
            if (n == "anydesk.exe" || n.StartsWith("teamviewer") || n == "rustdesk.exe" || n.StartsWith("winvnc") || n.StartsWith("tvnserver") ||
                n.StartsWith("vncserver") || n == "uvnc_service.exe" || n == "parsecd.exe" || n == "remoting_host.exe" || n.StartsWith("srservice") || n == "screenconnect.windowsclient.exe")
                return new Cls("Remote access", "Remote access / screen sharing (" + p.Name + ")", Role.Controller);
            if (n.StartsWith("python") || n == "py.exe" || n == "pyw.exe")
            {
                if (c.Contains("pyautogui") || c.Contains("computer_use") || c.Contains("computer-use") || Regex.IsMatch(c, @"\bcua\b")) return new Cls(null, "Python screen automation", Role.Controller);
            }

            // ---- agents / AI apps ----
            if (n == "openbot.exe") return new Cls("OpenBot", "OpenBot", Role.Agent);
            if (pa.Contains("\\openbot\\provider-runtimes\\"))
            {
                if (n == "antigravity.exe") return new Cls("OpenBot", "OpenBot agent: Gemini (Antigravity)", Role.Agent);
                if (n == "pi.exe") return new Cls("OpenBot", "OpenBot agent: DeepSeek (Pi)", Role.Agent);
                return new Cls("OpenBot", "OpenBot provider runtime", Role.Agent);
            }
            if (n == "grok bot.exe") return new Cls("Grok Bot", "Grok Bot", Role.Agent);
            if (n == "chatgpt.exe") return pa.Contains("openai.codex") ? new Cls("Codex", "Codex app", Role.Agent) : new Cls("ChatGPT", "ChatGPT", Role.Agent);
            if (n == "codex.exe" || n.StartsWith("codex-")) return new Cls("Codex", "Codex agent (" + p.Name + ")", Role.Agent);
            if (n == "claude.exe") return pa.Contains("\\windowsapps\\claude") || pa.Contains("\\anthropicclaude\\") ? new Cls("Claude", "Claude desktop", Role.Agent) : new Cls("Claude Code", "Claude Code CLI", Role.Agent);
            if (n.StartsWith("opencode")) return new Cls("OpenCode", "OpenCode (" + p.Name + ")", Role.Agent);
            if (n.StartsWith("ollama")) return new Cls("Ollama", "Ollama (" + p.Name + ")", Role.Agent);
            if (n == "lm studio.exe" || n == "lms.exe" || n.StartsWith("llmster")) return new Cls("LM Studio", "LM Studio", Role.Agent);
            if (n.StartsWith("strata") || pa.Contains("\\strata\\") || c.Contains("strata_control")) return new Cls("Strata", "Strata", Role.Agent);
            if (n.StartsWith("invokeai") || n == "invoke community edition.exe") return new Cls("InvokeAI", "InvokeAI", Role.Agent);
            if (n == "jan.exe") return new Cls("Jan", "Jan", Role.Agent);
            if (n.StartsWith("comfyui") || pa.Contains("\\comfyui")) return new Cls("ComfyUI", "ComfyUI", Role.Agent);
            if (n == "cloudflared.exe") return new Cls("cloudflared tunnel", "cloudflared tunnel", Role.Agent);
            if (n == "docker desktop.exe" || n.StartsWith("com.docker.")) return new Cls("Docker Desktop", "Docker Desktop", Role.Agent);
            if (n == "docker-agent.exe") return new Cls("Docker Desktop", "Docker AI agent", Role.Agent);
            if (n == "cursor.exe") return new Cls("Cursor", "Cursor", Role.Agent);
            if (n == "llama-server.exe" || n == "llama-cli.exe") return new Cls("llama.cpp", "llama.cpp server", Role.Agent);
            if (pa.Contains("\\google\\gemini\\")) return new Cls("Gemini", "Gemini app", Role.Agent);
            if (n == "antigravity.exe") return new Cls("Antigravity", "Antigravity IDE", Role.Agent);
            if (n == "github.exe" && pa.Contains("copilot")) return new Cls("GitHub Copilot", "GitHub Copilot app", Role.Agent);
            if (n == "warp.exe") return new Cls("Warp", "Warp terminal (AI)", Role.Agent);
            if (n == "node.exe" || n == "bun.exe" || n == "deno.exe" || n == "npx.exe")
            {
                if (c.Contains("claude-code") || c.Contains("@anthropic-ai")) return new Cls("Claude Code", "Claude Code CLI (node)", Role.Agent);
                if (c.Contains("opencode")) return new Cls("OpenCode", "OpenCode (node)", Role.Agent);
                if (Regex.IsMatch(c, @"mcp|modelcontextprotocol")) return new Cls("MCP servers", "MCP server", Role.Agent);
                if (c.Contains("codex")) return new Cls("Codex", "Codex (node)", Role.Agent);
                if (c.Contains("gemini")) return new Cls("Gemini CLI", "Gemini CLI", Role.Agent);
            }
            if (n.StartsWith("python") || n == "uv.exe")
            {
                if (Regex.IsMatch(c, @"mcp|modelcontextprotocol")) return new Cls("MCP servers", "MCP server (python)", Role.Agent);
                if (c.Contains("invokeai")) return new Cls("InvokeAI", "InvokeAI", Role.Agent);
                if (c.Contains("comfy")) return new Cls("ComfyUI", "ComfyUI", Role.Agent);
                if (c.Contains("open-webui") || c.Contains("open_webui")) return new Cls("Open WebUI", "Open WebUI", Role.Agent);
            }
            return null;
        }
    }

    static class Redactor
    {
        static readonly Regex R1 = new Regex(@"(?i)((?:--?|/)[\w.-]*(?:key|token|secret|passw(?:or)?d|pwd|auth|bearer|cookie|credential)[\w.-]*(?:[=:]|\s+))(""[^""]*""|'[^']*'|\S+)");
        static readonly Regex R2 = new Regex(@"(?i)(\b[\w.-]*(?:key|token|secret|passw(?:or)?d|pwd|credential)[\w.-]*\s*[=:]\s*)(""[^""]*""|\S+)");
        static readonly Regex R3 = new Regex(@"(?i)(bearer\s+)\S+");
        static readonly Regex R4 = new Regex(@"\b(sk-[\w-]{12,}|xai-[\w-]{12,}|gh[pousr]_\w{20,}|github_pat_\w{20,}|AKIA[0-9A-Z]{16}|eyJ[\w-]{10,}\.[\w-]{10,}\.[\w-]{5,})");
        static readonly Regex R5 = new Regex(@"(?i)(\w+://[^\s:/@]+:)[^\s@/]+@");
        static readonly Regex R6 = new Regex(@"[A-Za-z0-9+_-]{40,}={0,2}");
        public static string Clean(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = R1.Replace(s, "$1***");
            s = R2.Replace(s, "$1***");
            s = R3.Replace(s, "$1***");
            s = R4.Replace(s, "***");
            s = R5.Replace(s, "$1***@");
            s = R6.Replace(s, "***");
            s = s.Replace("\r", " ").Replace("\n", " ");
            if (s.Length > max) s = s.Substring(0, max) + "...";
            return s;
        }
    }

    class Logger
    {
        public static string Dir = @"D:\DEBUGGING LOGS\AgentWatch";
        static readonly object L = new object();
        public static string EventsPath { get { return Path.Combine(Dir, "events.log"); } }
        public static void Event(string line) { Write("events.log", line); }
        public static void Error(string line) { Write("errors.log", line); }
        static void Write(string file, string line)
        {
            lock (L)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    string p = Path.Combine(Dir, file);
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                    {
                        string old = Path.Combine(Dir, Path.GetFileNameWithoutExtension(file) + ".1.log");
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(p, old);
                    }
                    File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ET | " + line + Environment.NewLine, Encoding.UTF8);
                }
                catch { }
            }
        }
        public static void WriteText(string file, string text)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, file), text, Encoding.UTF8); } catch { }
        }
    }

    class Expl
    {
        public string Id, App, Friendly, Risk, What, Why, Todo, Category; public bool Agent;
        public Regex Name, PathRx, Cmd, Parent;
        public Level BaseLevel { get { return Risk == "yellow" || Risk == "screen" ? Level.Yellow : Level.Green; } }
    }

    class Explainer
    {
        public static string FilePath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "explanations.json");
        public List<Expl> Rules = new List<Expl>();
        public int Version = 0;
        public HashSet<string> ShowCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent", "mcp", "screen-control" };
        public string LoadError = null;
        DateTime mtime = DateTime.MinValue;

        public void CheckReload()
        {
            try
            {
                var fi = new FileInfo(FilePath);
                if (!fi.Exists) { if (LoadError == null) LoadError = "explanations.json not found"; return; }
                if (fi.LastWriteTimeUtc == mtime) return;
                mtime = fi.LastWriteTimeUtc;
                Load();
            }
            catch (Exception ex) { LoadError = ex.Message; }
        }

        static string Str(Dictionary<string, object> d, string k)
        {
            object o; if (d.TryGetValue(k, out o) && o != null) return Convert.ToString(o); return null;
        }
        static Regex Rx(string s) { return string.IsNullOrEmpty(s) ? null : new Regex(s, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }

        void Load()
        {
            try
            {
                string text = File.ReadAllText(FilePath, Encoding.UTF8);
                var js = new JavaScriptSerializer(); js.MaxJsonLength = 16 * 1024 * 1024;
                var root = js.DeserializeObject(text) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("rules")) throw new Exception("missing \"rules\" list");
                var arr = root["rules"] as object[];
                if (arr == null) throw new Exception("\"rules\" is not a list");
                var list = new List<Expl>();
                int i = 0;
                foreach (var o in arr)
                {
                    i++;
                    var d = o as Dictionary<string, object>; if (d == null) continue;
                    var e = new Expl();
                    try
                    {
                        e.Id = Str(d, "id") ?? ("rule" + i);
                        e.Name = Rx(Str(d, "name")); e.PathRx = Rx(Str(d, "path")); e.Cmd = Rx(Str(d, "cmd")); e.Parent = Rx(Str(d, "parent"));
                        e.App = Str(d, "app"); e.Friendly = Str(d, "friendly"); e.Risk = (Str(d, "risk") ?? "green").ToLowerInvariant();
                        e.What = Str(d, "what"); e.Why = Str(d, "why"); e.Todo = Str(d, "todo"); e.Category = (Str(d, "category") ?? "").ToLowerInvariant();
                        object ag; e.Agent = d.TryGetValue("agent", out ag) && ag is bool && (bool)ag;
                    }
                    catch (Exception ex) { Logger.Error("explanations.json rule " + i + ": " + ex.Message); continue; }
                    if (e.Name == null && e.PathRx == null && e.Cmd == null && e.Parent == null) continue;
                    list.Add(e);
                }
                var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                object sc; if (root.TryGetValue("showCategories", out sc) && sc is object[]) foreach (var c in (object[])sc) cats.Add(Convert.ToString(c));
                if (cats.Count == 0) { cats.Add("agent"); cats.Add("mcp"); cats.Add("screen-control"); }
                ShowCategories = cats;
                Rules = list; Version++; LoadError = null;
                Logger.Event(string.Format("EXPLAIN | loaded {0} rules from explanations.json", list.Count));
            }
            catch (Exception ex) { LoadError = "explanations.json error: " + ex.Message; Logger.Error(LoadError); }
        }

        public Expl Match(PInfo p)
        {
            string name = p.Name ?? "", path = p.Path ?? "", cmd = p.Cmd ?? "", parent = p.Parent != null ? p.Parent.Name ?? "" : "";
            foreach (var r in Rules)
            {
                if (r.Name != null && !r.Name.IsMatch(name)) continue;
                if (r.PathRx != null && !r.PathRx.IsMatch(path)) continue;
                if (r.Cmd != null && !r.Cmd.IsMatch(cmd)) continue;
                if (r.Parent != null && !r.Parent.IsMatch(parent)) continue;
                return r;
            }
            return null;
        }

        static string Js(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (char ch in s)
            {
                if (ch == '\\') sb.Append("\\\\"); else if (ch == '"') sb.Append("\\\"");
                else if (ch < 32) sb.AppendFormat("\\u{0:x4}", (int)ch); else sb.Append(ch);
            }
            return sb.Append('"').ToString();
        }

        public string AddStub(PInfo p, string app)
        {
            string text = File.ReadAllText(FilePath, Encoding.UTF8);
            int close = text.LastIndexOf(']');
            if (close < 0) throw new Exception("could not find the end of the rules list");
            File.Copy(FilePath, FilePath + ".bak", true);
            string id = "note-" + Regex.Replace((p.Name ?? "proc").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string nameRx = "^" + Regex.Escape((p.Name ?? "").ToLowerInvariant()) + "$";
            var sb = new StringBuilder();
            sb.Append("    {\r\n");
            sb.Append("      \"id\": ").Append(Js(id)).Append(",\r\n");
            sb.Append("      \"name\": ").Append(Js(nameRx)).Append(",\r\n");
            sb.Append("      \"app\": ").Append(Js(app)).Append(",\r\n");
            sb.Append("      \"friendly\": ").Append(Js(p.Name)).Append(",\r\n");
            sb.Append("      \"category\": \"agent\",\r\n");
            sb.Append("      \"risk\": \"green\",\r\n");
            sb.Append("      \"what\": \"TODO: describe what this does\",\r\n");
            sb.Append("      \"why\": \"TODO: why it runs\",\r\n");
            sb.Append("      \"todo\": \"TODO: what to do about it\",\r\n");
            sb.Append("      \"note\": ").Append(Js(string.Format("Added from Agent Watch {0} ET. Seen at {1}, parent {2}", DateTime.Now.ToString("yyyy-MM-dd HH:mm"), p.Path ?? "(path hidden)", p.Parent != null ? p.Parent.Name : "?"))).Append("\r\n");
            sb.Append("    }");
            string before = text.Substring(0, close).TrimEnd();
            bool empty = before.EndsWith("[");
            string result = before + (empty ? "\r\n" : ",\r\n") + sb.ToString() + "\r\n  " + text.Substring(close);
            // sanity check before writing
            new JavaScriptSerializer().DeserializeObject(result);
            File.WriteAllText(FilePath, result, new UTF8Encoding(false));
            Logger.Event("EXPLAIN | added stub rule " + id + " for " + p.Name);
            return id;
        }
    }

    class Episode
    {
        public DateTime Start, End; public bool Open; public string App, Agent, Proc, Trigger, Detail = ""; public int Pid;
        static string Clean(string x) { return (x ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
        public string ToLine()
        {
            return string.Join("\t", new[] { Start.ToString("yyyy-MM-dd HH:mm:ss"), End.ToString("yyyy-MM-dd HH:mm:ss"), Clean(App), Clean(Agent), Clean(Proc), Pid.ToString(), Clean(Trigger), Clean(Detail) });
        }
        public static Episode FromLine(string l)
        {
            var f = l.Split('\t'); if (f.Length < 8) return null;
            var e = new Episode();
            DateTime.TryParse(f[0], out e.Start); DateTime.TryParse(f[1], out e.End);
            e.App = f[2]; e.Agent = f[3]; e.Proc = f[4]; int.TryParse(f[5], out e.Pid); e.Trigger = f[6]; e.Detail = f[7];
            return e;
        }
    }

    static class LogPeek
    {
        static readonly Regex Ansi = new Regex(@"\x1b\[[0-9;]*m");
        static readonly Regex TraceTurn = new Regex("\"at\":\"([^\"]+)\",\"kind\":\"turn\",\"name\":\"([^\"]*)\",\"durationMs\":(\\d+),\"outcome\":\"([^\"]*)\"");
        public static List<string> Tail(string path, int bytes)
        {
            var lines = new List<string>();
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0, fs.Length - bytes); fs.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[fs.Length - start]; int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
                    var text = Encoding.UTF8.GetString(buf, 0, n);
                    foreach (var l in text.Split('\n')) lines.Add(l.TrimEnd('\r'));
                    if (start > 0 && lines.Count > 0) lines.RemoveAt(0);
                }
            }
            catch { }
            return lines;
        }

        // What OpenBot was doing between start and end (local times)
        public static string OpenBot(DateTime start, DateTime end)
        {
            var parts = new List<string>();
            string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            DateTime s0 = start.AddSeconds(-10).ToUniversalTime(), e0 = end.AddSeconds(10).ToUniversalTime();
            int count = 0; string last = null;
            foreach (var raw in Tail(Path.Combine(app, @"OpenBot\logs\remote\cua-driver.log"), 128 * 1024))
            {
                var l = Ansi.Replace(raw, "").Trim(); if (l.Length < 28) continue;
                DateTime t; if (!DateTime.TryParse(l.Substring(0, 27), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out t)) continue;
                if (t < s0 || t > e0) continue;
                count++; last = l.Substring(Math.Min(l.Length, 28)).Trim();
            }
            if (count > 0)
            {
                if (last != null && last.Length > 140) last = last.Substring(0, 140) + "...";
                parts.Add(string.Format("cua-driver logged {0} screen/window event{1} (last: {2})", count, count == 1 ? "" : "s", last));
            }
            foreach (var l in Tail(Path.Combine(app, @"OpenBot\logs\trace.ndjson"), 256 * 1024))
            {
                var m = TraceTurn.Match(l); if (!m.Success) continue;
                DateTime at; if (!DateTime.TryParse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out at)) continue;
                double dur = double.Parse(m.Groups[3].Value) / 1000.0;
                DateTime from = at.AddSeconds(-dur);
                if (at < s0 || from > e0) continue;
                parts.Add(string.Format("OpenBot chat turn {0} at {1:h:mm:ss tt} ET after {2:0}s", m.Groups[4].Value, at.ToLocalTime(), dur));
            }
            return string.Join("; ", parts);
        }
    }

    class Engine
    {
        Dictionary<int, PInfo> cache = new Dictionary<int, PInfo>();
        Dictionary<string, PInfo> notable = new Dictionary<string, PInfo>();
        bool first = true;
        int selfPid = Process.GetCurrentProcess().Id;
        int cores = Environment.ProcessorCount;
        string cuaLog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"OpenBot\logs\remote\cua-driver.log");
        int tickNo = 0;
        IntPtr sbuf = IntPtr.Zero; int sbufLen = 0;
        long lastStamp = 0;

        public Explainer X = new Explainer();
        public List<PInfo> Roots = new List<PInfo>();
        public List<PInfo> Controllers = new List<PInfo>();
        public List<string> RemoteSessions = new List<string>();
        public int ProcessCount;
        public bool OpenBotScanning;
        public DateTime CuaLogWrite = DateTime.MinValue;
        public List<PInfo> NewControllers = new List<PInfo>();
        public List<PInfo> NewlyActive = new List<PInfo>();
        public bool FirstTick { get { return first; } }
        public long SelfCpu100ns, SelfPrivBytes;
        public List<Episode> History = new List<Episode>();
        public Dictionary<string, Episode> OpenEpisodes = new Dictionary<string, Episode>(StringComparer.OrdinalIgnoreCase);
        public List<Episode> NewEpisodes = new List<Episode>();
        string epFile = Path.Combine(Logger.Dir, "screen-episodes.tsv");
        List<PInfo> newProcs = new List<PInfo>();

        public void LoadHistory()
        {
            try
            {
                if (!File.Exists(epFile)) return;
                var lines = File.ReadAllLines(epFile);
                for (int i = Math.Max(0, lines.Length - 100); i < lines.Length; i++) { var e = Episode.FromLine(lines[i]); if (e != null) History.Insert(0, e); }
            }
            catch { }
        }

        // the agent that owns a controller, e.g. "OpenBot agent: Gemini (Antigravity)"
        public static string AgentOf(PInfo p)
        {
            int g = 0;
            for (var a = p.Parent; a != null && g < 8; a = a.Parent, g++)
            {
                if (a.E != null && a.E.Category == "agent" && a.E.Id != "agent-shell") return a.E.Friendly ?? a.Name;
                if (a.E == null && a.C != null && a.C.Role == Role.Agent) return a.C.Label;
            }
            return AppOf(p);
        }

        struct Raw { public int Pid, Ppid; public string Name; public long Create, Cpu, Priv; }

        List<Raw> Snapshot()
        {
            var list = new List<Raw>(600);
            if (sbuf == IntPtr.Zero) { sbufLen = 2 << 20; sbuf = Marshal.AllocHGlobal(sbufLen); }
            int ret, st = 0;
            for (int i = 0; i < 4; i++)
            {
                st = Native.NtQuerySystemInformation(5, sbuf, sbufLen, out ret);
                if (st == unchecked((int)0xC0000004))
                {
                    Marshal.FreeHGlobal(sbuf); sbufLen = Math.Max(ret + (256 << 10), sbufLen * 2); sbuf = Marshal.AllocHGlobal(sbufLen); continue;
                }
                break;
            }
            if (st != 0) return list;
            long off = 0; long b = sbuf.ToInt64();
            while (true)
            {
                var e = new IntPtr(b + off);
                int next = Marshal.ReadInt32(e, 0);
                var r = new Raw();
                r.Priv = Marshal.ReadInt64(e, 8);
                r.Create = Marshal.ReadInt64(e, 32);
                r.Cpu = Marshal.ReadInt64(e, 40) + Marshal.ReadInt64(e, 48);
                int nlen = (ushort)Marshal.ReadInt16(e, 56);
                IntPtr nbuf = Marshal.ReadIntPtr(e, 64);
                r.Pid = (int)Marshal.ReadInt64(e, 80);
                r.Ppid = (int)Marshal.ReadInt64(e, 88);
                r.Name = (nlen > 0 && nbuf != IntPtr.Zero) ? Marshal.PtrToStringUni(nbuf, nlen / 2) : (r.Pid == 0 ? "System Idle Process" : "System");
                list.Add(r);
                if (next == 0) break;
                off += next;
            }
            return list;
        }

        public void Tick()
        {
            tickNo++;
            NewControllers.Clear(); NewlyActive.Clear(); NewEpisodes.Clear(); newProcs.Clear();
            if (tickNo % 4 == 1 || X.Version == 0) X.CheckReload();
            long stamp = DateTime.UtcNow.Ticks;
            double dt = lastStamp > 0 ? (stamp - lastStamp) : 0; lastStamp = stamp;
            var raws = Snapshot();
            if (raws.Count == 0) return;
            var alive = new Dictionary<int, PInfo>(raws.Count);
            foreach (var r in raws)
            {
                if (r.Pid == selfPid) { SelfCpu100ns = r.Cpu; SelfPrivBytes = r.Priv; continue; }
                PInfo p;
                if (cache.TryGetValue(r.Pid, out p) && p.Create == r.Create && p.Ppid == r.Ppid) { }
                else { p = Load(r); if (!first) newProcs.Add(p); }
                if (dt > 0 && p.LastStamp > 0) p.Cpu = Math.Max(0, (r.Cpu - p.LastCpu) * 100.0 / (dt * cores));
                p.LastCpu = r.Cpu; p.LastStamp = stamp; p.RamMB = r.Priv / 1048576.0;
                alive[r.Pid] = p;
            }
            cache = alive;
            ProcessCount = alive.Count;

            foreach (var p in alive.Values) { p.Kids.Clear(); p.Parent = null; p.MatchedParent = null; p.Root = null; }
            foreach (var p in alive.Values)
            {
                PInfo par;
                if (p.Ppid != p.Pid && p.Pid > 4 && alive.TryGetValue(p.Ppid, out par) && (par.Create == 0 || p.Create == 0 || par.Create <= p.Create))
                { p.Parent = par; par.Kids.Add(p); }
            }
            // explanations (only for new processes or after the file changed)
            foreach (var p in alive.Values) if (p.EVer != X.Version) Explain(p);

            BuildRoots(alive);

            // fetch command lines for unexplained helpers inside agent trees (once each), then re-explain
            int budget = 60;
            foreach (var p in alive.Values)
            {
                if (budget <= 0) break;
                if (p.Root != null && p.Cmd == null && !p.CmdTried)
                {
                    budget--; p.CmdTried = true;
                    IntPtr h = Native.OpenProcess(Native.QUERY_LIMITED, false, p.Pid);
                    if (h != IntPtr.Zero) { try { p.Cmd = Native.GetCmd(h); } finally { Native.CloseHandle(h); } }
                    if (p.Cmd != null) { p.C0 = Classifier.Classify(p); Explain(p); }
                }
            }
            if (budget < 60) BuildRoots(alive);

            try { var fi = new FileInfo(cuaLog); if (fi.Exists) CuaLogWrite = fi.LastWriteTime; } catch { }
            OpenBotScanning = (DateTime.Now - CuaLogWrite).TotalSeconds < 30;

            Controllers = new List<PInfo>();
            foreach (var p in alive.Values)
                if (p.C != null && p.C.Role == Role.Controller) Controllers.Add(p);
            Controllers.Sort((a, c) => a.Pid.CompareTo(c.Pid));
            foreach (var p in Controllers)
            {
                bool openBotLog = OpenBotScanning && p.Name.Equals("cua-driver.exe", StringComparison.OrdinalIgnoreCase) && AppOf(p) == "OpenBot";
                string reason = null;
                if (p.Cpu >= 0.2) reason = string.Format("CPU {0:0.0}%", p.Cpu);
                else if (openBotLog) reason = "cua-driver.log writing (scanning windows) at " + CuaLogWrite.ToString("h:mm:ss tt");
                foreach (var np in newProcs)
                {
                    int gg = 0;
                    for (var a = np.Parent; a != null && gg < 3; a = a.Parent, gg++) if (a == p) { reason = "started " + np.Name + " (PID " + np.Pid + ")"; break; }
                    if (reason != null && reason.StartsWith("started")) break;
                }
                if (reason != null) { p.LastActive = DateTime.Now; if (!p.Active || p.ActiveReason == "") p.ActiveReason = reason; }
                bool nowActive = (DateTime.Now - p.LastActive).TotalSeconds < 30;
                if (nowActive && !p.Active && !first)
                {
                    NewlyActive.Add(p);
                    Logger.Event(string.Format("ACTIVE  | CONTROLLER | {0} > {1} | PID {2} | CPU {3:0.0}%{4}", AppOf(p), p.Name, p.Pid, p.Cpu, openBotLog ? " | cua-driver.log is being written (window scanning)" : ""));
                }
                else if (!nowActive && p.Active)
                    Logger.Event(string.Format("IDLE    | CONTROLLER | {0} > {1} | PID {2}", AppOf(p), p.Name, p.Pid));
                if (!nowActive) p.ActiveReason = "";
                p.Active = nowActive;
            }
            UpdateEpisodes();
            foreach (var p in alive.Values) p.Lv = LevelOf(p);

            if (tickNo % 4 == 1) RemoteSessions = GetRemoteSessions();

            var now = new Dictionary<string, PInfo>();
            foreach (var p in alive.Values)
            {
                if (p.C == null) continue;
                bool isNotable = p.Root == p || p.C.Role == Role.Controller || p.MatchedParent == null || p.MatchedParent.C.Label != p.C.Label;
                if (isNotable) now[p.Key] = p;
            }
            foreach (var kv in now)
                if (!notable.ContainsKey(kv.Key))
                {
                    LogProc(first ? "PRESENT" : "START  ", kv.Value);
                    if (!first && kv.Value.C.Role == Role.Controller) NewControllers.Add(kv.Value);
                }
            foreach (var kv in notable)
                if (!now.ContainsKey(kv.Key))
                {
                    var p = kv.Value;
                    string dur = p.Create > 0 ? FormatDur(DateTime.Now - p.StartLocal) : "?";
                    Logger.Event(string.Format("STOP    | {0} | {1} > {2} | PID {3} | ran {4}", RoleText(p), AppOf(p), p.Name, p.Pid, dur));
                }
            notable = now;
            first = false;
        }

        void UpdateEpisodes()
        {
            var activeByApp = new Dictionary<string, List<PInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Controllers) if (p.Active) { string a = AppOf(p); List<PInfo> l; if (!activeByApp.TryGetValue(a, out l)) { l = new List<PInfo>(); activeByApp[a] = l; } l.Add(p); }
            foreach (var kv in activeByApp)
            {
                Episode e;
                if (!OpenEpisodes.TryGetValue(kv.Key, out e))
                {
                    var p = kv.Value[0];
                    e = new Episode { Start = p.LastActive, End = p.LastActive, Open = true, App = kv.Key, Agent = AgentOf(p), Proc = (p.E != null && p.E.Friendly != null ? p.E.Friendly : p.Name) + " (" + p.Name + ")", Pid = p.Pid, Trigger = p.ActiveReason };
                    OpenEpisodes[kv.Key] = e; NewEpisodes.Add(e); History.Insert(0, e);
                    Logger.Event(string.Format("SCREEN  | START | {0} | agent: {1} | {2} PID {3} | {4}", e.App, e.Agent, e.Proc, e.Pid, e.Trigger));
                }
                foreach (var p in kv.Value)
                {
                    if (p.LastActive > e.End) e.End = p.LastActive;
                    string ag = AgentOf(p); if (!e.Agent.Contains(ag)) e.Agent += " + " + ag;
                }
            }
            var closed = new List<string>();
            foreach (var kv in OpenEpisodes) if (!activeByApp.ContainsKey(kv.Key)) closed.Add(kv.Key);
            foreach (var k in closed)
            {
                var e = OpenEpisodes[k]; OpenEpisodes.Remove(k); e.Open = false;
                try { if (e.App == "OpenBot") e.Detail = LogPeek.OpenBot(e.Start, e.End); } catch { }
                Logger.Event(string.Format("SCREEN  | END   | {0} | agent: {1} | {2:h:mm:ss tt} - {3:h:mm:ss tt} ET | {4}", e.App, e.Agent, e.Start, e.End, e.Detail));
                try { Directory.CreateDirectory(Logger.Dir); File.AppendAllText(epFile, e.ToLine() + Environment.NewLine, Encoding.UTF8); } catch { }
            }
            while (History.Count > 100) History.RemoveAt(History.Count - 1);
        }

        void Explain(PInfo p)
        {
            p.E = X.Match(p); p.EVer = X.Version;
            var c = p.C0; var e = p.E;
            if (e != null)
            {
                if (e.Risk == "screen") c = new Cls(e.App ?? (c != null ? c.App : null), e.Friendly ?? p.Name, (c != null && c.Role == Role.Harmless) ? Role.Harmless : Role.Controller);
                else if (c != null && c.Role == Role.Controller) c = new Cls(c.App ?? e.App, e.Friendly ?? c.Label, Role.Agent);
                else if (c == null && e.Agent) c = new Cls(e.App, e.Friendly ?? p.Name, Role.Agent);
                else if (c != null && e.Friendly != null) c = new Cls(c.App ?? e.App, e.Friendly, c.Role);
            }
            p.C = c;
        }

        void BuildRoots(Dictionary<int, PInfo> alive)
        {
            foreach (var p in alive.Values) { p.MatchedParent = null; p.Root = null; }
            Roots = new List<PInfo>();
            foreach (var p in alive.Values)
            {
                if (p.C == null) continue;
                PInfo top = p; int guard = 0;
                for (var a = p.Parent; a != null && guard < 64; a = a.Parent, guard++)
                    if (a.C != null) { if (p.MatchedParent == null) p.MatchedParent = a; top = a; }
                p.Root = top;
                if (top == p) Roots.Add(p);
            }
            foreach (var r in Roots) MarkRoot(r, r, 0);
        }

        public static Level LevelOf(PInfo p)
        {
            if (p.C != null && p.C.Role == Role.Controller) return p.Active ? Level.Red : Level.Yellow;
            if (p.C != null && p.C.Role == Role.Harmless) return Level.Green;
            if (p.E != null) return p.E.BaseLevel;
            return p.C != null ? Level.Green : Level.None;
        }

        void MarkRoot(PInfo p, PInfo root, int depth)
        {
            if (depth > 40) return;
            foreach (var k in p.Kids) { if (k.Root == null) k.Root = root; MarkRoot(k, root, depth + 1); }
        }

        public static string AppOf(PInfo p)
        {
            var r = p.Root ?? p;
            if (r.C != null && r.C.App != null) return r.C.App;
            if (p.C != null && p.C.App != null) return p.C.App;
            if (r.C != null && r.C.Role == Role.Controller) return "Screen control helpers";
            return r.Name;
        }

        public static string RoleText(PInfo p)
        {
            if (p.C == null) return "child     ";
            switch (p.C.Role)
            {
                case Role.Controller: return "CONTROLLER";
                case Role.Harmless: return "HARMLESS  ";
                default: return "AGENT     ";
            }
        }

        static string FormatDur(TimeSpan t)
        {
            if (t.TotalDays >= 1) return string.Format("{0}d {1}h", (int)t.TotalDays, t.Hours);
            if (t.TotalHours >= 1) return string.Format("{0}h {1}m", (int)t.TotalHours, t.Minutes);
            if (t.TotalMinutes >= 1) return string.Format("{0}m {1}s", (int)t.TotalMinutes, t.Seconds);
            return string.Format("{0}s", (int)t.TotalSeconds);
        }

        void LogProc(string kind, PInfo p)
        {
            var chain = new StringBuilder(); int g = 0;
            for (var a = p.Parent; a != null && g < 6; a = a.Parent, g++)
            {
                if (chain.Length > 0) chain.Append(" < ");
                chain.Append(a.Name).Append("(").Append(a.Pid).Append(")");
            }
            if (p.Parent == null) chain.Append("(parent " + p.Ppid + " gone)");
            Logger.Event(string.Format("{0} | {1} | {2} > {3} [{4}] | PID {5} | started {6} | parents: {7} | {8} | cmd: {9}",
                kind, RoleText(p), AppOf(p), p.Name, p.C.Label, p.Pid,
                p.Create > 0 ? p.StartLocal.ToString("yyyy-MM-dd HH:mm:ss") : "?", chain, p.Path ?? "(path hidden)", Redactor.Clean(p.Cmd, 400)));
        }

        PInfo Load(Raw r)
        {
            var p = new PInfo(); p.Pid = r.Pid; p.Ppid = r.Ppid; p.Name = r.Name; p.Create = r.Create;
            IntPtr h = r.Pid > 4 ? Native.OpenProcess(Native.QUERY_LIMITED, false, r.Pid) : IntPtr.Zero;
            try
            {
                if (h != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024); int sz = sb.Capacity;
                    if (Native.QueryFullProcessImageNameW(h, 0, sb, ref sz)) p.Path = sb.ToString();
                }
                if (Classifier.NeedsCmd(p.Name) && h != IntPtr.Zero) { p.Cmd = Native.GetCmd(h); p.CmdTried = true; }
                p.C0 = Classifier.Classify(p);
                if (p.C0 != null && p.Cmd == null && h != IntPtr.Zero) { p.Cmd = Native.GetCmd(h); p.CmdTried = true; p.C0 = Classifier.Classify(p); }
            }
            catch (Exception ex) { Logger.Error("load " + r.Pid + ": " + ex.Message); }
            finally { if (h != IntPtr.Zero) Native.CloseHandle(h); }
            return p;
        }

        static List<string> GetRemoteSessions()
        {
            var list = new List<string>();
            IntPtr info; int count;
            try
            {
                if (!Native.WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out info, out count)) return list;
                int sz = Marshal.SizeOf(typeof(Native.WTS_SESSION_INFO));
                for (int i = 0; i < count; i++)
                {
                    var s = (Native.WTS_SESSION_INFO)Marshal.PtrToStructure(new IntPtr(info.ToInt64() + i * sz), typeof(Native.WTS_SESSION_INFO));
                    if (s.State == 0 && s.WinStationName != null && s.WinStationName.StartsWith("RDP-Tcp", StringComparison.OrdinalIgnoreCase))
                        list.Add(string.Format("Incoming Remote Desktop session {0} ({1})", s.SessionId, s.WinStationName));
                }
                Native.WTSFreeMemory(info);
            }
            catch { }
            return list;
        }
    }
    static class Settings
    {
        static string FilePath = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "settings.json");
        public static bool WindowOpen = true, ShowModelServers = false;
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (d == null) return;
                object o;
                if (d.TryGetValue("windowOpen", out o) && o is bool) WindowOpen = (bool)o;
                if (d.TryGetValue("showModelServers", out o) && o is bool) ShowModelServers = (bool)o;
            }
            catch { }
        }
        public static void Save()
        {
            try { File.WriteAllText(FilePath, string.Format("{{\r\n  \"windowOpen\": {0},\r\n  \"showModelServers\": {1}\r\n}}\r\n", WindowOpen ? "true" : "false", ShowModelServers ? "true" : "false")); } catch { }
        }
    }

    class DBListView : ListView
    {
        public DBListView() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    }

    class Row
    {
        public string Key; public string[] T; public Color Back, Fore; public bool Bold; public PInfo P; public GroupInfo G;
        public int PillCol = -1; public Level PillLevel; public string PillText; public Episode Ep;
    }

    class GroupInfo
    {
        public string App; public List<PInfo> Roots = new List<PInfo>(); public List<PInfo> Procs = new List<PInfo>();
        public Level Lv = Level.Green; public string Sentence; public double Cpu, Ram;
        public List<PInfo> ActiveCtrls = new List<PInfo>(), IdleCtrls = new List<PInfo>(), Yellows = new List<PInfo>();
        public bool Harmless, Infra; public string Category = "agent", Doing = "Idle";
    }

    class MainForm : Form
    {
        static readonly Color C_BG = Color.FromArgb(30, 27, 75);
        static readonly Color C_PANEL = Color.FromArgb(49, 46, 129);
        static readonly Color C_PANEL2 = Color.FromArgb(46, 40, 112);
        static readonly Color C_LIST = Color.FromArgb(37, 35, 92);
        static readonly Color C_LIST2 = Color.FromArgb(42, 39, 104);
        static readonly Color C_HEAD = Color.FromArgb(67, 56, 202);
        static readonly Color C_GROUP = Color.FromArgb(76, 29, 149);
        static readonly Color C_ROOT = Color.FromArgb(55, 48, 163);
        static readonly Color C_CHILD = Color.FromArgb(37, 35, 92);
        static readonly Color C_REDROW = Color.FromArgb(136, 19, 75);
        static readonly Color C_SEL = Color.FromArgb(124, 58, 237);
        static readonly Color C_TEXT = Color.FromArgb(224, 231, 255);
        static readonly Color C_DIM = Color.FromArgb(165, 180, 252);
        static readonly Color C_BTN = Color.FromArgb(79, 70, 229);
        static readonly Color P_RED = Color.FromArgb(244, 63, 94), P_YEL = Color.FromArgb(251, 191, 36), P_GRN = Color.FromArgb(52, 211, 153), P_UNK = Color.FromArgb(148, 163, 184);
        static readonly HashSet<string> InfraApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WSL graphics (WSLg)", "cloudflared tunnel", "Docker Desktop" };

        Engine eng = new Engine();
        NotifyIcon tray = new NotifyIcon();
        System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer();
        DBListView lv = new DBListView(), lvSum = new DBListView(), lvHist = new DBListView();
        Label banner = new Label(); CheckBox chkModels = new CheckBox(); int histSig = -1;
        Label title = new Label(), headline = new Label(), legend = new Label(), status = new Label();
        Label exTitle = new Label(), exBadge = new Label();
        TextBox exText = new TextBox();
        Button bAddNote, bKill;
        CheckBox chkDetails = new CheckBox(), chkKids = new CheckBox();
        Icon icIdle, icGreen, icYellow, icRed, icRedBright, icApp;
        bool exiting = false, blinkOn = false;
        DateTime lastBalloon = DateTime.MinValue;
        Font fNormal, fBold, fHead, fHeadline, fPill;
        float S = 1f;
        long selfLastCpu = 0; DateTime selfLastAt = DateTime.Now; double selfCpu = 0; DateTime lastStatusWrite = DateTime.MinValue;
        double selfCpuLong = -1; long selfLongCpu = -1; DateTime selfLongAt = DateTime.Now;
        string state = ""; string phase = ""; double tickMs = 0, totalMs = 0; long refreshCount = 0; string lastTip = "";
        List<GroupInfo> groups = new List<GroupInfo>(), allGroups = new List<GroupInfo>();
        string headlineText = ""; ListView lastSelList;
        string exKey = null; Episode exEpisode; PInfo noteTarget; string noteApp;

        int Sc(int v) { return (int)(v * S); }
        bool IsShown { get { return IsHandleCreated && Native.IsWindowVisible(Handle); } }
        void HideMain() { Hide(); Settings.WindowOpen = false; Settings.Save(); }

        public MainForm()
        {
            using (var g = CreateGraphics()) S = g.DpiX / 96f;
            fNormal = new Font("Segoe UI", 9f); fBold = new Font("Segoe UI Semibold", 9f, FontStyle.Bold);
            fHead = new Font("Segoe UI Semibold", 12f, FontStyle.Bold); fHeadline = new Font("Segoe UI Semibold", 11f, FontStyle.Bold); fPill = new Font("Segoe UI", 8f, FontStyle.Bold);
            icIdle = MakeIcon(Color.FromArgb(99, 102, 241), Color.FromArgb(199, 210, 254), false);
            icGreen = MakeIcon(Color.FromArgb(16, 185, 129), Color.FromArgb(209, 250, 229), false);
            icYellow = MakeIcon(Color.FromArgb(245, 158, 11), Color.FromArgb(254, 243, 199), false);
            icRed = MakeIcon(Color.FromArgb(220, 38, 38), Color.FromArgb(254, 202, 202), true);
            icRedBright = MakeIcon(Color.FromArgb(255, 230, 0), Color.FromArgb(255, 60, 60), true);
            try { icApp = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { icApp = icIdle; }

            Text = "Agent Watch"; Icon = icApp; BackColor = C_BG; ForeColor = C_TEXT; Font = fNormal;
            Size = new Size(Sc(1260), Sc(660)); StartPosition = FormStartPosition.CenterScreen; MinimumSize = new Size(Sc(820), Sc(380));

            // header
            var top = new Panel { Dock = DockStyle.Top, Height = Sc(84), BackColor = C_PANEL };
            title.Text = "Agent Watch"; title.Font = fHead; title.ForeColor = Color.White; title.AutoSize = true; title.Location = new Point(Sc(12), Sc(6));
            headline.AutoSize = false; headline.Location = new Point(Sc(13), Sc(32)); headline.Size = new Size(Sc(1200), Sc(24)); headline.Font = fHeadline; headline.ForeColor = Color.White;
            headline.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; headline.AutoEllipsis = true;
            legend.AutoSize = false; legend.Location = new Point(Sc(14), Sc(58)); legend.Size = new Size(Sc(1200), Sc(20)); legend.ForeColor = C_DIM; legend.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right; legend.AutoEllipsis = true;
            legend.Text = "RED = controlling or scanning your screen right now    YELLOW = can act for you (agent, tool server, network) or a screen helper that is loaded but idle    GREEN = normal helper";
            top.Controls.Add(title); top.Controls.Add(headline); top.Controls.Add(legend);
            banner.Dock = DockStyle.Top; banner.Height = Sc(40); banner.Font = new Font("Segoe UI Semibold", 12.5f, FontStyle.Bold); banner.TextAlign = ContentAlignment.MiddleLeft;
            banner.Padding = new Padding(Sc(12), 0, Sc(12), 0); banner.BackColor = C_PANEL2; banner.ForeColor = C_DIM; banner.AutoEllipsis = true; banner.Text = "Who's controlling the screen: nobody right now.";
            banner.Cursor = Cursors.Hand; banner.Click += delegate { if (lvHist.Items.Count > 0) { lvHist.Items[0].Selected = true; lvHist.Focus(); } };

            // bottom bar
            var bottom = new Panel { Dock = DockStyle.Bottom, Height = Sc(44), BackColor = C_PANEL };
            var flow = new FlowLayoutPanel { Dock = DockStyle.Left, Width = Sc(1000), BackColor = C_PANEL, Padding = new Padding(Sc(8), Sc(7), 0, 0), WrapContents = false };
            chkDetails.Text = "Show details (every process)"; chkDetails.Checked = false; chkDetails.ForeColor = C_TEXT; chkDetails.AutoSize = true; chkDetails.Margin = new Padding(Sc(4), Sc(6), Sc(10), 0);
            chkDetails.CheckedChanged += delegate { ApplyView(); };
            chkKids.Text = "Include console/launcher helpers"; chkKids.Checked = false; chkKids.ForeColor = C_TEXT; chkKids.AutoSize = true; chkKids.Margin = new Padding(Sc(4), Sc(6), Sc(10), 0);
            chkKids.CheckedChanged += delegate { RefreshNow(); };
            bKill = MakeButton("Kill selected process...", Sc(170)); bKill.Click += delegate { KillSelected(); };
            var bLogs = MakeButton("Open log folder", Sc(125)); bLogs.Click += delegate { OpenLogs(); };
            var bEdit = MakeButton("Edit explanations", Sc(135)); bEdit.Click += delegate { EditExplanations(); };
            chkModels.Text = "Show model servers"; chkModels.Checked = Settings.ShowModelServers; chkModels.ForeColor = C_TEXT; chkModels.AutoSize = true; chkModels.Margin = new Padding(Sc(4), Sc(6), Sc(10), 0);
            chkModels.CheckedChanged += delegate { Settings.ShowModelServers = chkModels.Checked; Settings.Save(); RefreshNow(); };
            flow.Controls.Add(chkDetails); flow.Controls.Add(chkModels); flow.Controls.Add(chkKids); flow.Controls.Add(bKill); flow.Controls.Add(bLogs); flow.Controls.Add(bEdit);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleRight; status.ForeColor = C_DIM; status.Padding = new Padding(0, 0, Sc(10), 0); status.AutoEllipsis = true;
            bottom.Controls.Add(status); bottom.Controls.Add(flow);

            // explanation pane
            var ex = new Panel { Dock = DockStyle.Right, Width = Sc(400), BackColor = C_PANEL2, Padding = new Padding(Sc(12), Sc(10), Sc(12), Sc(10)) };
            exText.Multiline = true; exText.ReadOnly = true; exText.BorderStyle = BorderStyle.None; exText.BackColor = C_PANEL2; exText.ForeColor = C_TEXT;
            exText.ScrollBars = ScrollBars.Vertical; exText.WordWrap = true; exText.Dock = DockStyle.Fill; exText.Font = new Font("Segoe UI", 9.5f);
            exTitle.Dock = DockStyle.Top; exTitle.Height = Sc(30); exTitle.Font = fHeadline; exTitle.ForeColor = Color.White; exTitle.AutoEllipsis = true; exTitle.Text = "What is this?";
            exBadge.Dock = DockStyle.Top; exBadge.Height = Sc(28); exBadge.Font = fBold; exBadge.ForeColor = Color.FromArgb(30, 27, 75); exBadge.TextAlign = ContentAlignment.MiddleLeft; exBadge.Visible = false;
            var gap = new Panel { Dock = DockStyle.Top, Height = Sc(8), BackColor = C_PANEL2 };
            bAddNote = MakeButton("Add note to explanations.json", Sc(376)); bAddNote.Dock = DockStyle.Bottom; bAddNote.Visible = false; bAddNote.Click += delegate { AddNote(); };
            ex.Controls.Add(exText); ex.Controls.Add(bAddNote); ex.Controls.Add(gap); ex.Controls.Add(exBadge); ex.Controls.Add(exTitle);
            exText.Text = "Select a row to see, in plain English, what it is, why it is running, how risky it is, and what to do about it.";

            SetupList(lvSum, Sc(32));
            lvSum.Columns.Add("Risk", Sc(110)); lvSum.Columns.Add("Agent app", Sc(130)); lvSum.Columns.Add("Doing now", Sc(250)); lvSum.Columns.Add("Processes", Sc(76)); lvSum.Columns.Add("CPU %", Sc(58)); lvSum.Columns.Add("RAM MB", Sc(70)); lvSum.Columns.Add("What's happening", Sc(400));
            SetupList(lv, Sc(22));
            lv.Columns.Add("Agent / process", Sc(330)); lv.Columns.Add("PID", Sc(64)); lv.Columns.Add("Started", Sc(120));
            lv.Columns.Add("CPU %", Sc(58)); lv.Columns.Add("RAM MB", Sc(70)); lv.Columns.Add("Risk", Sc(150)); lv.Columns.Add("Path / command line", Sc(330));
            lv.Visible = false;
            SetupList(lvHist, Sc(22));
            lvHist.Columns.Add("Screen-control history: started", Sc(190)); lvHist.Columns.Add("Ended", Sc(90)); lvHist.Columns.Add("Who (agent)", Sc(250)); lvHist.Columns.Add("Process", Sc(220)); lvHist.Columns.Add("What it was doing", Sc(300));
            lvHist.Dock = DockStyle.Bottom; lvHist.Height = Sc(150);
            var histSplit = new Splitter { Dock = DockStyle.Bottom, Height = Sc(4), BackColor = C_HEAD };

            Controls.Add(lv); Controls.Add(lvSum); Controls.Add(histSplit); Controls.Add(lvHist); Controls.Add(ex); Controls.Add(bottom); Controls.Add(banner); Controls.Add(top);

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Agent Watch", null, delegate { ShowMain(); });
            menu.Items.Add("Open log folder", null, delegate { OpenLogs(); });
            menu.Items.Add("Open event log", null, delegate { OpenEvents(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit Agent Watch", null, delegate { exiting = true; tray.Visible = false; Application.Exit(); });
            tray.ContextMenuStrip = menu; tray.Icon = icIdle; tray.Text = "Agent Watch"; tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ToggleMain(); };
            tray.BalloonTipClicked += delegate { ShowMain(); };

            timer.Interval = 2500; timer.Tick += delegate { Refresh2(); };
            blink.Interval = 1000; blink.Tick += delegate { blinkOn = !blinkOn; tray.Icon = blinkOn ? icRedBright : icRed; };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; HideMain(); } };
            Resize += delegate { if (WindowState == FormWindowState.Minimized) { HideMain(); WindowState = FormWindowState.Normal; } };

            Logger.Event("===== Agent Watch started (PID " + Process.GetCurrentProcess().Id + ") =====");
            eng.LoadHistory();
            Refresh2();
            timer.Start();
        }

        void SetupList(DBListView l, int rowH)
        {
            l.Dock = DockStyle.Fill; l.View = View.Details; l.FullRowSelect = true; l.HideSelection = false; l.MultiSelect = false;
            l.BackColor = C_LIST; l.ForeColor = C_TEXT; l.BorderStyle = BorderStyle.None; l.OwnerDraw = true; l.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            var il = new ImageList(); il.ImageSize = new Size(1, Math.Max(16, rowH)); l.SmallImageList = il;
            l.DrawColumnHeader += DrawHeader;
            l.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { };
            l.DrawSubItem += DrawSub;
            l.SelectedIndexChanged += delegate { l.Invalidate(); if (l.SelectedItems.Count > 0) lastSelList = l; UpdateExplanation(); };
            l.Resize += delegate { FitLastColumn(l); };
            l.MouseUp += LvMouseUp;
            l.DoubleClick += delegate { if (l == lvSum) { chkDetails.Checked = true; } };
        }

        Button MakeButton(string text, int w)
        {
            var b = new Button { Text = text, Width = w, Height = Sc(28), FlatStyle = FlatStyle.Flat, BackColor = C_BTN, ForeColor = Color.White, Margin = new Padding(Sc(4), 0, Sc(4), 0), Cursor = Cursors.Hand };
            b.FlatAppearance.BorderColor = Color.FromArgb(129, 140, 248); b.FlatAppearance.MouseOverBackColor = Color.FromArgb(99, 102, 241);
            return b;
        }

        static Icon MakeIcon(Color fill, Color eye, bool warn)
        {
            var bmp = new Bitmap(32, 32);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
                using (var b = new SolidBrush(fill)) g.FillEllipse(b, 1, 1, 30, 30);
                using (var p = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f)) g.DrawEllipse(p, 1.5f, 1.5f, 29, 29);
                using (var b = new SolidBrush(eye)) g.FillEllipse(b, 5, 10, 22, 12);
                using (var b = new SolidBrush(Color.FromArgb(30, 27, 75))) g.FillEllipse(b, 11.5f, 11.5f, 9, 9);
                if (warn) using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, 15f, 13.5f, 3, 3);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }

        void FitLastColumn(ListView l)
        {
            if (l.Columns.Count == 0) return;
            int sum = 0; for (int i = 0; i < l.Columns.Count - 1; i++) sum += l.Columns[i].Width;
            int w = l.ClientSize.Width - sum; if (w < Sc(200)) w = Sc(200);
            if (l.Columns[l.Columns.Count - 1].Width != w) l.Columns[l.Columns.Count - 1].Width = w;
        }

        void DrawHeader(object s, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(C_HEAD)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Color.FromArgb(99, 102, 241))) e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 3);
            var r = new Rectangle(e.Bounds.X + Sc(6), e.Bounds.Y, e.Bounds.Width - Sc(8), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, fBold, r, Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        static Color PillColor(Level l) { return l == Level.Red ? P_RED : l == Level.Yellow ? P_YEL : l == Level.Green ? P_GRN : P_UNK; }

        void DrawSub(object s, DrawListViewSubItemEventArgs e)
        {
            var l = (ListView)s;
            var row = e.Item.Tag as Row;
            Rectangle r = e.Bounds;
            if (e.ColumnIndex == 0) r.Width = l.Columns[0].Width;
            Color back = e.Item.Selected ? C_SEL : (row != null ? row.Back : C_LIST);
            using (var b = new SolidBrush(back)) e.Graphics.FillRectangle(b, r);
            if (row != null && row.PillCol == e.ColumnIndex && row.PillText != null)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var sz = TextRenderer.MeasureText(row.PillText, fPill);
                int h = Math.Min(r.Height - Sc(6), Sc(20)); int w = Math.Min(r.Width - Sc(8), sz.Width + Sc(14));
                var pr = new Rectangle(r.X + Sc(5), r.Y + (r.Height - h) / 2, w, h);
                using (var path = Rounded(pr, h / 2)) using (var b = new SolidBrush(PillColor(row.PillLevel))) e.Graphics.FillPath(b, path);
                TextRenderer.DrawText(e.Graphics, row.PillText, fPill, pr, row.PillLevel == Level.Red ? Color.White : Color.FromArgb(30, 27, 75), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            }
            else
            {
                Color fore = row != null ? row.Fore : C_TEXT;
                var f = row != null && row.Bold ? fBold : fNormal;
                var tr = new Rectangle(r.X + Sc(5), r.Y, r.Width - Sc(7), r.Height);
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                bool numeric = l == lv ? (e.ColumnIndex >= 1 && e.ColumnIndex <= 4) : l == lvSum ? (e.ColumnIndex >= 3 && e.ColumnIndex <= 5) : false;
                if (numeric) flags |= TextFormatFlags.Right;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, f, tr, fore, flags);
            }
            using (var p = new Pen(Color.FromArgb(52, 48, 120))) e.Graphics.DrawLine(p, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = Math.Max(2, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure();
            return p;
        }

        Row SelectedRow()
        {
            var l = chkDetails.Checked ? lv : lvSum;
            if (lastSelList == lvHist && lvHist.SelectedItems.Count > 0) return lvHist.SelectedItems[0].Tag as Row;
            return l.SelectedItems.Count > 0 ? l.SelectedItems[0].Tag as Row : null;
        }

        void LvMouseUp(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var l = (ListView)s;
            var hit = l.HitTest(e.Location);
            if (hit.Item == null) return;
            hit.Item.Selected = true;
            var row = hit.Item.Tag as Row;
            if (row == null || row.P == null) return;
            var m = new ContextMenuStrip();
            m.Items.Add("Kill process " + row.P.Name + " (PID " + row.P.Pid + ")...", null, delegate { KillSelected(); });
            if (!string.IsNullOrEmpty(row.P.Path)) m.Items.Add("Open file location", null, delegate { try { Process.Start("explorer.exe", "/select,\"" + row.P.Path + "\""); } catch { } });
            m.Items.Add("Copy details", null, delegate
            {
                try { Clipboard.SetText(string.Format("{0} PID {1} parent {2}\r\n{3}\r\n{4}", row.P.Name, row.P.Pid, row.P.Ppid, row.P.Path, Redactor.Clean(row.P.Cmd, 4000))); } catch { }
            });
            m.Show(l, e.Location);
        }

        void KillSelected()
        {
            var row = SelectedRow();
            if (row == null || row.P == null)
            {
                MessageBox.Show(this, chkDetails.Checked ? "Select a process row first (not an app heading)." : "Turn on 'Show details' and select the exact process you want to stop.", "Agent Watch", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var p = row.P;
            string msg = string.Format("Kill {0} (PID {1})?\n\n{2}\nApp: {3}\nPath: {4}\n\nThis ends only this one process. Anything unsaved in it is lost, and the app may start it again.",
                p.Name, p.Pid, p.E != null ? p.E.Friendly : "", Engine.AppOf(p), p.Path ?? "(hidden)");
            if (MessageBox.Show(this, msg, "Agent Watch - confirm kill", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                var proc = Process.GetProcessById(p.Pid);
                long c = 0;
                try { c = proc.StartTime.ToUniversalTime().ToFileTimeUtc(); } catch { }
                if (p.Create > 0 && c > 0 && Math.Abs(c - p.Create) > 10000000) { MessageBox.Show(this, "That process already exited (its PID now belongs to something else). Nothing was killed.", "Agent Watch"); return; }
                proc.Kill();
                Logger.Event(string.Format("KILLED  | by user from Agent Watch | {0} > {1} | PID {2} | {3}", Engine.AppOf(p), p.Name, p.Pid, p.Path));
            }
            catch (ArgumentException) { MessageBox.Show(this, "That process has already exited.", "Agent Watch"); }
            catch (Exception ex) { MessageBox.Show(this, "Could not kill it: " + ex.Message, "Agent Watch", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            RefreshNow();
        }

        void AddNote()
        {
            if (noteTarget == null) return;
            try
            {
                string id = eng.X.AddStub(noteTarget, noteApp);
                eng.X.CheckReload();
                if (MessageBox.Show(this, "Added a stub rule (" + id + ") for " + noteTarget.Name + " at the end of explanations.json.\n\nOpen the file now to fill in what it does?", "Agent Watch", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    EditExplanations();
            }
            catch (Exception ex) { MessageBox.Show(this, "Could not add the note: " + ex.Message, "Agent Watch", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            exKey = null; RefreshNow();
        }

        void EditExplanations() { try { Process.Start("notepad.exe", "\"" + Explainer.FilePath + "\""); } catch { } }
        void OpenLogs() { try { Directory.CreateDirectory(Logger.Dir); Process.Start("explorer.exe", "\"" + Logger.Dir + "\""); } catch { } }
        void OpenEvents() { try { if (File.Exists(Logger.EventsPath)) Process.Start("notepad.exe", "\"" + Logger.EventsPath + "\""); else OpenLogs(); } catch { } }

        public void ShowMain()
        {
            Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront(); Native.SetForegroundWindow(Handle);
            if (!Settings.WindowOpen) { Settings.WindowOpen = true; Settings.Save(); }
            RefreshNow();
        }
        void ToggleMain() { if (IsShown) HideMain(); else ShowMain(); }

        void ApplyView()
        {
            lv.Visible = chkDetails.Checked; lvSum.Visible = !chkDetails.Checked;
            chkKids.Enabled = chkDetails.Checked;
            exKey = null; RefreshNow();
        }

        void Balloon(string titleT, string text, ToolTipIcon icon)
        {
            if ((DateTime.Now - lastBalloon).TotalSeconds < 8) return;
            lastBalloon = DateTime.Now;
            tray.ShowBalloonTip(8000, titleT, text, icon);
        }

        void RefreshNow() { BuildGroups(); if (IsShown) { BuildRows(); UpdateExplanation(); } }

        void Refresh2()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                eng.Tick();
                tickMs = tickMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1;
                double t0 = sw.Elapsed.TotalMilliseconds;
                BuildGroups();
                double t1 = sw.Elapsed.TotalMilliseconds;
                UpdateTray();
                double t2 = sw.Elapsed.TotalMilliseconds;
                UpdateSelf();
                double t3 = sw.Elapsed.TotalMilliseconds;
                if (IsShown) { BuildRows(); UpdateExplanation(); }
                double t4 = sw.Elapsed.TotalMilliseconds;
                phase = string.Format("groups {0:0.0} tray {1:0.0} self {2:0.0} rows {3:0.0} visible {4}", t1 - t0, t2 - t1, t3 - t2, t4 - t3, IsShown);
                totalMs = totalMs * 0.9 + sw.Elapsed.TotalMilliseconds * 0.1; refreshCount++;
            }
            catch (Exception ex) { Logger.Error("tick: " + ex); }
        }

        void UpdateSelf()
        {
            long cpu = eng.SelfCpu100ns; var now = DateTime.Now;
            double sec = (now - selfLastAt).TotalSeconds;
            if (sec > 0.5 && selfLastCpu > 0) selfCpu = (cpu - selfLastCpu) / 1e7 * 100.0 / (sec * Environment.ProcessorCount);
            selfLastCpu = cpu; selfLastAt = now;
            if (selfLongCpu < 0) { selfLongCpu = cpu; selfLongAt = now; }
            else if ((now - selfLongAt).TotalSeconds >= 60) { selfCpuLong = (cpu - selfLongCpu) / 1e7 * 100.0 / ((now - selfLongAt).TotalSeconds * Environment.ProcessorCount); selfLongCpu = cpu; selfLongAt = now; }
            if (IsShown)
                status.Text = string.Format("{0} processes | Agent Watch CPU {1:0.00}%{2} | updated {3:h:mm:ss tt} ET{4}",
                    eng.ProcessCount, selfCpu, selfCpuLong >= 0 ? string.Format(" (1-min {0:0.00}%)", selfCpuLong) : "", now, eng.X.LoadError != null ? " | " + eng.X.LoadError : "");
            if ((now - lastStatusWrite).TotalSeconds >= 30) { lastStatusWrite = now; WriteStatus(); }
        }

        void WriteStatus()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Agent Watch status " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ET");
            sb.AppendLine(string.Format("Self: CPU {0:0.00}% of machine (1-min avg {1}), private RAM {2:0} MB, processes scanned {3}, avg scan {4:0.0} ms, avg tick {5:0.0} ms, ticks {6}",
                selfCpu, selfCpuLong >= 0 ? selfCpuLong.ToString("0.00") + "%" : "n/a", eng.SelfPrivBytes / 1048576.0, eng.ProcessCount, tickMs, totalMs, refreshCount));
            sb.AppendLine("Last tick phases (ms): " + phase);
            sb.AppendLine("Headline: " + headlineText);
            sb.AppendLine("Tray: " + state + (eng.X.LoadError != null ? " | " + eng.X.LoadError : string.Format(" | {0} explanation rules", eng.X.Rules.Count)));
            sb.AppendLine("OpenBot cua-driver.log last written: " + (eng.CuaLogWrite == DateTime.MinValue ? "never" : eng.CuaLogWrite.ToString("yyyy-MM-dd HH:mm:ss")) + (eng.OpenBotScanning ? " (SCANNING NOW)" : ""));
            sb.AppendLine("Apps:");
            foreach (var g in groups) sb.AppendLine(string.Format("  [{0}] {1} ({2} processes, {3:0.0}% CPU): {4}", g.Lv.ToString().ToUpper(), g.App, g.Procs.Count, g.Cpu, g.Sentence));
            sb.AppendLine("Screen controllers:");
            foreach (var p in eng.Controllers) sb.AppendLine(string.Format("  {0} > {1} ({2}) PID {3} parent {4}({5}) started {6:yyyy-MM-dd HH:mm:ss} CPU {7:0.00}% {8}", Engine.AppOf(p), p.Name, p.E != null ? p.E.Friendly : p.C.Label, p.Pid, p.Parent != null ? p.Parent.Name : "?", p.Ppid, p.StartLocal, p.Cpu, p.Active ? "ACTIVE" : "idle (loaded)"));
            foreach (var r in eng.RemoteSessions) sb.AppendLine("  " + r);
            sb.AppendLine("Screen-control episodes (newest first):");
            for (int i = 0; i < Math.Min(8, eng.History.Count); i++) { var e = eng.History[i]; sb.AppendLine(string.Format("  {0:yyyy-MM-dd HH:mm:ss} - {1} {2} - {3} | {4} PID {5} | {6} | {7}", e.Start, e.Open ? "(now)" : e.End.ToString("HH:mm:ss"), e.App, e.Agent, e.Proc, e.Pid, e.Trigger, e.Detail)); }
            sb.AppendLine("Hidden by category filter: " + string.Join(", ", allGroups.FindAll(g => !groups.Contains(g)).ConvertAll(g => g.App + " (" + g.Category + ")")));
            Logger.WriteText("status.txt", sb.ToString());
        }

        static string Fr(PInfo p) { return p.E != null && p.E.Friendly != null ? p.E.Friendly : (p.C != null ? p.C.Label : p.Name); }

        void Collect(GroupInfo g, PInfo p, PInfo root, int depth)
        {
            g.Procs.Add(p); g.Cpu += p.Cpu; g.Ram += p.RamMB;
            if (p.C != null && p.C.Role == Role.Controller) { if (p.Active) g.ActiveCtrls.Add(p); else g.IdleCtrls.Add(p); }
            else if (p.Lv == Level.Yellow) g.Yellows.Add(p);
            if (depth < 40) foreach (var k in p.Kids) if (k.Root == root) Collect(g, k, root, depth + 1);
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
                foreach (var r in g.Roots) Collect(g, r, r, 0);
                g.Harmless = true; foreach (var p in g.Procs) if (p.C != null && p.C.Role != Role.Harmless) { g.Harmless = false; break; }
                g.Infra = InfraApps.Contains(g.App);
                g.Lv = Level.Green;
                foreach (var p in g.Procs) if (p.Lv > g.Lv) g.Lv = p.Lv;
                g.Sentence = Sentence(g);
                var re = g.Roots.Count > 0 ? g.Roots[0].E : null;
                g.Category = re != null && !string.IsNullOrEmpty(re.Category) ? re.Category : (g.Roots.Count > 0 && g.Roots[0].C != null && g.Roots[0].C.Role == Role.Controller ? "screen-control" : "agent");
                g.Doing = Doing(g);
            }
            allGroups = new List<GroupInfo>(list);
            var cats = eng.X.ShowCategories;
            list.RemoveAll(g => !(cats.Contains(g.Category) || (g.Category == "model-server" && Settings.ShowModelServers) || g.IdleCtrls.Count + g.ActiveCtrls.Count > 0));
            list.Sort((a, b) => { int c = b.Lv.CompareTo(a.Lv); if (c != 0) return c; return string.Compare(a.App, b.App, StringComparison.OrdinalIgnoreCase); });
            groups = list;

            // headline
            var red = new List<string>(); var idle = new List<string>(); int ai = 0;
            foreach (var g in groups) { if (g.ActiveCtrls.Count > 0) red.Add(g.App); if (g.ActiveCtrls.Count == 0 && g.IdleCtrls.Count > 0) idle.Add(g.App); if (g.Category == "agent") ai++; }
            string h;
            if (eng.RemoteSessions.Count > 0) h = "Someone is connected to this PC by Remote Desktop right now.";
            else if (red.Count > 0) h = JoinAnd(red) + (red.Count == 1 ? " is" : " are") + " controlling or scanning your screen right now.";
            else h = "Nothing is controlling your screen right now.";
            h += string.Format(" {0} AI agent app{1} open.", ai, ai == 1 ? "" : "s");
            if (idle.Count > 0) h += string.Format(" {0} {1} a screen-control helper loaded but idle.", JoinAnd(idle), idle.Count == 1 ? "has" : "have");
            headlineText = h;
        }

        static string JoinAnd(List<string> l)
        {
            if (l.Count == 1) return l[0];
            if (l.Count == 2) return l[0] + " and " + l[1];
            return string.Join(", ", l.GetRange(0, l.Count - 1)) + " and " + l[l.Count - 1];
        }

        string Doing(GroupInfo g)
        {
            if (g.ActiveCtrls.Count > 0)
            {
                var names = new List<string>(); foreach (var p in g.ActiveCtrls) { string a = Engine.AgentOf(p); if (!names.Contains(a)) names.Add(a); }
                return "CONTROLLING THE SCREEN (" + string.Join(", ", names) + ")";
            }
            var tools = new List<string>();
            var rootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var r in g.Roots) rootNames.Add(r.Name);
            foreach (var p in g.Procs)
            {
                if (rootNames.Contains(p.Name) || p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase)) continue;
                bool fresh = p.Create > 0 && (DateTime.Now - p.StartLocal).TotalSeconds < 60;
                bool busyTool = p.Cpu >= 0.5 && p.E != null && (p.E.Category == "mcp" || p.E.Id == "agent-shell");
                if (fresh || busyTool) { string f = p.E != null && p.E.Id == "agent-shell" ? p.Name : Fr(p); if (!tools.Contains(f)) tools.Add(f); }
            }
            if (tools.Count > 0) return "Running tools (" + string.Join(", ", tools.GetRange(0, Math.Min(2, tools.Count))) + (tools.Count > 2 ? ", ..." : "") + ")";
            if (g.Cpu >= 0.5) return string.Format("Working / thinking ({0:0.0}% CPU)", g.Cpu);
            return "Idle";
        }

        string Sentence(GroupInfo g)
        {
            if (g.App.StartsWith("WSL graphics")) return "Windows' own bridge for Linux (WSL) app windows. Local only - nobody is remoting in.";
            if (g.ActiveCtrls.Count > 0)
            {
                var p = g.ActiveCtrls[0];
                bool log = eng.OpenBotScanning && g.App == "OpenBot";
                return string.Format("{0} is using your screen right now: {1} ({2:0.0}% CPU{3}).", g.App, Fr(p), p.Cpu,
                    log ? ", scanning windows " + (int)(DateTime.Now - eng.CuaLogWrite).TotalSeconds + "s ago" : "");
            }
            var sb = new StringBuilder();
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
            if (g.IdleCtrls.Count == 0 && g.Yellows.Count == 0) sb.Append(" and running normally");
            sb.AppendFormat(". {0} process{1}.", g.Procs.Count, g.Procs.Count == 1 ? "" : "es");
            return sb.ToString();
        }

        static string LevelWord(Level l) { return l == Level.Red ? "RED" : l == Level.Yellow ? "YELLOW" : l == Level.Green ? "GREEN" : "UNKNOWN"; }

        void UpdateTray()
        {
            Level top = Level.None;
            foreach (var g in groups) if (g.Lv > top) top = g.Lv;
            if (eng.RemoteSessions.Count > 0) top = Level.Red;
            string newState = top.ToString();
            if (newState != state)
            {
                blink.Stop();
                if (top == Level.Red) { blink.Start(); tray.Icon = icRedBright; }
                else if (top == Level.Yellow) tray.Icon = icYellow;
                else if (top == Level.Green) tray.Icon = icGreen;
                else tray.Icon = icIdle;
                state = newState;
            }
            string tip = headlineText.Length > 0 ? headlineText : "Agent Watch";
            foreach (var e in eng.OpenEpisodes.Values) { tip = "SCREEN: " + e.App + " - " + e.Agent; break; }
            if (tip.Length > 63) tip = tip.Substring(0, 60) + "...";
            if (tip != lastTip) { tray.Text = tip; lastTip = tip; }

            if (eng.FirstTick) return;
            foreach (var p in eng.NewControllers)
                Balloon("Screen-control helper loaded", string.Format("{0}: {1} (PID {2}) started{3}.", Engine.AppOf(p), Fr(p), p.Pid, p.Parent != null ? " by " + p.Parent.Name : ""), ToolTipIcon.Info);
            foreach (var e in eng.NewEpisodes)
            { lastBalloon = DateTime.MinValue; Balloon(e.App + " is controlling your screen", string.Format("{0} via {1} (PID {2}) at {3:h:mm:ss tt} ET. {4}", e.Agent, e.Proc, e.Pid, e.Start, e.Trigger), ToolTipIcon.Warning); }
            if (eng.NewEpisodes.Count == 0) foreach (var p in eng.NewlyActive)
                Balloon(Engine.AppOf(p) + " is using your screen", string.Format("{0} (PID {1}) is active{2}. Click for details.", Fr(p), p.Pid, eng.OpenBotScanning && Engine.AppOf(p) == "OpenBot" ? " and scanning your windows" : ""), ToolTipIcon.Warning);
        }

        bool firstBalloonDone = false;
        public void StartupBalloon()
        {
            if (firstBalloonDone) return; firstBalloonDone = true;
            tray.ShowBalloonTip(6000, "Agent Watch is running", headlineText, ToolTipIcon.Info);
        }

        void BuildRows()
        {
            headline.Text = headlineText;
            bool anyRed = false; foreach (var g in groups) if (g.Lv == Level.Red) anyRed = true;
            headline.ForeColor = anyRed || eng.RemoteSessions.Count > 0 ? Color.FromArgb(254, 205, 211) : Color.White;
            if (!chkDetails.Checked) BuildSummary(); else BuildDetails();
            UpdateBanner(); BuildHistory();
        }

        void UpdateBanner()
        {
            string t; Color b, f;
            if (eng.OpenEpisodes.Count > 0)
            {
                var parts = new List<string>();
                foreach (var e in eng.OpenEpisodes.Values) parts.Add(string.Format("{0} - {1} (since {2:h:mm:ss tt} ET, {3} PID {4})", e.App, e.Agent, e.Start, e.Proc, e.Pid));
                t = "\u25CF CONTROLLING YOUR SCREEN NOW: " + string.Join("  |  ", parts);
                b = Color.FromArgb(190, 18, 60); f = Color.White;
            }
            else if (eng.RemoteSessions.Count > 0) { t = "\u25CF " + eng.RemoteSessions[0]; b = Color.FromArgb(190, 18, 60); f = Color.White; }
            else if (eng.History.Count > 0)
            {
                var e = eng.History[0];
                t = string.Format("Nobody is controlling the screen now.   Last time: {0} - {1}, {2:MMM d h:mm:ss tt} to {3:h:mm:ss tt} ET ({4}).", e.App, e.Agent, e.Start, e.End, e.Proc);
                b = C_PANEL2; f = C_TEXT;
            }
            else { t = "Nobody is controlling the screen now. No screen-control activity seen yet."; b = C_PANEL2; f = C_DIM; }
            if (banner.Text != t) banner.Text = t;
            banner.BackColor = b; banner.ForeColor = f;
        }

        void BuildHistory()
        {
            int sig = eng.History.Count * 31 + (eng.History.Count > 0 ? eng.History[0].End.GetHashCode() + (eng.History[0].Open ? 7 : 0) + eng.History[0].Detail.Length : 0);
            if (sig == histSig) return; histSig = sig;
            var list = new List<Row>();
            foreach (var e in eng.History)
            {
                string what = e.Detail != null && e.Detail.Length > 0 ? e.Detail : (e.Open ? "in progress - " + e.Trigger : e.Trigger);
                list.Add(new Row { Key = "h:" + e.Start.Ticks + e.App, T = new[] { e.Start.ToString("MMM d  h:mm:ss tt") + " ET", e.Open ? "now" : e.End.ToString("h:mm:ss tt"), e.App + " - " + e.Agent, e.Proc + " PID " + e.Pid, what },
                    Back = e.Open ? C_REDROW : C_LIST, Fore = Color.White, Bold = e.Open, Ep = e });
            }
            ApplyRows(lvHist, list);
        }

        void BuildSummary()
        {
            var list = new List<Row>(); int i = 0;
            foreach (var r in eng.RemoteSessions)
                list.Add(new Row { Key = "rdp:" + r, T = new[] { "", "Remote Desktop", "Someone is connected", "", "", "", r }, Back = C_REDROW, Fore = Color.White, Bold = true, PillCol = 0, PillLevel = Level.Red, PillText = "RED" });
            foreach (var g in groups)
            {
                i++;
                string pill = g.Lv == Level.Red ? "RED - ACTIVE" : g.Lv == Level.Yellow && g.IdleCtrls.Count > 0 ? "YELLOW - loaded" : LevelWord(g.Lv);
                list.Add(new Row { Key = "g:" + g.App, G = g, T = new[] { pill, g.App, g.Doing, g.Procs.Count.ToString(), g.Cpu.ToString("0.0"), g.Ram.ToString("0"), g.Sentence },
                    Back = g.Lv == Level.Red ? C_REDROW : (i % 2 == 0 ? C_LIST2 : C_LIST), Fore = Color.White, Bold = false, PillCol = 0, PillLevel = g.Lv, PillText = pill });
            }
            ApplyRows(lvSum, list);
        }

        void BuildDetails()
        {
            var list = new List<Row>();
            foreach (var r in eng.RemoteSessions)
                list.Add(new Row { Key = "rdp:" + r, T = new[] { "Remote Desktop: " + r, "", "", "", "", "RED", "" }, Back = C_REDROW, Fore = Color.White, Bold = true, PillCol = 5, PillLevel = Level.Red, PillText = "RED - remote session" });
            foreach (var g in groups)
            {
                list.Add(new Row { Key = "g:" + g.App, G = g, T = new[] { g.App + "   (" + g.Procs.Count + " processes)", "", "", g.Cpu.ToString("0.0"), g.Ram.ToString("0"), LevelWord(g.Lv), g.Sentence },
                    Back = g.Lv == Level.Red ? C_REDROW : C_GROUP, Fore = Color.White, Bold = true, PillCol = 5, PillLevel = g.Lv, PillText = LevelWord(g.Lv) });
                foreach (var r in g.Roots) AddTree(list, r, r, 1);
            }
            ApplyRows(lv, list);
        }

        void AddTree(List<Row> list, PInfo p, PInfo root, int depth)
        {
            bool helper = p.Name.Equals("conhost.exe", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase);
            bool show = chkKids.Checked || !helper;
            if (show)
            {
                string indent = new string(' ', (depth - 1) * 4) + (depth > 1 ? "\u2514 " : "");
                string label = p.E != null && p.E.Friendly != null ? "  \u2014 " + p.E.Friendly : (p.C != null ? "  \u2014 " + p.C.Label : "  \u2014 (unknown)");
                if (p.MatchedParent != null && p.E != null && p.MatchedParent.E == p.E) label = "";
                string pill; Level lvl = p.Lv;
                if (p.C != null && p.C.Role == Role.Controller) pill = p.Active ? "RED - controlling now" : "YELLOW - loaded, idle";
                else pill = p.E == null && p.C == null ? "UNKNOWN" : LevelWord(lvl);
                Color back = lvl == Level.Red ? C_REDROW : depth == 1 ? C_ROOT : C_CHILD;
                Color fore = p.E == null && p.C == null ? C_DIM : C_TEXT;
                string started = p.Create > 0 ? (p.StartLocal.Date == DateTime.Today ? p.StartLocal.ToString("h:mm:ss tt") : p.StartLocal.ToString("MMM d h:mm tt")) : "";
                string cmd = !string.IsNullOrEmpty(p.Cmd) ? Redactor.Clean(p.Cmd, 300) : (p.Path ?? "");
                list.Add(new Row { Key = "p:" + p.Key, P = p, Back = back, Fore = fore, Bold = depth == 1 || lvl == Level.Red, PillCol = 5, PillLevel = lvl, PillText = pill,
                    T = new[] { indent + p.Name + label, p.Pid.ToString(), started, p.Cpu.ToString("0.0"), p.RamMB.ToString("0"), pill, cmd } });
            }
            if (depth > 40) return;
            var kids = new List<PInfo>(); foreach (var k in p.Kids) if (k.Root == root) kids.Add(k);
            kids.Sort((a, b) => a.Create.CompareTo(b.Create));
            foreach (var k in kids) AddTree(list, k, root, show ? depth + 1 : depth);
        }

        void ApplyRows(DBListView l, List<Row> list)
        {
            bool same = list.Count == l.Items.Count;
            if (same) for (int i = 0; i < list.Count; i++) if (((Row)l.Items[i].Tag).Key != list[i].Key) { same = false; break; }
            l.BeginUpdate();
            try
            {
                if (same)
                {
                    for (int i = 0; i < list.Count; i++)
                    {
                        var it = l.Items[i]; it.Tag = list[i];
                        for (int c = 0; c < list[i].T.Length; c++) if (it.SubItems[c].Text != list[i].T[c]) it.SubItems[c].Text = list[i].T[c];
                    }
                    l.Invalidate();
                }
                else
                {
                    string selKey = l.SelectedItems.Count > 0 ? ((Row)l.SelectedItems[0].Tag).Key : null;
                    int topIdx = l.TopItem != null ? l.TopItem.Index : 0;
                    l.Items.Clear();
                    var items = new ListViewItem[list.Count];
                    for (int i = 0; i < list.Count; i++) { var it = new ListViewItem(list[i].T); it.Tag = list[i]; items[i] = it; }
                    l.Items.AddRange(items);
                    if (selKey != null) foreach (ListViewItem it in l.Items) if (((Row)it.Tag).Key == selKey) { it.Selected = true; break; }
                    if (l.Items.Count > 0) { try { l.TopItem = l.Items[Math.Min(topIdx, l.Items.Count - 1)]; } catch { } }
                }
            }
            finally { l.EndUpdate(); }
            FitLastColumn(l);
        }

        void UpdateExplanation()
        {
            var row = SelectedRow();
            string title = "What is this?", badge = null, body; Level lvl = Level.None;
            noteTarget = null; noteApp = null; bool showNote = false;
            if (row == null) body = "Select a row to see, in plain English, what it is, why it is running, how risky it is, and what to do about it.\r\n\r\nDouble-click an app (or tick 'Show details') to see every process it runs.";
            else if (row.Ep != null)
            {
                var e = row.Ep; title = "Screen control by " + e.App; lvl = e.Open ? Level.Red : Level.Yellow;
                badge = e.Open ? "  RED - happening now" : "  Past episode";
                body = string.Format("Who: {0} - {1}\r\nProcess: {2}, PID {3}\r\nStarted: {4:MMM d, h:mm:ss tt} ET\r\nEnded: {5}\r\nWhat set it off: {6}\r\n\r\nWhat it was doing: {7}\r\n\r\nEvery episode is saved in D:\\DEBUGGING LOGS\\AgentWatch\\screen-episodes.tsv and events.log.",
                    e.App, e.Agent, e.Proc, e.Pid, e.Start, e.Open ? "still going" : e.End.ToString("h:mm:ss tt") + " ET", e.Trigger, string.IsNullOrEmpty(e.Detail) ? (e.Open ? "(filled in when it ends, from the app's logs where available)" : "no extra detail in the app's logs") : e.Detail);
            }
            else if (row.G != null && row.P == null)
            {
                var g = row.G; title = g.App; lvl = g.Lv;
                badge = g.Lv == Level.Red ? "  RED - using your screen right now" : g.Lv == Level.Yellow ? (g.IdleCtrls.Count > 0 ? "  YELLOW - screen helper loaded, idle" : "  YELLOW - can act on your behalf") : "  GREEN - normal";
                var sb = new StringBuilder();
                sb.Append("Doing now: ").Append(g.Doing).Append("\r\n\r\n").Append(g.Sentence).Append("\r\n\r\n");
                var re = g.Roots.Count > 0 ? g.Roots[0].E : null;
                if (re != null)
                {
                    if (re.What != null) sb.Append("What it is: ").Append(re.What).Append("\r\n\r\n");
                    if (re.Why != null) sb.Append("Why it's running: ").Append(re.Why).Append("\r\n\r\n");
                    if (re.Todo != null) sb.Append("What to do: ").Append(re.Todo).Append("\r\n\r\n");
                }
                sb.Append("What's inside:\r\n");
                var counts = new Dictionary<string, int>(); var lvls = new Dictionary<string, string>(); var order = new List<string>();
                foreach (var p in g.Procs)
                {
                    string f = Fr(p);
                    string lw = p.C != null && p.C.Role == Role.Controller ? (p.Active ? "RED, active" : "YELLOW, loaded but idle") : (p.E == null && p.C == null ? "unknown" : LevelWord(p.Lv).ToLower());
                    string k = f + "|" + lw;
                    if (!counts.ContainsKey(k)) { counts[k] = 0; order.Add(k); lvls[k] = lw; }
                    counts[k]++;
                }
                order.Sort((a, b) => { int c = Rank(lvls[b]).CompareTo(Rank(lvls[a])); return c != 0 ? c : string.Compare(a, b, StringComparison.OrdinalIgnoreCase); });
                foreach (var k in order) sb.AppendFormat("  \u2022 {0}{1} - {2}\r\n", k.Split('|')[0], counts[k] > 1 ? " \u00d7" + counts[k] : "", lvls[k]);
                body = sb.ToString();
            }
            else if (row.P != null)
            {
                var p = row.P; lvl = p.Lv; var e = p.E;
                title = (e != null && e.Friendly != null ? e.Friendly : p.Name);
                var sb = new StringBuilder();
                sb.AppendFormat("{0}  (PID {1})  -  part of {2}\r\n", p.Name, p.Pid, Engine.AppOf(p));
                if (p.C != null && p.C.Role == Role.Controller)
                {
                    badge = p.Active ? "  RED - controlling / scanning your screen now" : "  YELLOW - screen helper loaded, idle";
                    sb.AppendFormat("Right now: {0} ({1:0.0}% CPU{2}).\r\n", p.Active ? "ACTIVE" : "idle", p.Cpu,
                        Engine.AppOf(p) == "OpenBot" && eng.CuaLogWrite != DateTime.MinValue ? ", OpenBot's screen log last written " + eng.CuaLogWrite.ToString("h:mm:ss tt") + " ET" : "");
                }
                else badge = e == null && p.C == null ? "  UNKNOWN" : "  " + LevelWord(lvl) + (lvl == Level.Yellow ? " - can act on your behalf" : " - normal");
                sb.Append("\r\n");
                if (e != null)
                {
                    if (e.What != null) sb.Append("What it does: ").Append(e.What).Append("\r\n\r\n");
                    if (e.Why != null) sb.Append("Why it's running: ").Append(e.Why).Append("\r\n\r\n");
                    if (e.Todo != null) sb.Append("What to do: ").Append(e.Todo).Append("\r\n\r\n");
                }
                else
                {
                    sb.Append("Unknown - not in the explanation list yet.\r\n\r\n");
                    if (p.Parent != null) sb.AppendFormat("It was started by {0}{1}.\r\n\r\n", p.Parent.Name, p.Parent.E != null && p.Parent.E.Friendly != null ? " (" + p.Parent.E.Friendly + ")" : "");
                    sb.Append("Use 'Add note' to add a stub rule to explanations.json, then describe it there.\r\n\r\n");
                    noteTarget = p; noteApp = Engine.AppOf(p); showNote = true;
                }
                sb.AppendFormat("Started: {0}\r\nCPU: {1:0.0}%   RAM: {2:0} MB\r\nParent: {3}\r\nPath: {4}\r\nCommand: {5}",
                    p.Create > 0 ? p.StartLocal.ToString("MMM d, h:mm:ss tt") + " ET" : "?", p.Cpu, p.RamMB,
                    p.Parent != null ? p.Parent.Name + " (PID " + p.Parent.Pid + ")" : "gone (PID " + p.Ppid + ")", p.Path ?? "(hidden)", Redactor.Clean(p.Cmd, 600));
                body = sb.ToString();
            }
            else body = row.T.Length > 5 ? row.T[5] : "";

            if (exTitle.Text != title) exTitle.Text = title;
            exBadge.Visible = badge != null;
            if (badge != null) { if (exBadge.Text != badge) exBadge.Text = badge; exBadge.BackColor = PillColor(lvl); exBadge.ForeColor = lvl == Level.Red ? Color.White : Color.FromArgb(30, 27, 75); }
            if (exText.Text != body) { int ss = exText.SelectionStart; exText.Text = body; try { exText.SelectionStart = Math.Min(ss, exText.Text.Length); exText.SelectionLength = 0; } catch { } }
            bAddNote.Visible = showNote;
        }

        static int Rank(string lw) { return lw.StartsWith("RED") ? 4 : lw.StartsWith("YELLOW") ? 3 : lw == "yellow" ? 3 : lw == "unknown" ? 2 : 1; }

        public void TakeScreenshot(string path)
        {
            bool wasVisible = IsShown;
            ShowMain();
            TopMost = true;
            var t = new System.Windows.Forms.Timer { Interval = 1800 };
            t.Tick += delegate
            {
                t.Stop(); t.Dispose();
                try
                {
                    RefreshNow(); Application.DoEvents();
                    using (var bmp = new Bitmap(Width, Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                        {
                            IntPtr hdc = g.GetHdc();
                            try { Native.PrintWindow(Handle, hdc, 2); } finally { g.ReleaseHdc(hdc); }
                        }
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Logger.Event("SCREENSHOT saved to " + path);
                }
                catch (Exception ex) { Logger.Error("screenshot: " + ex); }
                TopMost = false;
                if (!wasVisible) Hide();
            };
            t.Start();
        }

        public void SelectForShot(string what)
        {
            // used by --screenshot-details / --select to show a populated explanation pane
            if (what == "details") chkDetails.Checked = true;
            var l = chkDetails.Checked ? lv : lvSum;
            if (l.Items.Count > 0) { l.Items[0].Selected = true; UpdateExplanation(); }
        }

        bool firstShowHandled = false;
        protected override void SetVisibleCore(bool value)
        {
            if (!firstShowHandled) { firstShowHandled = true; if (Program.StartHidden) { if (!IsHandleCreated) CreateHandle(); value = false; } }
            base.SetVisibleCore(value);
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); FitLastColumn(lvSum); FitLastColumn(lv); }
        public void Shutdown() { exiting = true; tray.Visible = false; Logger.Event("===== Agent Watch stopped ====="); }
    }

    static class Program
    {
        public static bool StartHidden;
        [STAThread]
        static void Main(string[] args)
        {
            bool shot = false; string shotPath = Path.Combine(Logger.Dir, "screenshot.png");
            foreach (var a in args)
            {
                if (a.Equals("--hidden", StringComparison.OrdinalIgnoreCase)) StartHidden = true;
                if (a.Equals("--screenshot", StringComparison.OrdinalIgnoreCase)) shot = true;
            }
            bool created;
            var mutex = new Mutex(true, "Local\\AgentWatch.Single", out created);
            var evShow = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentWatch.Show");
            var evShot = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\AgentWatch.Shot");
            if (!created)
            {
                if (shot) evShot.Set(); else if (!StartHidden) evShow.Set();
                return;
            }
            try { Native.SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { Logger.Error("ui: " + e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { Logger.Error("fatal: " + e.ExceptionObject); };
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            Settings.Load();
            if (StartHidden && Settings.WindowOpen) StartHidden = false;
            var form = new MainForm();
            var th = new Thread(delegate()
            {
                var hs = new WaitHandle[] { evShow, evShot };
                while (true)
                {
                    int i = WaitHandle.WaitAny(hs);
                    try
                    {
                        if (i == 0) form.BeginInvoke((MethodInvoker)delegate { form.ShowMain(); });
                        else form.BeginInvoke((MethodInvoker)delegate { form.SelectForShot("summary"); form.TakeScreenshot(shotPath); });
                    }
                    catch { }
                }
            });
            th.IsBackground = true; th.Start();
            var once = new System.Windows.Forms.Timer { Interval = 3000 };
            once.Tick += delegate { once.Stop(); form.StartupBalloon(); if (shot) form.TakeScreenshot(shotPath); };
            once.Start();
            Application.ApplicationExit += delegate { form.Shutdown(); };
            Application.Run(form);
            GC.KeepAlive(mutex);
        }
    }
}
