// Agent Watch - shows live AI agents, their child processes, and screen-control helpers.
// Built with the .NET Framework csc.exe (C# 5). No admin, no installs.
using System;
using System.Collections.Generic;
using System.Diagnostics;


using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyTitle("Agent Watch")]
[assembly: System.Reflection.AssemblyProduct("Agent Watch")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

namespace AgentWatch
{
    static class AppPaths
    {
        public static readonly string Exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        public static readonly string Dir = Path.GetDirectoryName(Exe);
    }

    class DangerRule
    {
        public string Id, Title, Kind, Why, Todo; public Regex Match, Name; public bool AgentOnly = true;
    }

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
        public delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        public static string WinText(IntPtr h) { int n = GetWindowTextLengthW(h); if (n <= 0) return ""; var sb = new StringBuilder(Math.Min(n + 1, 512)); GetWindowTextW(h, sb, sb.Capacity); return sb.ToString(); }
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
        public bool Active; public DateTime LastActive = DateTime.MinValue; public string ActiveReason = ""; public bool AgentApp; public string AppName, AppAgent, WinTitle; public DateTime LastBusy = DateTime.MinValue;
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
        public string Id, App, Friendly, Risk, What, Why, Todo, Category, Short; public bool Agent;
        public Regex Name, PathRx, Cmd, Parent;
        public Level BaseLevel { get { return Risk == "yellow" || Risk == "screen" ? Level.Yellow : Level.Green; } }
    }

    class Explainer
    {
        public static string FilePath = Path.Combine(AppPaths.Dir, "explanations.json");
        public List<Expl> Rules = new List<Expl>();
        public int Version = 0;
        public HashSet<string> ShowCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent", "mcp", "screen-control" };
        public string LoadError = null;
        public List<DangerRule> Dangers = new List<DangerRule>();
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
                        e.What = Str(d, "what"); e.Why = Str(d, "why"); e.Todo = Str(d, "todo"); e.Category = (Str(d, "category") ?? "").ToLowerInvariant(); e.Short = Str(d, "short");
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
                var dl = new List<DangerRule>();
                object da; if (root.TryGetValue("danger", out da) && da is object[])
                    foreach (var o in (object[])da)
                    {
                        var d = o as Dictionary<string, object>; if (d == null) continue;
                        try
                        {
                            var r = new DangerRule { Id = Str(d, "id"), Title = Str(d, "title"), Kind = (Str(d, "kind") ?? "cmd").ToLowerInvariant(), Why = Str(d, "why"), Todo = Str(d, "todo"), Match = Rx(Str(d, "match")), Name = Rx(Str(d, "name")) };
                            object ao; if (d.TryGetValue("agentOnly", out ao) && ao is bool) r.AgentOnly = (bool)ao;
                            if (r.Match != null || r.Name != null) dl.Add(r);
                        }
                        catch (Exception ex) { Logger.Error("explanations.json danger rule: " + ex.Message); }
                    }
                Dangers = dl;
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
        public DateTime Start, End; public bool Open; public string App, Agent, Proc, Trigger, Detail = "", Kind = "screen", Target; public int Pid, Count = 1;
        public string What { get { return Kind == "danger" ? "DANGER: " + (Target ?? "risky command") : Kind == "app" ? "Opened " + (Target ?? Proc) + (Count > 1 ? " (" + Count + " copies)" : "") + (Open ? " (still open)" : "") : "Controlling " + (Target ?? "the screen"); } }
        static string Clean(string x) { return (x ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }
        public string ToLine()
        {
            return string.Join("\t", new[] { Start.ToString("yyyy-MM-dd HH:mm:ss"), End.ToString("yyyy-MM-dd HH:mm:ss"), Clean(App), Clean(Agent), Clean(Proc), Pid.ToString(), Clean(Trigger), Clean(Detail), Clean(Kind), Clean(Target) });
        }
        public static Episode FromLine(string l)
        {
            var f = l.Split('\t'); if (f.Length < 8) return null;
            var e = new Episode();
            DateTime.TryParse(f[0], out e.Start); DateTime.TryParse(f[1], out e.End);
            e.App = f[2]; e.Agent = f[3]; e.Proc = f[4]; int.TryParse(f[5], out e.Pid); e.Trigger = f[6]; e.Detail = f[7]; if (f.Length > 8) e.Kind = f[8]; if (f.Length > 9 && f[9].Length > 0) e.Target = f[9];
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
        long lastStamp = 0; DateTime cuaChecked = DateTime.MinValue; bool cuaReal = false;

        public Explainer X = new Explainer();
        public Dictionary<int, PInfo> All { get { return cache; } }
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
            NewControllers.Clear(); NewlyActive.Clear(); NewEpisodes.Clear(); NewAppEps.Clear(); newProcs.Clear();
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
            bool recent = (DateTime.Now - CuaLogWrite).TotalSeconds < 30;
            if (recent && CuaLogWrite != cuaChecked)
            {
                cuaChecked = CuaLogWrite; cuaReal = false;
                var tl = LogPeek.Tail(cuaLog, 4096);
                for (int i = tl.Count - 1; i >= 0 && i >= tl.Count - 4; i--) { var l = tl[i]; if (l.Trim().Length == 0) continue; cuaReal = !(l.Contains("daemon listening") || l.Contains("driver stopped")); break; }
            }
            OpenBotScanning = recent && cuaReal;

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
            ScanWindows();
            UpdateAgentApps();
            UpdateEpisodes();
            foreach (var p in alive.Values) p.Lv = LevelOf(p);
            foreach (var p in AgentApps) p.Lv = OpenEpisodes.ContainsKey(AppOf(p)) ? Level.Red : Level.Yellow;

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

        // ---------- windows + agent-launched apps ----------
        public Dictionary<int, string> Windows = new Dictionary<int, string>();
        public int FgPid; public string FgTitle = "";
        public List<PInfo> AgentApps = new List<PInfo>();
        public Dictionary<string, Episode> OpenAppEps = new Dictionary<string, Episode>();
        public List<Episode> NewAppEps = new List<Episode>();
        static Native.EnumWindowsProc enumCb; static Dictionary<int, string> enumTarget;
        static readonly Dictionary<string, string> descCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> NotApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "node.exe","conhost.exe","cmd.exe","powershell.exe","pwsh.exe","bash.exe","sh.exe","wsl.exe","wslhost.exe","py.exe","pyw.exe","uv.exe","npx.exe","bun.exe","deno.exe",
            "git.exe","gh.exe","cua-driver.exe","crashpad_handler.exe","werfault.exe","msedgewebview2.exe","openconsole.exe","rg.exe","curl.exe","tasklist.exe","where.exe","timeout.exe",
            "reg.exe","findstr.exe","net.exe","sc.exe","schtasks.exe","whoami.exe","taskkill.exe","dllhost.exe","rundll32.exe","node_repl.exe","java.exe","esbuild.exe" };
        static readonly HashSet<string> KnownGui = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "blender","chrome","msedge","firefox","brave","opera","notepad","notepad++","mspaint","paint","winword","excel","powerpnt","outlook","onenote","code","explorer","calc",
            "calculatorapp","photoshop","gimp","gimp-2.10","obs64","vlc","wordpad","acrobat","acrord32","sumatrapdf","discord","slack","ms-teams","figma","krita","inkscape","audacity",
            "resolve","unity","unrealeditor","godot","freecad","sketchup","mstsc","snippingtool","screensketch","photos","wt","windowsterminal" };

        void ScanWindows()
        {
            var d = new Dictionary<int, string>();
            enumTarget = d; if (enumCb == null) enumCb = EnumCb;
            try { Native.EnumWindows(enumCb, IntPtr.Zero); } catch { }
            Windows = d;
            try { IntPtr fg = Native.GetForegroundWindow(); uint pid; Native.GetWindowThreadProcessId(fg, out pid); FgPid = (int)pid; FgTitle = Native.WinText(fg); } catch { FgPid = 0; FgTitle = ""; }
        }
        static bool EnumCb(IntPtr h, IntPtr l)
        {
            if (!Native.IsWindowVisible(h) || Native.GetWindow(h, 4) != IntPtr.Zero || Native.GetWindowTextLengthW(h) == 0) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            if (!enumTarget.ContainsKey((int)pid)) enumTarget[(int)pid] = Native.WinText(h);
            return true;
        }

        public static string AppNameOf(PInfo p)
        {
            if (p.E != null && !string.IsNullOrEmpty(p.E.Short)) return p.E.Short;
            string desc = null;
            if (!string.IsNullOrEmpty(p.Path))
            {
                if (!descCache.TryGetValue(p.Path, out desc))
                {
                    try { var vi = FileVersionInfo.GetVersionInfo(p.Path); desc = !string.IsNullOrEmpty(vi.FileDescription) ? vi.FileDescription : vi.ProductName; } catch { }
                    if (desc != null && (desc.Length > 40 || desc.Trim().Length == 0)) desc = null;
                    descCache[p.Path] = desc;
                }
            }
            if (!string.IsNullOrEmpty(desc)) return desc.Trim();
            string n = Path.GetFileNameWithoutExtension(p.Name ?? "app");
            return n.Length > 0 ? char.ToUpper(n[0]) + n.Substring(1) : n;
        }

        static string RootCategory(PInfo r)
        {
            if (r == null) return "";
            if (r.E != null && !string.IsNullOrEmpty(r.E.Category)) return r.E.Category;
            return r.C != null && r.C.Role == Role.Agent ? "agent" : "";
        }

        bool IsGuiApp(PInfo p)
        {
            if (p.Root == null || p.Root == p || RootCategory(p.Root) != "agent") return false;
            string n = p.Name ?? "";
            if (NotApps.Contains(n) || n.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.C != null) return false;
            if (p.E != null && (p.E.Category == "agent" || p.E.Category == "mcp" || p.E.Category == "screen-control")) return false;
            int g = 0;
            for (var a = p.Parent; a != null && g < 4; a = a.Parent, g++) if (string.Equals(a.Name, n, StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(p.Root.Name, n, StringComparison.OrdinalIgnoreCase)) return false;
            return Windows.ContainsKey(p.Pid) || KnownGui.Contains(Path.GetFileNameWithoutExtension(n));
        }

        static string ShortOf(Expl e, PInfo p)
        {
            if (e != null && !string.IsNullOrEmpty(e.Short)) return e.Short;
            if (e != null && e.App != null) return e.App;
            return p.Name;
        }

        PInfo OpenBotRuntime()
        {
            PInfo best = null;
            foreach (var p in cache.Values)
            {
                if (p.E == null || p.E.Category != "agent" || !p.E.Id.StartsWith("openbot-") || p.E.Id == "openbot-app") continue;
                if (best == null || p.Cpu > best.Cpu + 0.05 || (Math.Abs(p.Cpu - best.Cpu) <= 0.05 && p.Create > best.Create)) best = p;
            }
            return best;
        }

        // who asked for it, e.g. "OpenBot (Gemini)", "Codex", "Claude"
        public string AgentShort(PInfo p)
        {
            int g = 0;
            for (var a = p.Parent; a != null && g < 10; a = a.Parent, g++)
            {
                if (a.E != null && a.E.Category == "agent" && a.E.Id != "agent-shell")
                {
                    if (a.E.Id == "openbot-app") { var rt = OpenBotRuntime(); return rt != null ? ShortOf(rt.E, rt) : "OpenBot"; }
                    return ShortOf(a.E, a);
                }
            }
            return AppOf(p);
        }

        public static string Possessive(string agentShort)
        {
            var m = Regex.Match(agentShort ?? "", @"^(.+?) \((.+)\)$");
            if (m.Success) return m.Groups[1].Value + "'s " + m.Groups[2].Value + " agent";
            return agentShort;
        }

        static readonly Regex LangGuard = new Regex(@"(?is)if\s*\(\s*\$ExecutionContext[^{}]*\{[^{}]*\}\s*;?");
        static readonly Regex TryPre = new Regex(@"(?is)try\s*\{[^{}]*\}\s*catch\s*\{[^{}]*\}\s*;?");
        static readonly Regex WinPath = new Regex(@"""?(?:[A-Za-z]:|\\\\)\\?[^""\s]*\\([^\\""\s]+)""?");
        static string SafeName(string v)
        {
            v = (v ?? "").Replace("\\\"", "").Trim('"', '\'', ' ', '\\');
            int i = Math.Max(v.LastIndexOf('\\'), v.LastIndexOf('/'));
            return i >= 0 ? v.Substring(i + 1) : v;
        }
        public static string ShellSummary(PInfo p) { try { return ShellSummary0(p); } catch { return null; } }
        static string ShellSummary0(PInfo p)
        {
            string c = p.Cmd; if (string.IsNullOrEmpty(c)) return null;
            if (c.IndexOf("CLAUDE_DESKTOP_TERMINAL_HOOKS", StringComparison.OrdinalIgnoreCase) >= 0) return "Claude's built-in terminal (an interactive PowerShell window)";
            string n = (p.Name ?? "").ToLowerInvariant();
            bool ps = n.StartsWith("powershell") || n.StartsWith("pwsh");
            string kind = ps ? "PowerShell command" : n == "cmd.exe" ? "command" : "Linux command";
            c = Regex.Replace(c, @"^\s*(""[^""]+""|\S+)\s*", "");
            var enc = Regex.Match(c, @"(?i)-e(nc|ncodedcommand)?\s+([A-Za-z0-9+/=]{8,})");
            if (ps && enc.Success) { try { c = Encoding.Unicode.GetString(Convert.FromBase64String(enc.Groups[2].Value)); } catch { return "Running an encoded PowerShell command"; } }
            var f = Regex.Match(c, @"(?i)-File\s+(""[^""]+""|\S+)");
            if (ps && f.Success) return "Running a PowerShell script: " + SafeName(f.Groups[1].Value);
            c = Regex.Replace(c, @"(?i)(^|\s)(-NoProfile|-NonInteractive|-NoLogo|-NoExit|-ExecutionPolicy\s+\S+|-WindowStyle\s+\S+|-InputFormat\s+\S+|-OutputFormat\s+\S+|-Command|-c|/d|/s|/c|/k|/e:ON|/v:OFF)(?=\s|$)", " ");
            for (int i = 0; i < 3; i++) c = TryPre.Replace(LangGuard.Replace(c, ""), " ");
            c = c.Replace("^", "");
            c = WinPath.Replace(c, "$1");
            c = c.Replace("\\\"", "\"");
            c = Regex.Replace(c, @"\s+", " ").Trim().Trim('"').Trim();
            if (c.StartsWith("& ")) c = c.Substring(2);
            if (c.StartsWith("call ")) c = c.Substring(5);
            c = Redactor.Clean(c, 90);
            if (c.Length == 0) return ps ? "Open PowerShell session (waiting for commands)" : "Open command prompt";
            return "Running a " + kind + ": " + c;
        }

        void UpdateAgentApps()
        {
            var apps = new List<PInfo>();
            foreach (var p in cache.Values)
            {
                if (!IsGuiApp(p)) { p.AgentApp = false; continue; }
                p.AgentApp = true;
                if (p.AppName == null) p.AppName = AppNameOf(p);
                p.AppAgent = AgentShort(p);
                string t; p.WinTitle = Windows.TryGetValue(p.Pid, out t) ? t : null;
                if (p.Cpu >= 0.5) p.LastBusy = DateTime.Now;
                apps.Add(p);
                Episode e;
                if (!OpenAppEps.TryGetValue(p.Key, out e))
                {
                    var st0 = p.Create > 0 ? p.StartLocal : DateTime.Now;
                    foreach (var o in OpenAppEps.Values) if (o.Agent == p.AppAgent && o.Target == p.AppName && Math.Abs((o.Start - st0).TotalSeconds) < 120) { e = o; break; }
                    if (e != null) { OpenAppEps[p.Key] = e; e.Count++; e.Proc = p.Name + " x" + e.Count; e.End = DateTime.Now; continue; }
                }
                if (!OpenAppEps.TryGetValue(p.Key, out e))
                {
                    var st = p.Create > 0 ? p.StartLocal : DateTime.Now;
                    e = new Episode { Kind = "app", Start = st, End = DateTime.Now, Open = true, App = AppOf(p), Agent = p.AppAgent, Target = p.AppName, Proc = p.Name, Pid = p.Pid,
                        Trigger = "started by " + (p.Parent != null ? p.Parent.Name + " (PID " + p.Parent.Pid + ")" : "?"), Detail = p.WinTitle != null ? "window: " + p.WinTitle : "no window (running in the background)" };
                    OpenAppEps[p.Key] = e; History.Insert(0, e);
                    if (!first) NewAppEps.Add(e);
                    Logger.Event(string.Format("APP     | OPENED | {0} opened {1} | {2} PID {3} | {4} | cmd: {5}", e.Agent, e.Target, p.Name, p.Pid, e.Trigger, Redactor.Clean(p.Cmd, 300)));
                }
                else
                {
                    e.End = DateTime.Now; e.Agent = p.AppAgent;
                    if (p.WinTitle != null) e.Detail = "window: " + p.WinTitle;
                }
            }
            AgentApps = apps;
            var gone = new List<string>();
            var live = new HashSet<string>(); foreach (var a in apps) live.Add(a.Key);
            foreach (var kv in OpenAppEps) if (!live.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (var k in gone)
            {
                var e = OpenAppEps[k]; OpenAppEps.Remove(k);
                if (OpenAppEps.ContainsValue(e)) continue;
                e.Open = false; e.End = DateTime.Now;
                Logger.Event(string.Format("APP     | CLOSED | {0} closed {1} | PID {2} | open {3:h:mm:ss tt} - {4:h:mm:ss tt} ET", e.Agent, e.Target, e.Pid, e.Start, e.End));
                try { Directory.CreateDirectory(Logger.Dir); File.AppendAllText(epFile, e.ToLine() + Environment.NewLine, Encoding.UTF8); } catch { }
            }
        }

        string TargetFor(string app)
        {
            // foreground window first (an existing app being driven), then the newest app this agent opened
            PInfo fg;
            if (FgPid > 0 && FgPid != selfPid && cache.TryGetValue(FgPid, out fg))
            {
                bool own = AppOf(fg) == app && !fg.AgentApp;
                bool shell = fg.Name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(FgTitle);
                if (!own && !shell && (fg.C == null || fg.AgentApp)) return (fg.AgentApp ? fg.AppName : AppNameOf(fg)) + (string.IsNullOrEmpty(FgTitle) ? "" : " (\"" + (FgTitle.Length > 50 ? FgTitle.Substring(0, 50) + "..." : FgTitle) + "\")");
            }
            PInfo best = null;
            foreach (var p in AgentApps) if (AppOf(p) == app && (best == null || p.Create > best.Create)) best = p;
            return best != null ? best.AppName : null;
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
                    e = new Episode { Start = p.LastActive, End = p.LastActive, Open = true, App = kv.Key, Agent = AgentShort(p), Target = TargetFor(kv.Key), Proc = (p.E != null && p.E.Friendly != null ? p.E.Friendly : p.Name) + " (" + p.Name + ")", Pid = p.Pid, Trigger = p.ActiveReason };
                    OpenEpisodes[kv.Key] = e; NewEpisodes.Add(e); History.Insert(0, e);
                    Logger.Event(string.Format("SCREEN  | START | {0} is controlling {1} | {2} PID {3} | {4}", e.Agent, e.Target ?? "the screen", e.Proc, e.Pid, e.Trigger));
                }
                foreach (var p in kv.Value)
                {
                    if (p.LastActive > e.End) e.End = p.LastActive;
                    string ag = AgentShort(p); if (!e.Agent.Contains(ag)) e.Agent += " + " + ag;
                }
                var tg = TargetFor(kv.Key); if (tg != null) e.Target = tg;
            }
            var closed = new List<string>();
            foreach (var kv in OpenEpisodes)
            {
                if (activeByApp.ContainsKey(kv.Key)) continue;
                DateTime busy = DateTime.MinValue;
                foreach (var a in AgentApps) if (AppOf(a) == kv.Key && a.LastBusy > busy) busy = a.LastBusy;
                if ((DateTime.Now - busy).TotalSeconds < 20) { if (busy > kv.Value.End) kv.Value.End = busy; continue; }
                closed.Add(kv.Key);
            }
            foreach (var k in closed)
            {
                var e = OpenEpisodes[k]; OpenEpisodes.Remove(k); e.Open = false;
                try { if (e.App == "OpenBot") e.Detail = LogPeek.OpenBot(e.Start, e.End); } catch { }
                Logger.Event(string.Format("SCREEN  | END   | {0} was controlling {1} | {2:h:mm:ss tt} - {3:h:mm:ss tt} ET | {4}", e.Agent, e.Target ?? "the screen", e.Start, e.End, e.Detail));
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
}
