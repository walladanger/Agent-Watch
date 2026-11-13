using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Web.Script.Serialization;

namespace AgentWatch
{
    static class Settings
    {
        static string FilePath = System.IO.Path.Combine(AppPaths.Dir, "settings.json");
        public static bool WindowOpen = true, ShowModelServers = false, ShowHelpers = false, ShowMcp = true, EyesFollow = true, Maximized = false, AllowShake = true;
        public static string Preset = "Indigo Night", Finish = "Satin", AccentHex = null, PanelHex = null, TextHex = null;
        public static double Transparency = 10, Blur = 0, W = 1360, H = 820, X = double.NaN, Y = double.NaN, DetW = 420, HistH = 190;
        public static HashSet<string> Expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static bool B(Dictionary<string, object> d, string k, bool def) { object o; return d.TryGetValue(k, out o) && o is bool ? (bool)o : def; }
        static string S(Dictionary<string, object> d, string k, string def) { object o; return d.TryGetValue(k, out o) && o is string && ((string)o).Length > 0 ? (string)o : def; }
        static double N(Dictionary<string, object> d, string k, double def)
        {
            object o; if (!d.TryGetValue(k, out o) || o == null) return def;
            try { return Convert.ToDouble(o, System.Globalization.CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(FilePath)) as Dictionary<string, object>;
                if (d == null) return;
                WindowOpen = B(d, "windowOpen", WindowOpen); ShowModelServers = B(d, "showModelServers", ShowModelServers);
                ShowHelpers = B(d, "showHelpers", ShowHelpers); ShowMcp = B(d, "showMcp", ShowMcp);
                EyesFollow = B(d, "eyesFollowMouse", EyesFollow); AllowShake = B(d, "allowShake", AllowShake); Maximized = B(d, "maximized", Maximized);
                Preset = S(d, "preset", Preset); Finish = S(d, "finish", Finish);
                if (Array.IndexOf(Theme.Finishes, Finish) < 0) Finish = "Matte"; // Vinyl / Metallic were removed
                AccentHex = S(d, "accent", null); PanelHex = S(d, "panel", null); TextHex = S(d, "text", null);
                Transparency = N(d, "transparency", Transparency); Blur = N(d, "blur", Blur);
                W = N(d, "width", W); H = N(d, "height", H); X = N(d, "left", X); Y = N(d, "top", Y); DetW = N(d, "detailsWidth", DetW); HistH = N(d, "historyHeight", HistH);
                object ex; if (d.TryGetValue("expanded", out ex) && ex is object[]) foreach (var o in (object[])ex) Expanded.Add(Convert.ToString(o));
            }
            catch { }
        }
        public static void Save()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["windowOpen"] = WindowOpen; d["showModelServers"] = ShowModelServers; d["showHelpers"] = ShowHelpers; d["showMcp"] = ShowMcp;
                d["preset"] = Preset; d["finish"] = Finish; d["accent"] = AccentHex ?? ""; d["panel"] = PanelHex ?? ""; d["text"] = TextHex ?? "";
                d["transparency"] = Math.Round(Transparency); d["blur"] = Math.Round(Blur); // old gradient / animateGradient / speed keys are no longer written (fixed subtle gradient + rare glint)
                d["eyesFollowMouse"] = EyesFollow; d["allowShake"] = AllowShake; d["maximized"] = Maximized;
                d["width"] = Math.Round(W); d["height"] = Math.Round(H); if (!double.IsNaN(X)) { d["left"] = Math.Round(X); d["top"] = Math.Round(Y); }
                d["detailsWidth"] = Math.Round(DetW); d["historyHeight"] = Math.Round(HistH);
                d["expanded"] = new List<string>(Expanded).ToArray();
                string js = new JavaScriptSerializer().Serialize(d);
                // light pretty-print: one key per line
                js = js.Replace(",\"", ",\r\n  \"").Replace("{\"", "{\r\n  \"").TrimEnd('}') + "\r\n}\r\n";
                File.WriteAllText(FilePath, js);
            }
            catch { }
        }
    }

    class Preset
    {
        public string Name; public Color Bg1, Bg2, Bg3, Panel, Accent, Accent2, Text;
        public Preset(string n, string b1, string b2, string b3, string p, string a, string a2, string t)
        { Name = n; Bg1 = Theme.Hex(b1); Bg2 = Theme.Hex(b2); Bg3 = Theme.Hex(b3); Panel = Theme.Hex(p); Accent = Theme.Hex(a); Accent2 = Theme.Hex(a2); Text = Theme.Hex(t); }
    }

    static class Theme
    {
        public static readonly string[] Finishes = { "Matte", "Glossy", "Satin", "Glass" };
        public static readonly List<Preset> Presets = new List<Preset> {
            new Preset("Indigo Night", "#1E1B4B", "#312E81", "#4C1D95", "#2A2670", "#818CF8", "#C084FC", "#E0E7FF"),
            new Preset("Synthwave",    "#2D1150", "#6A1B6E", "#9D2B5F", "#3A1657", "#FF4FD8", "#FFB347", "#FFE9F7"),
            new Preset("Aurora",       "#0E3346", "#16605F", "#4B2A7A", "#143E52", "#5EEAD4", "#A78BFA", "#E6FFFA"),
            new Preset("Neon Cyber",   "#101A3F", "#1B2D6B", "#3A0E5E", "#17245A", "#22F3FF", "#FF3DF2", "#E8FDFF"),
            new Preset("Ocean Deep",   "#0C2A4D", "#114B7A", "#0E5E6F", "#133A63", "#38BDF8", "#2DD4BF", "#E0F2FE"),
            new Preset("Ember",        "#3A1410", "#6B2414", "#7A3B0C", "#4A1C14", "#FB923C", "#F43F5E", "#FFEDD5"),
            new Preset("Forest Glow",  "#0F2E22", "#1A4D33", "#2F5A1E", "#163B2B", "#4ADE80", "#FACC15", "#ECFDF5"),
            new Preset("Vaporwave",    "#2A1B5E", "#5B2A86", "#1B6B8A", "#35236E", "#FF71CE", "#01CDFE", "#FFF0FB"),
            new Preset("Midnight Gold","#1A1A40", "#2A2560", "#4A3A1A", "#24224F", "#FBBF24", "#F59E0B", "#FEF3C7"),
            new Preset("Ultraviolet",  "#2A0E5C", "#4C1D95", "#6D28D9", "#3B1679", "#C084FC", "#F0ABFC", "#F5F3FF"),
            new Preset("Toxic Lime",   "#142A12", "#23401A", "#2E3A0E", "#1C3518", "#A3E635", "#D9F99D", "#F7FEE7"),
            new Preset("Rose Quartz",  "#3B1630", "#5E2248", "#6B3358", "#4A1D3C", "#F9A8D4", "#FDA4AF", "#FFF1F2"),
            // monochrome
            new Preset("Pure Black",   "#000000", "#080808", "#121212", "#0C0C0C", "#E5E5E5", "#A3A3A3", "#F5F5F5"),
            new Preset("Charcoal",     "#18181B", "#222226", "#2C2C31", "#232327", "#D4D4D8", "#A1A1AA", "#F4F4F5"),
            new Preset("Graphite",     "#26282C", "#30333A", "#3B3F46", "#2E3136", "#C7CCD4", "#9BA3AE", "#F3F4F6"),
            new Preset("Slate Gray",   "#1E293B", "#2B374B", "#3A475D", "#273449", "#CBD5E1", "#94A3B8", "#F1F5F9"),
            new Preset("Silver",       "#A3A9B3", "#B9BEC6", "#CDD1D7", "#C9CDD4", "#2F3742", "#4B5563", "#0F172A"),
            new Preset("Light Gray",   "#D6D6DA", "#E2E2E6", "#ECECEF", "#E9E9EC", "#3F3F46", "#52525B", "#18181B"),
            new Preset("Paper White",  "#F3F2EF", "#F8F7F5", "#FFFFFF", "#FFFFFF", "#44403C", "#78716C", "#1C1917"),
        };
        public static Preset Current;
        public static Color Accent, Accent2, Text, Panel, Bg1, Bg2, Bg3;
        public static bool IsLight; // light presets: dark text, dark-tinted hovers
        public static double Lum(Color c) { return (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0; }
        public static LinearGradientBrush Grad;

        public static Color Hex(string s)
        {
            try { var c = (Color)ColorConverter.ConvertFromString(s.StartsWith("#") ? s : "#" + s); return c; } catch { return Colors.MediumSlateBlue; }
        }
        public static string ToHex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
        public static Color A(Color c, int a) { return Color.FromArgb((byte)Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }
        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((byte)(a.A + (b.A - a.A) * t), (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        }
        static SolidColorBrush SB(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        public static Preset Find(string name) { foreach (var p in Presets) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p; return Presets[0]; }

        // 0 = solid, 2 = Mica, 3 = Acrylic
        public static int BackdropType { get { return Settings.Blur < 1 ? 0 : Settings.Blur < 50 ? 2 : 3; } }

        static ImageBrush MakeNoise(int w, int h, bool streaks)
        {
            var px = new byte[w * h * 4]; var r = new Random(7);
            double[] row = new double[h]; for (int y = 0; y < h; y++) row[y] = r.NextDouble();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    double v; int a;
                    if (streaks) { v = row[y] * 0.8 + r.NextDouble() * 0.2; a = (int)(10 + v * 22); }
                    else { v = r.NextDouble(); a = (int)(r.NextDouble() * 26); }
                    byte g = (byte)(v > 0.5 ? 255 : 0); // light or dark speck (dark is premultiplied => just alpha)
                    int i = (y * w + x) * 4;
                    byte pa = (byte)a; byte pc = (byte)(g * a / 255);
                    px[i] = pc; px[i + 1] = pc; px[i + 2] = pc; px[i + 3] = pa;
                }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4); bmp.Freeze();
            var ib = new ImageBrush(bmp) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, w, h), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
            ib.Freeze(); return ib;
        }

        static LinearGradientBrush Lin(Point a, Point b, params object[] stops)
        {
            var g = new LinearGradientBrush { StartPoint = a, EndPoint = b };
            for (int i = 0; i < stops.Length; i += 2) g.GradientStops.Add(new GradientStop(Hex((string)stops[i + 1]), (double)stops[i]));
            g.Freeze(); return g;
        }

        public static void Apply(ResourceDictionary R)
        {
            var p = Find(Settings.Preset); Current = p;
            Accent = Settings.AccentHex != null ? Hex(Settings.AccentHex) : p.Accent;
            Panel = Settings.PanelHex != null ? Hex(Settings.PanelHex) : p.Panel;
            Text = Settings.TextHex != null ? Hex(Settings.TextHex) : p.Text;
            Accent2 = p.Accent2; Bg1 = p.Bg1; Bg2 = p.Bg2; Bg3 = p.Bg3;
            // keep panels from going near-black: lift very dark custom panel colours
            double lum = (0.2126 * Panel.R + 0.7152 * Panel.G + 0.0722 * Panel.B) / 255.0;
            if (lum < 0.07 && Settings.PanelHex != null) Panel = Mix(Panel, Accent, 0.18);
            IsLight = Lum(Text) < 0.4;
            Color ov = IsLight ? Colors.Black : Colors.White;
            string f = Settings.Finish;
            bool backdrop = BackdropType != 0;
            double t = Math.Max(0, Math.Min(100, Settings.Transparency));
            int panelA = (int)(250 - t * 1.25); if (f == "Glass") panelA = (int)(panelA * 0.7); panelA = Math.Max(f == "Glass" ? 95 : 125, panelA);
            int baseA = backdrop ? (int)Math.Max(70, 255 - t * 1.85) : 255;

            R["Accent"] = SB(Accent); R["Accent2"] = SB(Accent2); R["AccentSoft"] = SB(A(Accent, 0x38));
            R["Text"] = SB(Text); R["Dim"] = SB(A(Mix(Text, Accent, 0.38), 0xE0));
            R["PanelBrush"] = SB(A(Panel, panelA)); R["PanelStrong"] = SB(Mix(Panel, Bg1, 0.25));
            R["Line"] = SB(!IsLight && (f == "Glass" || f == "Glossy") ? Color.FromArgb(0x55, 255, 255, 255) : A(Mix(Accent, Text, 0.3), IsLight ? 0x50 : 0x44));
            R["Hover"] = SB(A(ov, IsLight ? 0x12 : 0x1C)); R["Sel"] = SB(A(Accent, IsLight ? 0x30 : 0x45));
            R["Soft"] = SB(A(ov, IsLight ? 0x10 : 0x18)); R["Soft2"] = SB(A(ov, IsLight ? 0x1E : 0x2C));
            R["BaseFill"] = SB(A(Bg1, baseA));

            Brush tex = Brushes.Transparent, sheen = Brushes.Transparent;
            switch (f)
            {
                case "Glossy":
                    {
                        // soft diagonal gloss (~30 degrees, top-left to lower-right), absolute so wide cards keep the angle
                        var gl = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(520, 300) };
                        gl.GradientStops.Add(new GradientStop(Hex("#16FFFFFF"), 0)); gl.GradientStops.Add(new GradientStop(Hex("#0AFFFFFF"), 0.22));
                        gl.GradientStops.Add(new GradientStop(Hex("#03FFFFFF"), 0.45)); gl.GradientStops.Add(new GradientStop(Hex("#00FFFFFF"), 0.7));
                        gl.Freeze(); sheen = gl; break;
                    }
                case "Satin": sheen = Lin(new Point(0, 0), new Point(0.3, 1), 0.0, "#16FFFFFF", 0.55, "#05FFFFFF", 1.0, "#00FFFFFF"); break;
                case "Glass": sheen = Lin(new Point(0, 0), new Point(1, 1), 0.0, "#26FFFFFF", 0.35, "#08FFFFFF", 1.0, "#04FFFFFF"); break;
            }
            R["CardTex"] = tex; R["CardSheen"] = sheen;

            Grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            Grad.GradientStops.Add(new GradientStop(Bg1, 0)); Grad.GradientStops.Add(new GradientStop(Bg2, 0.45));
            Grad.GradientStops.Add(new GradientStop(Bg3, 0.8)); Grad.GradientStops.Add(new GradientStop(Mix(Bg3, Accent, 0.25), 1));
            // used directly by the window (a dictionary copy would be frozen and could not drift)
        }

    }

    static class N2
    {
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
        [DllImport("iphlpapi.dll")] public static extern uint GetExtendedTcpTable(IntPtr tbl, ref int size, bool order, int af, int cls, int reserved);

        public static void Dwm(IntPtr h, int attr, int v) { try { DwmSetWindowAttribute(h, attr, ref v, 4); } catch { } }

        // top-level visible titled windows per pid
        public static Dictionary<int, List<IntPtr>> WindowsByPid()
        {
            var d = new Dictionary<int, List<IntPtr>>();
            Native.EnumWindows(delegate (IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, 4) != IntPtr.Zero) return true; // owned
                if (Native.GetWindowTextLengthW(h) <= 0) return true;
                uint pid; Native.GetWindowThreadProcessId(h, out pid);
                List<IntPtr> l2; if (!d.TryGetValue((int)pid, out l2)) { l2 = new List<IntPtr>(); d[(int)pid] = l2; }
                l2.Add(h); return true;
            }, IntPtr.Zero);
            return d;
        }

        public static void Front(IntPtr h)
        {
            if (h == IntPtr.Zero) return;
            if (IsIconic(h)) ShowWindow(h, 9);
            Native.SetForegroundWindow(h); BringWindowToTop(h);
        }

        public static Rect? ScreenRect(IntPtr h)
        {
            RECT r; if (h == IntPtr.Zero || !GetWindowRect(h, out r) || IsIconic(h)) return null;
            return new Rect(r.L, r.T, Math.Max(1, r.R - r.L), Math.Max(1, r.B - r.T));
        }

        static string Ip(uint a) { return string.Format("{0}.{1}.{2}.{3}", a & 255, (a >> 8) & 255, (a >> 16) & 255, (a >> 24) & 255); }
        static int Port(uint p) { return (int)(((p & 255) << 8) | ((p >> 8) & 255)); }
        // IPv4 TCP connections for a set of pids: "established remote endpoints", count of listeners
        public static List<string> Tcp(HashSet<int> pids, out int listening, out int total)
        {
            listening = 0; total = 0; var res = new List<string>();
            int size = 0; GetExtendedTcpTable(IntPtr.Zero, ref size, true, 2, 5, 0);
            if (size <= 0) return res;
            IntPtr buf = Marshal.AllocHGlobal(size + 4096); size += 4096;
            try
            {
                if (GetExtendedTcpTable(buf, ref size, true, 2, 5, 0) != 0) return res;
                int n = Marshal.ReadInt32(buf);
                for (int i = 0; i < n; i++)
                {
                    IntPtr row = buf + 4 + i * 24;
                    uint state = (uint)Marshal.ReadInt32(row), la = (uint)Marshal.ReadInt32(row, 4), lp = (uint)Marshal.ReadInt32(row, 8), ra = (uint)Marshal.ReadInt32(row, 12), rp = (uint)Marshal.ReadInt32(row, 16);
                    int pid = Marshal.ReadInt32(row, 20);
                    if (!pids.Contains(pid)) continue;
                    total++;
                    if (state == 2) { listening++; continue; }
                    if (state == 5 && res.Count < 12) { string s = Ip(ra) + ":" + Port(rp); if (!res.Contains(s)) res.Add(s); }
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
            return res;
        }
    }

    class VM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        Dictionary<string, object> v = new Dictionary<string, object>();
        object G2(string k) { object o; return v.TryGetValue(k, out o) ? o : null; }
        void S2(string k, object val)
        {
            object o; if (v.TryGetValue(k, out o) && Equals(o, val)) return;
            v[k] = val; var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(k));
        }
        public string Key;
        public GroupInfo G; public PInfo P; public Episode Ep; public DangerHit Hit; public bool IsApp, HasKids, Expanded;
        public string Name { get { return (string)G2("Name"); } set { S2("Name", value); } }
        public string Sub { get { return (string)G2("Sub"); } set { S2("Sub", value); } }
        public string Doing { get { return (string)G2("Doing"); } set { S2("Doing", value); } }
        public string Pill { get { return (string)G2("Pill"); } set { S2("Pill", value); } }
        public string Pid { get { return (string)G2("Pid"); } set { S2("Pid", value); } }
        public string Cpu { get { return (string)G2("Cpu"); } set { S2("Cpu", value); } }
        public string Ram { get { return (string)G2("Ram"); } set { S2("Ram", value); } }
        public string Glyph { get { return (string)G2("Glyph"); } set { S2("Glyph", value); } }
        public string Tip { get { return (string)G2("Tip"); } set { S2("Tip", value); } }
        public Brush PillBack { get { return (Brush)G2("PillBack"); } set { S2("PillBack", value); } }
        public Brush PillFore { get { return (Brush)G2("PillFore"); } set { S2("PillFore", value); } }
        public Brush Dot { get { return (Brush)G2("Dot"); } set { S2("Dot", value); } }
        public Brush RowBack { get { return (Brush)G2("RowBack"); } set { S2("RowBack", value); } }
        public Brush Stripe { get { return (Brush)G2("Stripe"); } set { S2("Stripe", value); } }
        public Brush DoingFore { get { return (Brush)G2("DoingFore"); } set { S2("DoingFore", value); } }
        public Visibility DotVis { get { var o = G2("DotVis"); return o == null ? Visibility.Collapsed : (Visibility)o; } set { S2("DotVis", value); } }
        public FontWeight Weight { get { var o = G2("Weight"); return o == null ? FontWeights.Normal : (FontWeight)o; } set { S2("Weight", value); } }
        public FontWeight DoingWeight { get { var o = G2("DoingWeight"); return o == null ? FontWeights.Normal : (FontWeight)o; } set { S2("DoingWeight", value); } }
        public double IndentW { get { var o = G2("IndentW"); return o == null ? 0 : (double)o; } set { S2("IndentW", value); } }
        public double NameSize { get { var o = G2("NameSize"); return o == null ? 13.0 : (double)o; } set { S2("NameSize", value); } }
        public double RowH { get { var o = G2("RowH"); return o == null ? 30.0 : (double)o; } set { S2("RowH", value); } }
    }

    class DangerHit
    {
        public DangerRule Rule; public string Title, Why, Todo, Snippet, App, Agent, ProcName, Window, Key; public int Pid; public long Create; public DateTime When; public bool Dismissed, Test; public IntPtr Hwnd;
        public PInfo P;
    }

    class DangerWatch
    {
        HashSet<string> seen = new HashSet<string>();
        Dictionary<string, DateTime> winSeen = new Dictionary<string, DateTime>();
        public List<DangerHit> Hits = new List<DangerHit>();
        public List<DangerHit> New = new List<DangerHit>();

        static string Snip(string cmd, Match m)
        {
            string c = cmd ?? ""; int s = Math.Max(0, m.Index - 60); int e = Math.Min(c.Length, m.Index + m.Length + 80);
            return (s > 0 ? "..." : "") + Redactor.Clean(c.Substring(s, e - s), 300) + (e < c.Length ? "..." : "");
        }

        public void Check(Engine eng)
        {
            New.Clear();
            var rules = eng.X.Dangers; if (rules.Count == 0) return;
            foreach (var p in eng.All.Values)
            {
                if (p.Root == null) continue;
                if (p.Cmd == null && !p.CmdTried) continue;
                string k = p.Key + "|" + (p.Cmd != null ? p.Cmd.Length : -1);
                if (seen.Contains(k)) continue; seen.Add(k);
                foreach (var r in rules)
                {
                    if (r.Kind == "window") continue;
                    Match m = null;
                    if (r.Name != null && !r.Name.IsMatch(p.Name ?? "")) continue;
                    if (r.Match != null) { if (p.Cmd == null) continue; m = r.Match.Match(p.Cmd); if (!m.Success) continue; }
                    var h = new DangerHit { Rule = r, Title = r.Title ?? r.Id, Why = r.Why, Todo = r.Todo, P = p, Pid = p.Pid, Create = p.Create, ProcName = p.Name,
                        App = Engine.AppOf(p), Agent = eng.AgentShort(p), When = DateTime.Now, Key = r.Id + "|" + p.Key,
                        Snippet = m != null ? Snip(p.Cmd, m) : (p.Path ?? p.Name) };
                    Add(h); break;
                }
            }
            if (seen.Count > 20000) seen.Clear();
            // window rule: a screen helper is active and a sensitive window is in front
            if (eng.OpenEpisodes.Count > 0 && !string.IsNullOrEmpty(eng.FgTitle))
                foreach (var r in rules)
                {
                    if (r.Kind != "window" || r.Match == null) continue;
                    var m = r.Match.Match(eng.FgTitle); if (!m.Success) continue;
                    string k = r.Id + "|" + eng.FgPid + "|" + eng.FgTitle;
                    DateTime t; if (winSeen.TryGetValue(k, out t) && (DateTime.Now - t).TotalMinutes < 10) continue;
                    winSeen[k] = DateTime.Now;
                    Episode ep = null; foreach (var e in eng.OpenEpisodes.Values) { ep = e; break; }
                    PInfo fp; eng.All.TryGetValue(eng.FgPid, out fp);
                    Add(new DangerHit { Rule = r, Title = r.Title, Why = r.Why, Todo = r.Todo, Pid = ep != null ? ep.Pid : 0, ProcName = ep != null ? ep.Proc : "", App = ep != null ? ep.App : "?",
                        Agent = ep != null ? ep.Agent : "?", When = DateTime.Now, Key = k, Window = eng.FgTitle, Snippet = "Window in front: \"" + eng.FgTitle + "\"" + (fp != null ? " (" + fp.Name + ")" : "") });
                }
        }

        void Add(DangerHit h)
        {
            Hits.Insert(0, h); New.Add(h); if (Hits.Count > 200) Hits.RemoveAt(Hits.Count - 1);
            Logger.Event(string.Format("DANGER  | {0} | {1} | {2} > {3} PID {4} | {5}", h.Rule != null ? h.Rule.Id : "test", h.Title, h.Agent, h.ProcName, h.Pid, h.Snippet));
        }

        public void AddTest(DangerHit h) { h.Test = true; Hits.Insert(0, h); }

        // newest undismissed hit that is still relevant
        public DangerHit Active(Engine eng)
        {
            foreach (var h in Hits)
            {
                if (h.Dismissed) continue;
                if (h.Test) return h;
                if ((DateTime.Now - h.When).TotalMinutes > 30) continue;
                if (h.P != null) { PInfo p; if (!eng.All.TryGetValue(h.Pid, out p) || p.Create != h.Create) { if ((DateTime.Now - h.When).TotalSeconds > 90) continue; } }
                else if (h.Window != null && (DateTime.Now - h.When).TotalMinutes > 3) continue;
                return h;
            }
            return null;
        }
    }

    // The mascot eyes live in ..\Eyes\ (EyesControl.cs, EyeMoods.cs, EyeIcon.cs).
}
