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

    enum EyeMode { Idle, Nervous, Danger }
    enum Mood { Normal, Bored, Mad }

    // Mascot eyes: two soft black-shaded capsule slits on an accent face plate. No pupils, irises or whites -
    // every expression comes from the slit shape (height, width, tilt, lids) and where the slits sit.
    class Eyes
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

        public void Poke() { pokePending++; }

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

        public void PreviewMood(Mood m, double seconds) { forceMood = m; forceUntil = DateTime.Now.AddSeconds(seconds); forcePending = true; if (m != Mood.Bored) sleepStart = -1; }
        public void PreviewSleep() { PreviewMood(Mood.Bored, SleepLength + 3); sleepPending = true; }

        public void Recolor()
        {
            Color glow, tint; double tintK;
            if (Mode == EyeMode.Danger) { glow = Theme.Hex("#F43F5E"); tint = Theme.Hex("#E11D48"); tintK = 0.38; }
            else if (Mode == EyeMode.Nervous) { glow = Theme.Hex("#F59E0B"); tint = Theme.Hex("#F59E0B"); tintK = 0.3; }
            else if (CurrentMood == Mood.Mad) { glow = Theme.Hex("#F87171"); tint = Theme.Hex("#EF4444"); tintK = 0.28; }
            else { glow = Theme.Mix(Theme.Accent, Colors.White, 0.45); tint = Theme.Accent; tintK = 0; }
            Color pa = Theme.Accent, pb = Theme.Mix(Theme.Accent, Theme.Accent2, 0.6);
            if (Theme.IsLight) { pa = Theme.Mix(pa, Colors.White, 0.62); pb = Theme.Mix(pb, Colors.White, 0.5); } // keep the black slits readable on light presets
            Color a1 = Theme.Mix(pa, tint, tintK), a2 = Theme.Mix(pb, tint, tintK);
            var pg = new LinearGradientBrush(Theme.A(Theme.Mix(a1, Colors.White, 0.12), 240), Theme.A(Theme.Mix(a2, Colors.Black, 0.12), 240), 60); pg.Freeze();
            plate.Fill = pg;
            // black-shaded capsule: lit left edge, deep core; amber / red tint when a bot is active or in danger
            Color k0 = Theme.Hex("#3A3648"), k1 = Theme.Hex("#121019"), k2 = Theme.Hex("#040307");
            bool mad = Mode == EyeMode.Idle && CurrentMood == Mood.Mad;
            double bt = Mode == EyeMode.Nervous ? 0.42 : Mode == EyeMode.Danger ? 0.36 : mad ? 0.3 : 0;
            Color bc = Mode == EyeMode.Danger || mad ? Theme.Hex("#F43F5E") : Theme.Hex("#F59E0B");
            Color mid = Theme.Hex("#34303F"); // gentle, soft lighter middle (original intensity)
            var body = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            body.GradientStops.Add(new GradientStop(Theme.Mix(k2, bc, bt * 0.3), 0));
            body.GradientStops.Add(new GradientStop(Theme.Mix(k1, bc, bt * 0.55), 0.24));
            body.GradientStops.Add(new GradientStop(Theme.Mix(mid, bc, bt), 0.5));
            body.GradientStops.Add(new GradientStop(Theme.Mix(k1, bc, bt * 0.55), 0.76));
            body.GradientStops.Add(new GradientStop(Theme.Mix(k2, bc, bt * 0.3), 1));
            body.Freeze();
            var gb = new RadialGradientBrush(Theme.A(glow, 190), Theme.A(glow, 0)); gb.Freeze();
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
        public void Frame(double t)
        {
            double dt = lastT > 0 ? Math.Min(0.2, t - lastT) : 0.04; lastT = t;
            if (forcePending) { forcePending = false; if (forceMood == Mood.Mad) { madStart = t; lastMadAt = t; } }
            bool forced = DateTime.Now < forceUntil;
            while (pokePending > 0)
            {
                pokePending--; pokes.Add(t); pokes.RemoveAll(x => x < t - 4); pokeLevel = pokes.Count; pokeAt = t;
                if (Mode == EyeMode.Idle && pokeLevel >= 3) { madStart = t; lastMadAt = t; pokeMad = true; }
            }
            if (pokeMad && (madStart < 0 || t - madStart > MadLength)) pokeMad = false;
            bool poking = t - pokeAt < 3.5;

            N2.POINT cp; N2.GetCursorPos(out cp);
            bool movedMouse = cp.X != lastCx || cp.Y != lastCy;
            if (movedMouse)
            {
                if (lastCx != int.MinValue && !forced && !poking && (CurrentMood != Mood.Normal)) surpriseStart = t;
                cursorStill = 0; lastCx = cp.X; lastCy = cp.Y;
                if (!forced && !pokeMad) { madStart = -1; lastMadAt = -1e9; }
            }
            else cursorStill += dt;

            Mood mood;
            if (forced) mood = forceMood == Mood.Mad && madStart >= 0 && t - madStart > MadLength ? Mood.Bored : forceMood;
            else if (Mode != EyeMode.Idle) { mood = Mood.Normal; madStart = -1; pokeMad = false; }
            else if (pokeMad) mood = Mood.Mad;
            else
            {
                if (cursorStill >= MadAfter && t - lastMadAt >= MadAfter) { madStart = t; lastMadAt = t; }
                if (madStart >= 0 && t - madStart <= MadLength) mood = Mood.Mad;
                else mood = cursorStill >= BoredAfter ? Mood.Bored : Mood.Normal;
            }
            if (sleepPending) { sleepPending = false; sleepStart = t; }
            if (mood == Mood.Bored && lastMood != Mood.Bored && sleepStart < 0) nextSleep = t + Rand(12, 20);
            if (mood != Mood.Bored) sleepStart = -1;
            else if (sleepStart < 0 && nextSleep > 0 && t > nextSleep && !forced) sleepStart = t;
            if (sleepStart >= 0 && t - sleepStart >= SleepLength)
            {
                // snap awake: jolt, widen, quick double blink, then carry on being bored
                sleepStart = -1; nextSleep = t + Rand(25, 40); joltAt = t; surpriseStart = t;
                blinkStart = t + 0.4; doubleBlink = true; blinkDur = 0.1;
            }
            lastMood = mood;
            CurrentMood = mood;
            if (shownMode != Mode || shownMood != mood) Recolor();

            Point eyeScreen;
            try { eyeScreen = host.PointToScreen(new Point(HW / 2, HH / 2)); } catch { return; }
            double dx, dy, dist;
            if (Mode != EyeMode.Idle && Target.HasValue)
            {
                var r = Target.Value; dx = r.X + r.Width / 2 - eyeScreen.X; dy = r.Y + r.Height / 2 - eyeScreen.Y;
                dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 300); dx = dx / dist * k; dy = dy / dist * k; }
            }
            else if (Mode != EyeMode.Idle) { dx = 0.75; dy = 0.15; } // look at the facts to the right
            else if (Mode == EyeMode.Idle && t - pokeAt < 3.2)
            {
                dx = cp.X - eyeScreen.X; dy = cp.Y - eyeScreen.Y; dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 260); dx = dx / dist * k * 0.8; dy = dy / dist * k * 0.8; }
            }
            else if (mood == Mood.Bored)
            {
                if (t > nextLook) { nextLook = t + Rand(3, 6); tx = Rand(-0.7, 0.7); ty = Rand(0.15, 0.6); }
                dx = tx; dy = ty;
                if (sleepStart >= 0) { dx = tx * 0.3; dy = 0.7; }
            }
            else if (Follow && (cursorStill < 5 || mood == Mood.Mad))
            {
                dx = cp.X - eyeScreen.X; dy = cp.Y - eyeScreen.Y; dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist < 1) { dx = 0; dy = 0; } else { double k = Math.Min(1, dist / 420); dx = dx / dist * k; dy = dy / dist * k; }
                if (mood == Mood.Mad) { dx *= 0.4; dy *= 0.4; }
            }
            else
            {
                if (t > nextLook) { nextLook = t + Rand(1.4, 3.2); if (rnd.NextDouble() < 0.3) { tx = 0; ty = 0; } else { double a = Rand(0, Math.PI * 2), m = Rand(0.4, 1); tx = Math.Cos(a) * m; ty = Math.Sin(a) * m * 0.7; } }
                dx = tx; dy = ty;
            }
            if (Mode != EyeMode.Idle)
            {
                if (t > nextJit) { nextJit = t + (Mode == EyeMode.Danger ? Rand(0.12, 0.35) : Rand(0.18, 0.5)); jx = Rand(-0.14, 0.14); jy = Rand(-0.12, 0.12); }
                dx += jx; dy += jy;
            }
            double rate = Mode != EyeMode.Idle ? 16 : mood == Mood.Bored ? 1.4 : 9;
            gx += (dx * 22 - gx) * Math.Min(1, dt * rate);
            gy += (dy * 10 - gy) * Math.Min(1, dt * rate);

            // blinks: squash the slit to a line
            if (blinkStart < 0 && t > nextBlink && mood != Mood.Mad && sleepStart < 0)
            {
                blinkStart = t; doubleBlink = rnd.NextDouble() < (Mode == EyeMode.Nervous ? 0.45 : Mode == EyeMode.Danger ? 0.3 : 0.12);
                blinkDur = Mode == EyeMode.Nervous ? 0.12 : Mode == EyeMode.Danger ? 0.15 : mood == Mood.Bored ? 0.75 : 0.22;
            }
            double blink = 0;
            if (blinkStart >= 0)
            {
                double u = (t - blinkStart) / blinkDur;
                if (u >= 1)
                {
                    if (doubleBlink) { doubleBlink = false; blinkStart = t + 0.07; }
                    else
                    {
                        blinkStart = -1;
                        nextBlink = t + (Mode == EyeMode.Danger ? Rand(0.9, 2.2) : Mode == EyeMode.Nervous ? Rand(0.35, 1.3) : mood == Mood.Bored ? Rand(4, 8) : Rand(2.5, 7));
                    }
                }
                else if (u >= 0) blink = mood == Mood.Bored ? Math.Pow(Math.Sin(Math.PI * u), 0.7) : Math.Sin(Math.PI * u);
            }

            double sx = 1, sy = 1, rot = 0, to = 0, ti = 0, bo = 0, sk = 0, glowOp = 0.22, rsx = 0, rsy = 0;
            if (Mode == EyeMode.Danger) { sx = 1.2; sy = 1.16; double p = 0.5 + 0.5 * Math.Sin(t * 6); glowOp = 0.45 + 0.55 * p; }
            else if (Mode == EyeMode.Nervous) { sx = 0.9; sy = 0.94; to = ti = 0.06; glowOp = 0.55 + 0.15 * Math.Sin(t * 9); }
            else if (mood == Mood.Bored)
            {
                to = ti = 0.2 + 0.03 * Math.Sin(t * 0.9); sy = 0.62; glowOp = 0.1;
                if (sleepStart >= 0)
                {
                    double u = t - sleepStart;
                    if (u < 2.6) { double d = Ease(u / 2.6); to = ti = 0.2 + 0.25 * d; sy = 0.62 * (1 - 0.93 * d); }       // droop, then fully shut
                    else
                    {
                        double k = 1 - 0.88 * Ease((u - 2.6) / (SleepLength - 2.6));                           // drift off: shrink away
                        to = ti = 0.45; sy = 0.62 * 0.07 * k; sx = k; glowOp = 0.1 * k;
                    }
                }
            }
            else if (mood == Mood.Mad)
            {
                double u = t - madStart, a = Ease(u / 0.45) * (1 - Ease((u - (MadLength - 0.6)) / 0.6));
                // rage: very thin, sharp V, squinting hard
                rot = 24 * a; to = 0.08 * a; ti = 0.46 * a; sy = 1 - 0.3 * a; sx = 1 - 0.45 * a; glowOp = 0.25 + 0.45 * a;
                double su = u - 0.6;
                if (AllowShake && su >= 0 && su <= 1.0)
                {
                    double amp = 11 * Math.Pow(1 - su, 1.5);                                                 // the whole window shakes
                    rsx = (rnd.NextDouble() * 2 - 1) * amp; rsy = (rnd.NextDouble() * 2 - 1) * amp * 0.6;
                    sk = (1 - su) * 3 * Math.Sin(su * Math.PI * 2 * 14);
                }
            }
            else if (squintStart >= 0 || t > nextSquint)
            {
                if (squintStart < 0) squintStart = t;
                double u = (t - squintStart) / 1.6;
                if (u >= 1) { squintStart = -1; nextSquint = t + Rand(14, 40); }
                else { double q = 0.32 * Math.Sin(Math.PI * u); to = ti = q * 0.45; sy = 1 - 0.45 * q; }
            }
            // poke overlay: wince (squeeze shut + flinch), then a narrowed glare at the cursor
            double pu = t - pokeAt, recoil = 1, recoilY = 0;
            if (pu >= 0 && pu < 0.32)
            {
                double w = pu < 0.22 ? Math.Min(1, pu / 0.05) : 1 - (pu - 0.22) / 0.1;
                blink = Math.Max(blink, w); sx *= 1 + 0.18 * w; recoil = 1 - 0.08 * w; recoilY = -3 * w;
                to *= 1 - w; ti *= 1 - w; bo *= 1 - w;
            }
            else if (Mode == EyeMode.Idle && !pokeMad && pokeLevel < 3)
            {
                double end = pokeLevel >= 2 ? 3.1 : 2.5;
                if (pu >= 0.32 && pu < end)
                {
                    double g = Ease((pu - 0.32) / 0.18) * (1 - Ease((pu - (end - 0.45)) / 0.45));
                    if (pokeLevel >= 2) { rot = Math.Max(rot, 11 * g); ti = Math.Max(ti, 0.34 * g); to = Math.Max(to, 0.06 * g); sy *= 1 - 0.25 * g; }
                    else { ti = Math.Max(ti, 0.16 * g); to = Math.Max(to, 0.16 * g); sy *= 1 - 0.4 * g; }
                    sx *= 1 + 0.06 * g; glowOp = Math.Max(glowOp, 0.22 + 0.2 * g);
                }
            }
            double su2 = t - surpriseStart;
            if (su2 >= 0 && su2 < 0.5) { double k = Math.Pow(1 - su2 / 0.5, 2); sx *= 1 + 0.3 * k; sy *= 1 + 0.2 * k; to *= 1 - k; ti *= 1 - k; bo *= 1 - k; blink *= 1 - k; }
            sy *= 1 - 0.9 * blink;

            double targetScale = Mode == EyeMode.Danger ? 1.06 : 1;
            curScale += (targetScale - curScale) * Math.Min(1, dt * 6);
            double ju = t - joltAt;
            if (ju >= 0 && ju < 0.3) recoilY += -4 * (1 - ju / 0.3) * Math.Cos(ju * 40);
            RootShakeX = rsx; RootShakeY = rsy;
            double fs = curScale * recoil, shy = sk * 0.3 + recoilY;
            if (Math.Abs(scale.ScaleX - fs) > 0.002) { scale.ScaleX = fs; scale.ScaleY = fs; }
            if (Math.Abs(shake.X - sk) > 0.05 || Math.Abs(shake.Y - shy) > 0.05) { shake.X = sk; shake.Y = shy; }
            foreach (var e in eyes)
            {
                if (Math.Abs(e.T.X - gx) > 0.05 || Math.Abs(e.T.Y - gy) > 0.05) { e.T.X = gx; e.T.Y = gy; e.GT.X = gx; e.GT.Y = gy; }
                if (Math.Abs(e.S.ScaleX - sx) > 0.003) e.S.ScaleX = sx;
                if (Math.Abs(e.S.ScaleY - sy) > 0.003) e.S.ScaleY = sy;
                double r = rot * e.Side; if (Math.Abs(e.R.Angle - r) > 0.05) e.R.Angle = r;
                SetLids(e, to, ti, bo);
                if (Math.Abs(e.Glow.Opacity - glowOp) > 0.01) e.Glow.Opacity = glowOp;
            }
        }
    }
}
