// AI usage desktop widget (WPF, .NET Framework 4.x, C# 5).
// Claude: reads claude.json written by AiUsageStatus.exe (Claude Code statusLine).
// Antigravity: runs the official `agy -p /usage` every 5 minutes.
// Neither path reads or refreshes login tokens.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using IOPath = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace AiUsageWidget
{
    // NA: the plan has no such window (e.g. no 5-hour limit), so the row is not drawn.
    class Metric { public double Remaining = -1; public DateTime? ResetsAt; public bool NA; }
    class Group { public string Name; public Metric FiveHour = new Metric(); public Metric Weekly = new Metric(); }

    static class Paths
    {
        public static readonly string Dir = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageWidget");
        public static readonly string ClaudeJson = IOPath.Combine(Dir, "claude.json");
        public static readonly string ClaudeAppJson = IOPath.Combine(Dir, "claude-app.json");
        public static readonly string AgyCache = IOPath.Combine(Dir, "antigravity.txt");
        public static readonly string CodexCache = IOPath.Combine(Dir, "codex.txt");
        public static readonly string CodexIcon = IOPath.Combine(Dir, "icons", "codex.png");

        // Codex CLI from npm first, then the codex.exe bundled with the running Codex desktop app.
        public static string CodexExe()
        {
            var npm = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules", "@openai", "codex", "node_modules", "@openai", "codex-win32-x64",
                "vendor", "x86_64-pc-windows-msvc", "bin", "codex.exe");
            if (File.Exists(npm)) return npm;
            try
            {
                foreach (var pr in Process.GetProcessesByName("ChatGPT"))
                {
                    var f = IOPath.Combine(IOPath.GetDirectoryName(pr.MainModule.FileName), "resources", "codex.exe");
                    if (File.Exists(f)) return f;
                }
            }
            catch { }
            return null;
        }
        public static readonly string Settings = IOPath.Combine(Dir, "settings.txt");
        public static readonly string ClaudeIcon = IOPath.Combine(Dir, "icons", "claude.png");
        public static readonly string AgyExe = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin", "agy.exe");
        public static readonly string AntigravityExe = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe");
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // A tiny window doesn't need a GPU device; software rendering keeps memory low.
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            Directory.CreateDirectory(Paths.Dir);

            // --snapshot <png> [bar|ring]: render the widget to an image and exit (for previews).
            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                WidgetWindow.Snapshot(args[1], args.Length >= 3 ? args[2] : "bar");
                return;
            }

            bool created;
            using (var mx = new Mutex(true, "Local\\AiUsageWidget_Single", out created))
            {
                if (!created) return;
                var app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                app.Run(new WidgetWindow());
            }
        }
    }

    static class Data
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public static DateTime FromEpoch(double s) { return Epoch.AddSeconds(s); }
        public static long ToEpoch(DateTime t) { return (long)(t - Epoch).TotalSeconds; }
        static double ToD(object o) { return Convert.ToDouble(o, CultureInfo.InvariantCulture); }

        public static Group LoadClaude(out DateTime? captured)
        {
            captured = null;
            var g = new Group { Name = "Claude" };
            try
            {
                if (!File.Exists(Paths.ClaudeJson)) return g;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Paths.ClaudeJson));
                captured = FromEpoch(ToD(d["captured_at"]));
                var rl = d["rate_limits"] as Dictionary<string, object>;
                ReadWindow(rl, "five_hour", g.FiveHour);
                ReadWindow(rl, "seven_day", g.Weekly);
            }
            catch { }
            return g;
        }

        // claude-app.json: the Claude desktop app's plan usage (get_usage result), written by a scheduled task.
        public static Group LoadClaudeApp(out DateTime? captured)
        {
            captured = null;
            var g = new Group { Name = "Claude" };
            try
            {
                if (!File.Exists(Paths.ClaudeAppJson)) return g;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Paths.ClaudeAppJson));
                object po;
                var plan = d.TryGetValue("plan", out po) ? po as Dictionary<string, object> : d;
                object st;
                string status = plan != null && plan.TryGetValue("status", out st) && st != null ? st.ToString() : "ok";
                if (plan == null || status == "unavailable") return g;
                if (status == "not_applicable")
                {
                    // API key, Bedrock, Vertex or a gateway: plan limits don't apply.
                    g.FiveHour.NA = g.Weekly.NA = true;
                    captured = File.GetLastWriteTimeUtc(Paths.ClaudeAppJson);
                    return g;
                }
                if (status != "ok") return g;

                bool five = false, week = false, weekIsAll = false;
                object wl;
                var windows = plan.TryGetValue("windows", out wl) ? wl as System.Collections.IEnumerable : null;
                if (windows != null)
                    foreach (var wo in windows)
                    {
                        var w = wo as Dictionary<string, object>;
                        if (w == null) continue;
                        object lo;
                        string label = w.TryGetValue("label", out lo) && lo != null ? lo.ToString().ToLowerInvariant() : "";
                        Metric m = null;
                        if (label.Contains("5-hour") || label.Contains("5 hour")) { m = g.FiveHour; five = true; }
                        else if (label.Contains("weekly"))
                        {
                            // Prefer the all-models weekly window over per-model ones (e.g. "Weekly · Opus").
                            bool all = label.Contains("all");
                            if (week && (weekIsAll || !all)) continue;
                            m = g.Weekly; week = true; weekIsAll = all;
                        }
                        if (m == null) continue;
                        object pu;
                        if (!w.TryGetValue("percentUsed", out pu) || pu == null) continue;
                        m.Remaining = Math.Max(0, 100 - ToD(pu));
                        DateTime t;
                        object ro;
                        m.ResetsAt = null;
                        if (w.TryGetValue("resetsAt", out ro) && ro != null &&
                            DateTime.TryParse(ro.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                            m.ResetsAt = t;
                    }
                // A window the plan doesn't have isn't listed: don't draw it.
                if (!five) g.FiveHour.NA = true;
                if (!week) g.Weekly.NA = true;
                captured = File.GetLastWriteTimeUtc(Paths.ClaudeAppJson);
            }
            catch { }
            return g;
        }

        static void ReadWindow(Dictionary<string, object> rl, string key, Metric m)
        {
            if (rl == null) return;
            object o;
            // Claude Code drops a window once it has reset, so a missing window means it is full.
            if (!rl.TryGetValue(key, out o) || o == null) { m.Remaining = 100; return; }
            var w = o as Dictionary<string, object>;
            if (w == null) return;
            object u, r;
            if (w.TryGetValue("used_percentage", out u) && u != null) m.Remaining = Math.Max(0, 100 - ToD(u));
            if (w.TryGetValue("resets_at", out r) && r != null) m.ResetsAt = FromEpoch(ToD(r));
        }

        public static List<Group> ParseAgy(string text)
        {
            var map = new Dictionary<string, Group>();
            var order = new List<Group>();
            foreach (var raw in text.Split('\n'))
            {
                var p = raw.Trim().Split('\t');
                if (p.Length < 4) continue;
                Metric m = null;
                if (p[1].IndexOf("Five", StringComparison.OrdinalIgnoreCase) >= 0) m = new Metric();
                else if (p[1].IndexOf("Week", StringComparison.OrdinalIgnoreCase) >= 0) m = new Metric();
                if (m == null) continue;
                double v;
                if (!double.TryParse(p[2].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
                m.Remaining = v;
                DateTime t;
                if (DateTime.TryParse(p[3].Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) m.ResetsAt = t;

                Group g;
                if (!map.TryGetValue(p[0], out g)) { g = new Group { Name = ShortName(p[0]) }; map[p[0]] = g; order.Add(g); }
                if (p[1].IndexOf("Five", StringComparison.OrdinalIgnoreCase) >= 0) g.FiveHour = m; else g.Weekly = m;
            }
            // A window the plan doesn't report isn't drawn.
            foreach (var g in order)
            {
                if (g.FiveHour.Remaining < 0) g.FiveHour.NA = true;
                if (g.Weekly.Remaining < 0) g.Weekly.NA = true;
            }
            return order;
        }

        static string ShortName(string s)
        {
            if (s.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini";
            if (s.StartsWith("Claude", StringComparison.OrdinalIgnoreCase)) return "Claude · GPT";
            return s;
        }

        // Codex: ask `codex app-server` (official JSON-RPC over stdio) for account/rateLimits/read.
        // Codex handles its own login; no model is called.
        public static string RunCodex()
        {
            var exe = Paths.CodexExe();
            if (exe == null) return null;
            var psi = new ProcessStartInfo(exe, "app-server")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Paths.Dir
            };
            using (var p = Process.Start(psi))
            {
                p.StandardInput.NewLine = "\n";
                p.StandardError.ReadToEndAsync();
                string result = null;
                var done = new ManualResetEvent(false);
                var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var reader = new Thread(() =>
                {
                    try
                    {
                        string line;
                        while ((line = p.StandardOutput.ReadLine()) != null)
                        {
                            Dictionary<string, object> m;
                            try { m = ser.Deserialize<Dictionary<string, object>>(line); } catch { continue; }
                            object id;
                            if (m == null || !m.TryGetValue("id", out id) || id == null) continue;
                            if (id.ToString() == "1")
                            {
                                Send(p, "{\"method\":\"initialized\"}");
                                Send(p, "{\"method\":\"account/rateLimits/read\",\"id\":2}");
                            }
                            else if (id.ToString() == "2")
                            {
                                object r;
                                if (m.TryGetValue("result", out r) && r != null) result = ser.Serialize(r);
                                break;
                            }
                        }
                    }
                    catch { }
                    done.Set();
                }) { IsBackground = true };
                reader.Start();
                Send(p, "{\"method\":\"initialize\",\"id\":1,\"params\":{\"clientInfo\":{\"name\":\"ai-usage-widget\",\"version\":\"1.0\"}}}");
                done.WaitOne(30000);
                try { p.StandardInput.Close(); } catch { }
                try { if (!p.WaitForExit(3000)) p.Kill(); } catch { }
                return result;
            }
        }

        static void Send(Process p, string s) { p.StandardInput.WriteLine(s); p.StandardInput.Flush(); }

        public static Group ParseCodex(string json)
        {
            var g = new Group { Name = "Codex" };
            var d = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(json);
            object rlo;
            if (!d.TryGetValue("rateLimits", out rlo)) return null;
            var rl = rlo as Dictionary<string, object>;
            // No rate limits at all (e.g. credit-based or unlimited plans): nothing to draw.
            if (rl == null) { g.FiveHour.NA = g.Weekly.NA = true; return g; }
            bool five = false, week = false;
            foreach (var key in new[] { "primary", "secondary" })
            {
                object o;
                var w = rl.TryGetValue(key, out o) ? o as Dictionary<string, object> : null;
                if (w == null) continue;
                object mins;
                double win = w.TryGetValue("windowDurationMins", out mins) && mins != null ? ToD(mins) : (key == "primary" ? 300 : 10080);
                Metric m = win <= 24 * 60 ? g.FiveHour : g.Weekly;
                if (m == g.FiveHour) five = true; else week = true;
                m.Remaining = Math.Max(0, 100 - ToD(w["usedPercent"]));
                object ra;
                if (w.TryGetValue("resetsAt", out ra) && ra != null) m.ResetsAt = FromEpoch(ToD(ra));
            }
            if (!five) g.FiveHour.NA = true;
            if (!week) g.Weekly.NA = true;
            return g;
        }

        public static string RunAgy()
        {
            if (!File.Exists(Paths.AgyExe)) return null;
            var psi = new ProcessStartInfo(Paths.AgyExe, "-p /usage")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Paths.Dir
            };
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(45000)) { try { p.Kill(); } catch { } return null; }
                return so.Result;
            }
        }
    }

    class WidgetWindow : Window
    {
        static Color C(string hex) { return (Color)ColorConverter.ConvertFromString(hex); }
        static readonly Color CText = C("#1F1E1C"), CLabel = C("#6B6962"), CMuted = C("#8A8780"), CTrack = C("#F1EFE8"),
            CSep = C("#EFEDE7"), CClaude = C("#D97757"), CAgy = C("#4285F4"), CCodex = C("#10A37F"), CWarn = C("#EF9F27"), CDanger = C("#E24B4A"),
            CBtn = C("#F4F3EF"), CBtnHover = C("#E9E7E0");
        static Brush B(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

        const double W = 264;
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        string mode = "bar";
        double? savedLeft, savedTop;
        bool loaded;

        Group claude = new Group { Name = "Claude" };
        DateTime? claudeAt;
        DateTime claudeMtime;
        List<Group> agy = new List<Group>();
        DateTime? agyAt;
        string agyNote = "불러오는 중…";
        volatile bool agyBusy;
        volatile bool showClaude = true, showCodex = true, showAgy = true;
        Group codex;
        DateTime? codexAt;
        string codexNote = "불러오는 중…";
        volatile bool codexBusy;
        Timer codexTimer;
        ImageSource icCodex;

        readonly StackPanel content;
        readonly Border toggle;
        ImageSource icClaude, icAgy;
        DispatcherTimer uiTimer;
        Timer agyTimer;

        public static void Snapshot(string file, string snapMode)
        {
            var w = new WidgetWindow(true);
            w.mode = snapMode == "ring" ? "ring" : "bar";
            w.Render();
            var card = (FrameworkElement)w.Content;
            w.Content = null;
            var desk = new Border { Background = new SolidColorBrush(C("#C9D6E3")), Padding = new Thickness(20), Child = card };
            desk.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desk.Arrange(new Rect(desk.DesiredSize));
            desk.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(desk.ActualWidth * 2), (int)Math.Ceiling(desk.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
            rtb.Render(desk);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (var fs = File.Create(file)) enc.Save(fs);
        }

        public WidgetWindow() : this(false) { }

        WidgetWindow(bool snapshot)
        {
            Title = "AI 사용 한도";
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Malgun Gothic");
            UseLayoutRounding = true;

            LoadSettings();
            Left = savedLeft ?? -10000;
            Top = savedTop ?? -10000;
            LoadIcons();

            var card = new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(24),
                Padding = new Thickness(18, 16, 18, 14),
                Margin = new Thickness(16),
                Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.16, Color = C("#1E283C") }
            };
            var root = new Grid();
            content = new StackPanel { Width = W };
            root.Children.Add(content);
            toggle = MakeToggle();
            root.Children.Add(toggle);
            card.Child = root;
            Content = card;

            ReloadClaude(true);
            LoadAgyCache();
            LoadCodexCache();
            Render();
            if (snapshot) return;

            card.MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } SaveSettings(); };
            ContextMenu = BuildMenu();
            // Grow upward: keep the bottom edge where it is when the content height changes.
            SizeChanged += (s, e) =>
            {
                if (!loaded || !e.HeightChanged || e.PreviousSize.Height <= 0) return;
                Top -= e.NewSize.Height - e.PreviousSize.Height;
                SaveSettings();
            };

            // Sit on the desktop: owned by the desktop window, hidden from Alt+Tab, kept under other windows.
            try { new WindowInteropHelper(this).Owner = FindWindow("Progman", null); } catch { }
            SourceInitialized += OnSourceInitialized;
            Loaded += OnLoaded;

            uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            uiTimer.Tick += (s, e) => { ReloadClaude(false); Render(); };
            uiTimer.Start();
            agyTimer = new Timer(_ => FetchAgy(), null, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(5));
            codexTimer = new Timer(_ => FetchCodex(), null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5));
            Closed += (s, e) => { uiTimer.Stop(); agyTimer.Dispose(); codexTimer.Dispose(); };
        }

        // ---------- data ----------

        void ReloadClaude(bool force)
        {
            try
            {
                var m1 = File.Exists(Paths.ClaudeJson) ? File.GetLastWriteTimeUtc(Paths.ClaudeJson) : DateTime.MinValue;
                var m2 = File.Exists(Paths.ClaudeAppJson) ? File.GetLastWriteTimeUtc(Paths.ClaudeAppJson) : DateTime.MinValue;
                var mt = m1 > m2 ? m1 : m2;
                if (!force && mt == claudeMtime) return;
                claudeMtime = mt;
                // Use whichever source is newer: the desktop app's scheduled task or the CLI status line.
                DateTime? a1, a2;
                var g1 = Data.LoadClaude(out a1);
                var g2 = Data.LoadClaudeApp(out a2);
                if (a2.HasValue && (!a1.HasValue || a2.Value >= a1.Value)) { claude = g2; claudeAt = a2; }
                else { claude = g1; claudeAt = a1; }
            }
            catch { }
        }

        void LoadAgyCache()
        {
            try
            {
                if (!File.Exists(Paths.AgyCache)) return;
                var text = File.ReadAllText(Paths.AgyCache);
                int nl = text.IndexOf('\n');
                if (nl < 0) return;
                var gs = Data.ParseAgy(text.Substring(nl + 1));
                if (gs.Count == 0) return;
                agy = gs;
                agyAt = Data.FromEpoch(double.Parse(text.Substring(0, nl).Trim(), CultureInfo.InvariantCulture));
            }
            catch { }
        }

        void FetchAgy()
        {
            if (agyBusy || !showAgy) return;
            agyBusy = true;
            try
            {
                if (!File.Exists(Paths.AgyExe))
                {
                    Dispatcher.BeginInvoke(new Action(() => { agyNote = "agy CLI를 찾을 수 없어요"; Render(); }));
                    return;
                }
                var txt = Data.RunAgy();
                if (txt == null) return;
                var gs = Data.ParseAgy(txt);
                if (gs.Count == 0) return;
                var now = DateTime.UtcNow;
                try { File.WriteAllText(Paths.AgyCache, Data.ToEpoch(now) + "\n" + txt, new UTF8Encoding(false)); } catch { }
                Dispatcher.BeginInvoke(new Action(() => { agy = gs; agyAt = now; Render(); }));
            }
            catch { }
            finally { agyBusy = false; }
        }

        void LoadCodexCache()
        {
            try
            {
                if (!File.Exists(Paths.CodexCache)) return;
                var text = File.ReadAllText(Paths.CodexCache);
                int nl = text.IndexOf('\n');
                if (nl < 0) return;
                var g = Data.ParseCodex(text.Substring(nl + 1));
                if (g == null) return;
                codex = g;
                codexAt = Data.FromEpoch(double.Parse(text.Substring(0, nl).Trim(), CultureInfo.InvariantCulture));
            }
            catch { }
        }

        void FetchCodex()
        {
            if (codexBusy || !showCodex) return;
            codexBusy = true;
            try
            {
                if (Paths.CodexExe() == null)
                {
                    Dispatcher.BeginInvoke(new Action(() => { codexNote = "Codex를 찾을 수 없어요"; Render(); }));
                    return;
                }
                var json = Data.RunCodex();
                if (json == null) return;
                var g = Data.ParseCodex(json);
                if (g == null) return;
                var now = DateTime.UtcNow;
                try { File.WriteAllText(Paths.CodexCache, Data.ToEpoch(now) + "\n" + json, new UTF8Encoding(false)); } catch { }
                Dispatcher.BeginInvoke(new Action(() => { codex = g; codexAt = now; Render(); }));
            }
            catch { }
            finally { codexBusy = false; }
        }

        // ---------- rendering ----------

        void Render()
        {
            content.Children.Clear();
            // The first visible section leaves room for the layout toggle button.
            double pad = 30;
            if (showClaude)
            {
                AddSection(Section(icClaude, "Claude", CClaude, new List<Group> { claude }, false, claudeAt,
                    claudeAt == null ? "다음 갱신 때 표시돼요" : null, 75, 360, pad));
                pad = 0;
            }
            if (showCodex)
            {
                AddSection(Section(icCodex, "Codex", CCodex, codex == null ? new List<Group>() : new List<Group> { codex }, false, codexAt,
                    codexAt == null ? codexNote : null, 12, 12, pad));
                pad = 0;
            }
            if (showAgy)
            {
                // Only the Gemini group; Antigravity's separate "Claude and GPT models" group is hidden.
                // If a plan reports no Gemini group, show whatever groups it has, with their names.
                var gem = agy.FindAll(g => g.Name == "Gemini");
                AddSection(Section(icAgy, "Antigravity", CAgy, gem.Count > 0 ? gem : agy, gem.Count == 0, agyAt,
                    agyAt == null ? agyNote : null, 12, 12, pad));
                pad = 0;
            }
            if (content.Children.Count == 0)
            {
                var t = Txt("표시할 서비스가 없어요. 우클릭해서 켜세요.", 12, CMuted);
                t.Margin = new Thickness(0, 4, 32, 4);
                t.TextWrapping = TextWrapping.Wrap;
                content.Children.Add(t);
            }
            UpdateToggleGlyph();
        }

        void AddSection(UIElement section)
        {
            if (content.Children.Count > 0)
                content.Children.Add(new Border { Height = 1, Background = B(CSep), Margin = new Thickness(0, 14, 0, 12) });
            content.Children.Add(section);
        }

        UIElement Section(ImageSource icon, string name, Color brand, List<Group> groups, bool showGroupNames,
            DateTime? at, string emptyNote, double staleMin, double dimMin, double rightPad)
        {
            var sp = new StackPanel();
            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, rightPad, 0) };
            var img = IconEl(icon, brand);
            DockPanel.SetDock(img, Dock.Left);
            head.Children.Add(img);

            string note = emptyNote;
            bool dim = false;
            if (at.HasValue)
            {
                var age = DateTime.UtcNow - at.Value;
                if (age.TotalMinutes >= staleMin) note = Ago(age) + " 기준";
                dim = age.TotalMinutes >= dimMin;
            }
            if (note != null)
            {
                var t = Txt(note, 11, CMuted);
                t.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(t, Dock.Right);
                head.Children.Add(t);
            }
            var title = Txt(name, 14, CText, true);
            title.Margin = new Thickness(9, 0, 0, 0);
            title.VerticalAlignment = VerticalAlignment.Center;
            head.Children.Add(title);
            sp.Children.Add(head);

            var body = new StackPanel { Opacity = dim ? 0.45 : 1 };
            bool any = false;
            if (at.HasValue)
            {
                foreach (var g in groups)
                {
                    // Only the windows this plan actually has.
                    var ms = new List<KeyValuePair<string, Metric>>();
                    if (!g.FiveHour.NA) ms.Add(new KeyValuePair<string, Metric>("5시간", g.FiveHour));
                    if (!g.Weekly.NA) ms.Add(new KeyValuePair<string, Metric>("주간", g.Weekly));
                    if (ms.Count == 0) continue;
                    any = true;
                    if (showGroupNames)
                    {
                        var gl = Txt(g.Name, 11, CMuted);
                        gl.Margin = new Thickness(0, 10, 0, mode == "bar" ? -3 : 0);
                        body.Children.Add(gl);
                    }
                    if (mode == "bar")
                    {
                        foreach (var kv in ms) body.Children.Add(BarRow(kv.Key, kv.Value, brand));
                    }
                    else
                    {
                        var row = new UniformGrid { Columns = 2, Margin = new Thickness(0, showGroupNames ? 4 : 10, 0, 0) };
                        foreach (var kv in ms) row.Children.Add(Ring(kv.Key, kv.Value, brand));
                        body.Children.Add(row);
                    }
                }
            }
            if (at.HasValue && !any && note == null)
            {
                // The plan reports no 5-hour or weekly limit.
                var t = Txt("사용 한도 없음", 11, CMuted);
                t.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(t, Dock.Right);
                head.Children.Insert(1, t);
            }
            sp.Children.Add(body);
            return sp;
        }

        static void Eff(Metric m, out double pct, out string reset)
        {
            if (m.Remaining < 0) { pct = -1; reset = ""; return; }
            if (m.ResetsAt.HasValue && m.ResetsAt.Value <= DateTime.UtcNow) { pct = 100; reset = "초기화됨"; return; }
            pct = Math.Round(m.Remaining);
            reset = m.ResetsAt.HasValue ? TimeLeft(m.ResetsAt.Value - DateTime.UtcNow) : "";
        }

        static string TimeLeft(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "1분 미만";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + "분";
            if (t.TotalDays < 1) return (int)t.TotalHours + "시간 " + t.Minutes + "분";
            return (int)t.TotalDays + "일 " + t.Hours + "시간";
        }

        static string Ago(TimeSpan t)
        {
            if (t.TotalHours < 1) return (int)Math.Max(1, t.TotalMinutes) + "분 전";
            if (t.TotalDays < 1) return (int)t.TotalHours + "시간 전";
            return (int)t.TotalDays + "일 전";
        }

        static Color ColorFor(double p, Color brand)
        {
            if (p < 0) return CMuted;
            if (p <= 5) return CDanger;
            if (p <= 20) return CWarn;
            return brand;
        }

        UIElement BarRow(string label, Metric m, Color brand)
        {
            double pct; string reset;
            Eff(m, out pct, out reset);
            var col = ColorFor(pct, brand);

            var sp = new StackPanel { Margin = new Thickness(0, 9, 0, 0) };
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
            var r = Txt(reset.Length > 0 && reset != "초기화됨" ? reset + " 후 초기화" : reset, 11, CMuted);
            r.VerticalAlignment = VerticalAlignment.Bottom;
            DockPanel.SetDock(r, Dock.Right);
            top.Children.Add(r);
            var l = new TextBlock { FontSize = 12, Foreground = B(CLabel), VerticalAlignment = VerticalAlignment.Bottom };
            l.Inlines.Add(new Run(label + "  "));
            l.Inlines.Add(new Run(pct < 0 ? "—" : pct + "%") { FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = B(col == brand ? CText : col) });
            top.Children.Add(l);
            sp.Children.Add(top);

            var track = new Grid { Height = 7 };
            track.Children.Add(new Border { Background = B(CTrack), CornerRadius = new CornerRadius(3.5) });
            if (pct > 0)
                track.Children.Add(new Border
                {
                    Background = B(col),
                    CornerRadius = new CornerRadius(3.5),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = Math.Max(7, W * pct / 100)
                });
            sp.Children.Add(track);
            return sp;
        }

        UIElement Ring(string label, Metric m, Color brand)
        {
            double pct; string reset;
            Eff(m, out pct, out reset);
            var col = ColorFor(pct, brand);
            const double S = 66, SW = 7;
            double rad = (S - SW) / 2, cx = S / 2, cy = S / 2;

            var g = new Grid { Width = S, Height = S };
            g.Children.Add(new Ellipse { Stroke = B(CTrack), StrokeThickness = SW });
            if (pct >= 100)
                g.Children.Add(new Ellipse { Stroke = B(col), StrokeThickness = SW });
            else if (pct > 0)
            {
                double a = 2 * Math.PI * pct / 100;
                var fig = new PathFigure { StartPoint = new Point(cx, cy - rad), IsClosed = false };
                fig.Segments.Add(new ArcSegment(new Point(cx + rad * Math.Sin(a), cy - rad * Math.Cos(a)), new Size(rad, rad), 0, pct > 50, SweepDirection.Clockwise, true));
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                g.Children.Add(new ShapePath { Data = geo, Stroke = B(col), StrokeThickness = SW, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
            }
            var t = Txt(pct < 0 ? "—" : pct + "%", 15, col == brand ? CText : col, true);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);

            var sp = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            sp.Children.Add(g);
            var lb = Txt(label, 12, CLabel);
            lb.HorizontalAlignment = HorizontalAlignment.Center;
            lb.Margin = new Thickness(0, 5, 0, 0);
            sp.Children.Add(lb);
            var rs = Txt(reset.Length > 0 && reset != "초기화됨" ? reset + " 후" : reset, 11, CMuted);
            rs.HorizontalAlignment = HorizontalAlignment.Center;
            sp.Children.Add(rs);
            return sp;
        }

        static TextBlock Txt(string s, double size, Color c, bool bold = false)
        {
            return new TextBlock { Text = s, FontSize = size, Foreground = B(c), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        }

        static FrameworkElement IconEl(ImageSource src, Color fallback)
        {
            if (src != null)
            {
                var im = new Image { Source = src, Width = 22, Height = 22 };
                RenderOptions.SetBitmapScalingMode(im, BitmapScalingMode.HighQuality);
                return im;
            }
            return new Ellipse { Width = 18, Height = 18, Fill = B(fallback), Margin = new Thickness(2) };
        }

        // ---------- toggle, menu, settings ----------

        Border MakeToggle()
        {
            var b = new Border
            {
                Width = 26, Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = B(CBtn),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -4, 0),
                Cursor = Cursors.Hand,
                ToolTip = "디자인 전환 (막대 / 원형)"
            };
            b.MouseEnter += (s, e) => b.Background = B(CBtnHover);
            b.MouseLeave += (s, e) => b.Background = B(CBtn);
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; ToggleMode(); };
            return b;
        }

        void UpdateToggleGlyph()
        {
            if (mode == "bar")
            {
                toggle.Child = new Ellipse { Width = 11, Height = 11, Stroke = B(CLabel), StrokeThickness = 2 };
            }
            else
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                for (int i = 0; i < 2; i++)
                    sp.Children.Add(new Border { Width = i == 0 ? 12 : 8, Height = 2.5, CornerRadius = new CornerRadius(1.25), Background = B(CLabel), Margin = new Thickness(0, 1.5, 0, 1.5), HorizontalAlignment = HorizontalAlignment.Left });
                toggle.Child = sp;
            }
        }

        void ToggleMode()
        {
            mode = mode == "bar" ? "ring" : "bar";
            Render();
            SaveSettings();
        }

        ContextMenu BuildMenu()
        {
            var m = new ContextMenu();
            var a = new MenuItem { Header = "디자인 전환 (막대 / 원형)" };
            a.Click += (s, e) => ToggleMode();
            m.Items.Add(a);
            var r = new MenuItem { Header = "지금 새로고침" };
            r.Click += (s, e) => { ReloadClaude(true); Render(); ThreadPool.QueueUserWorkItem(_ => FetchAgy()); ThreadPool.QueueUserWorkItem(_ => FetchCodex()); };
            m.Items.Add(r);
            m.Items.Add(new Separator());
            // Per-service visibility. A hidden service is not fetched either.
            var sc = new MenuItem { Header = "Claude 표시", IsCheckable = true };
            var sx = new MenuItem { Header = "Codex 표시", IsCheckable = true };
            var sa = new MenuItem { Header = "Antigravity 표시", IsCheckable = true };
            m.Opened += (s, e) => { sc.IsChecked = showClaude; sx.IsChecked = showCodex; sa.IsChecked = showAgy; };
            sc.Click += (s, e) => { showClaude = sc.IsChecked; ReloadClaude(true); Render(); SaveSettings(); };
            sx.Click += (s, e) => { showCodex = sx.IsChecked; Render(); SaveSettings(); if (showCodex) ThreadPool.QueueUserWorkItem(_ => FetchCodex()); };
            sa.Click += (s, e) => { showAgy = sa.IsChecked; Render(); SaveSettings(); if (showAgy) ThreadPool.QueueUserWorkItem(_ => FetchAgy()); };
            m.Items.Add(sc);
            m.Items.Add(sx);
            m.Items.Add(sa);
            m.Items.Add(new Separator());
            var st = new MenuItem { Header = "Windows 시작 시 실행", IsCheckable = true };
            m.Opened += (s, e) => st.IsChecked = Autostart;
            st.Click += (s, e) => Autostart = st.IsChecked;
            m.Items.Add(st);
            m.Items.Add(new Separator());
            var q = new MenuItem { Header = "종료" };
            q.Click += (s, e) => Close();
            m.Items.Add(q);
            return m;
        }

        static bool Autostart
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue("AiUsageWidget") != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue("AiUsageWidget", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    else k.DeleteValue("AiUsageWidget", false);
                }
            }
        }

        void LoadSettings()
        {
            try
            {
                if (!File.Exists(Paths.Settings)) return;
                foreach (var line in File.ReadAllLines(Paths.Settings))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i), v = line.Substring(i + 1);
                    double d;
                    if (k == "mode") mode = v == "ring" ? "ring" : "bar";
                    else if (k == "left" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) savedLeft = d;
                    else if (k == "top" && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) savedTop = d;
                    else if (k == "show_claude") showClaude = v != "0";
                    else if (k == "show_codex") showCodex = v != "0";
                    else if (k == "show_antigravity") showAgy = v != "0";
                }
            }
            catch { }
        }

        void SaveSettings()
        {
            if (!loaded) return;
            try
            {
                File.WriteAllLines(Paths.Settings, new[] {
                    "mode=" + mode,
                    "left=" + Left.ToString(CultureInfo.InvariantCulture),
                    "top=" + Top.ToString(CultureInfo.InvariantCulture),
                    "show_claude=" + (showClaude ? "1" : "0"),
                    "show_codex=" + (showCodex ? "1" : "0"),
                    "show_antigravity=" + (showAgy ? "1" : "0") });
            }
            catch { }
        }

        void LoadIcons()
        {
            icClaude = Png(Paths.ClaudeIcon);
            icCodex = Png(Paths.CodexIcon);
            icAgy = ExeIcon(Paths.AntigravityExe, 64);
        }

        static ImageSource Png(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.UriSource = new Uri(path);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        // ---------- window placement ----------

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            bool ok = savedLeft.HasValue && savedTop.HasValue && vs.Contains(new Point(savedLeft.Value + 40, savedTop.Value + 40));
            if (!ok)
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - ActualWidth;
                Top = wa.Bottom - ActualHeight;
            }
            loaded = true;
            SaveSettings();
        }

        void OnSourceInitialized(object sender, EventArgs e)
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
            var src = HwndSource.FromHwnd(h);
            if (src != null) src.AddHook(Hook);
            SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_WINDOWPOSCHANGING)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(lParam, typeof(WINDOWPOS));
                wp.hwndInsertAfter = HWND_BOTTOM;
                wp.flags &= ~SWP_NOZORDER;
                Marshal.StructureToPtr(wp, lParam, false);
            }
            return IntPtr.Zero;
        }

        static ImageSource ExeIcon(string path, int size)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var h = new IntPtr[1];
                var ids = new int[1];
                if (PrivateExtractIcons(path, 0, size, size, h, ids, 1, 0) > 0 && h[0] != IntPtr.Zero)
                {
                    var bs = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    DestroyIcon(h[0]);
                    return bs;
                }
            }
            catch { }
            return null;
        }

        const int GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WM_WINDOWPOSCHANGING = 0x46;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS { public IntPtr hwnd, hwndInsertAfter; public int x, y, cx, cy; public uint flags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int idx, int v);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int idx, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
