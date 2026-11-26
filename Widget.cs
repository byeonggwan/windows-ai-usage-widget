// AI usage desktop widget, built on BWUI (https://github.com/byeonggwan/bwui).
// Shows remaining 5-hour and weekly limits for Claude, Codex and Antigravity.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Bwui;

namespace AiUsageWidget
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            Directory.CreateDirectory(Paths.Dir);
            // --snapshot <png> [light|dark] [ring]: render a preview from the cached data and exit.
            Bwui.App.Run("AiUsageWidget", args, () => new UsageWidget());
        }
    }

    class UsageWidget : BwuiWindow
    {
        // Brand accents as light/dark pairs; each reaches 3:1 on its theme's Surface (DESIGN.md §3.5).
        static readonly Color ClaudeL = Palette.Hex("#D97757"), ClaudeD = Palette.Hex("#E38B6B"),
            CodexL = Palette.Hex("#10A37F"), CodexD = Palette.Hex("#19C37D"),
            AgyL = Palette.Hex("#4285F4"), AgyD = Palette.Hex("#6EA4F7");
        static readonly TimeSpan FetchEvery = TimeSpan.FromMinutes(5);

        bool rings;
        bool showClaude, showCodex, showAgy;

        Group claude = new Group { Name = "Claude" };
        DateTime? claudeAt;
        DateTime claudeMtime;
        List<Group> agy = new List<Group>();
        DateTime? agyAt;
        string agyProblem;
        volatile bool agyBusy;
        Group codex;
        DateTime? codexAt;
        string codexProblem;
        volatile bool codexBusy;
        ImageSource icClaude, icCodex, icCodexDark, icAgy;
        Timer agyTimer, codexTimer;

        public UsageWidget() : base("AiUsageWidget", "AI usage")
        {
            rings = Settings.Get("mode") == "ring";
            showClaude = Settings.GetBool("show_claude", true);
            showCodex = Settings.GetBool("show_codex", true);
            showAgy = Settings.GetBool("show_antigravity", true);
            if (Bwui.App.IsSnapshot)
            {
                // Previews show every service, in the layout asked for.
                rings = Array.IndexOf(Bwui.App.SnapshotArgs, "ring") >= 0;
                showClaude = showCodex = showAgy = true;
            }
            icClaude = Ui.LoadPng(Paths.ClaudeIcon);
            icCodex = Ui.LoadPng(Paths.CodexIcon);
            icCodexDark = Ui.LoadPng(Paths.CodexIconDark) ?? icCodex;
            icAgy = ExeIcon(Paths.AntigravityExe, 64);

            ReloadClaude(true);
            // Preview of a PC where Claude was never set up.
            if (Bwui.App.IsSnapshot && Array.IndexOf(Bwui.App.SnapshotArgs, "noclaude") >= 0)
            {
                snapshotNoClaude = true; claude = new Group { Name = "Claude" }; claudeAt = null;
            }
            LoadAgyCache();
            LoadCodexCache();
            Render();
            if (Bwui.App.IsSnapshot) return;

            // Local file: cheap to check often. Network/process fetches: every 5 min, doubled on battery (C12, C16).
            var ui = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            ui.Tick += (s, e) => { ReloadClaude(false); Render(); };
            ui.Start();
            agyTimer = new Timer(_ => { FetchAgy(); Reschedule(agyTimer); }, null, TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
            codexTimer = new Timer(_ => { FetchCodex(); Reschedule(codexTimer); }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan);
            Closed += (s, e) => { ui.Stop(); agyTimer.Dispose(); codexTimer.Dispose(); };
        }

        static void Reschedule(Timer t)
        {
            try { t.Change(Bwui.App.Interval(FetchEvery), Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        // ---------- menu ----------

        protected override UpdateSource Updates
        {
            get { return new UpdateSource("byeonggwan", "windows-ai-usage-widget", "AiUsageWidget.exe", "AiUsageStatus.exe"); }
        }

        protected override bool CanRefresh { get { return true; } }

        protected override void RefreshNow()
        {
            ReloadClaude(true);
            Render();
            ThreadPool.QueueUserWorkItem(_ => FetchAgy());
            ThreadPool.QueueUserWorkItem(_ => FetchCodex());
        }

        protected override void AddMenuItems(ContextMenu menu)
        {
            menu.Items.Add(Item(LayoutLabel, ToggleLayout));
            menu.Items.Add(new Separator());
            // A hidden service is not fetched either (DESIGN.md §5, Hidden).
            menu.Items.Add(Check(Bwui.Text.T("Claude 표시", "Show Claude"), showClaude, on =>
            {
                showClaude = on; Settings.SetBool("show_claude", on); ReloadClaude(true); Render();
            }));
            menu.Items.Add(Item(Bwui.Text.T("Claude 연결 설정 (설정 문구 복사)", "Connect Claude (copy setup message)"), CopyClaudeSetup));
            menu.Items.Add(Check(Bwui.Text.T("Codex 표시", "Show Codex"), showCodex, on =>
            {
                showCodex = on; Settings.SetBool("show_codex", on); Render();
                if (on) ThreadPool.QueueUserWorkItem(_ => FetchCodex());
            }));
            menu.Items.Add(Check(Bwui.Text.T("Antigravity 표시", "Show Antigravity"), showAgy, on =>
            {
                showAgy = on; Settings.SetBool("show_antigravity", on); Render();
                if (on) ThreadPool.QueueUserWorkItem(_ => FetchAgy());
            }));
        }

        DateTime? setupCopiedAt;
        bool snapshotNoClaude;

        // Claude needs a source on each PC: the desktop app's scheduled task (claude-app.json) or the
        // Claude Code status line (claude.json). Neither exists until it is set up once.
        bool ClaudeConnected
        {
            get { return !snapshotNoClaude && (File.Exists(Paths.ClaudeAppJson) || File.Exists(Paths.ClaudeJson)); }
        }

        void CopyClaudeSetup()
        {
            for (int i = 0; i < 5; i++)
            {
                // The clipboard can be briefly held by another app.
                try { Clipboard.SetText(ClaudeSetup.Message); setupCopiedAt = DateTime.UtcNow; break; }
                catch (COMException) { Thread.Sleep(100); }
            }
            Render();
        }

        string ClaudeProblem
        {
            get
            {
                if (ClaudeConnected) return Bwui.Text.T("다음 갱신 때 표시돼요", "Shown after the next update");
                if (setupCopiedAt.HasValue && DateTime.UtcNow - setupCopiedAt.Value < TimeSpan.FromMinutes(15))
                    return Bwui.Text.T("설정 문구를 복사했어요. Claude 앱의 새 대화에 붙여 넣고 보내 주세요.",
                        "Setup message copied. Paste it into a new chat in the Claude app and send it.");
                return Bwui.Text.T("Claude 연결이 필요해요. 우클릭 → Claude 연결 설정",
                    "Claude isn't connected. Right-click → Connect Claude");
            }
        }

        static string LayoutLabel { get { return Bwui.Text.T("디자인 전환 (막대 / 원형)", "Switch layout (bars / rings)"); } }

        void ToggleLayout()
        {
            rings = !rings;
            Settings.Set("mode", rings ? "ring" : "bar");
            Render();
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

        static bool ReadCache(string path, out string body, out DateTime at)
        {
            body = null; at = DateTime.MinValue;
            try
            {
                if (!File.Exists(path)) return false;
                var text = File.ReadAllText(path);
                int nl = text.IndexOf('\n');
                if (nl < 0) return false;
                body = text.Substring(nl + 1);
                at = Data.FromEpoch(double.Parse(text.Substring(0, nl).Trim(), CultureInfo.InvariantCulture));
                return true;
            }
            catch { return false; }
        }

        void LoadAgyCache()
        {
            string body; DateTime at;
            if (!ReadCache(Paths.AgyCache, out body, out at)) return;
            var gs = Data.ParseAgy(body);
            if (gs.Count == 0) return;
            agy = gs;
            agyAt = at;
        }

        void LoadCodexCache()
        {
            string body; DateTime at;
            if (!ReadCache(Paths.CodexCache, out body, out at)) return;
            var g = Data.ParseCodex(body);
            if (g == null) return;
            codex = g;
            codexAt = at;
        }

        void FetchAgy()
        {
            if (agyBusy || !showAgy) return;
            agyBusy = true;
            try
            {
                var exe = Paths.AgyExe();
                if (exe == null)
                {
                    Post(() => agyProblem = Bwui.Text.T("agy CLI를 찾을 수 없어요. Antigravity CLI를 설치하면 표시돼요.", "agy CLI not found. Install the Antigravity CLI to see its limits."));
                    return;
                }
                var txt = Data.RunAgy(exe);
                var gs = txt == null ? null : Data.ParseAgy(txt);
                if (gs == null || gs.Count == 0)
                {
                    Post(() => agyProblem = Bwui.Text.T("한도를 불러오지 못했어요", "Couldn't load the limits"));
                    return;
                }
                var now = DateTime.UtcNow;
                try { File.WriteAllText(Paths.AgyCache, Data.ToEpoch(now) + "\n" + txt, new UTF8Encoding(false)); } catch { }
                Post(() => { agy = gs; agyAt = now; agyProblem = null; });
            }
            catch { }
            finally { agyBusy = false; }
        }

        void FetchCodex()
        {
            if (codexBusy || !showCodex) return;
            codexBusy = true;
            try
            {
                if (Paths.CodexExe() == null)
                {
                    Post(() => codexProblem = Bwui.Text.T("Codex를 찾을 수 없어요. Codex 앱을 실행하거나 CLI를 설치하면 표시돼요.", "Codex not found. Run the Codex app or install the CLI."));
                    return;
                }
                var json = Data.RunCodex();
                var g = json == null ? null : Data.ParseCodex(json);
                if (g == null)
                {
                    Post(() => codexProblem = Bwui.Text.T("한도를 불러오지 못했어요", "Couldn't load the limits"));
                    return;
                }
                var now = DateTime.UtcNow;
                try { File.WriteAllText(Paths.CodexCache, Data.ToEpoch(now) + "\n" + json, new UTF8Encoding(false)); } catch { }
                Post(() => { codex = g; codexAt = now; codexProblem = null; });
            }
            catch { }
            finally { codexBusy = false; }
        }

        void Post(Action a)
        {
            Dispatcher.BeginInvoke(new Action(() => { a(); Render(); }));
        }

        // ---------- rendering ----------

        protected override UIElement Build()
        {
            var p = Theme.Current;
            var sp = new StackPanel();
            // The layout toggle sits in the first visible section's header.
            bool first = true;
            if (showClaude)
            {
                AddSection(sp, Section(icClaude, "Claude", p.Accent(ClaudeL, ClaudeD), new List<Group> { claude }, false,
                    Freshness.Of(claudeAt, TimeSpan.FromMinutes(75), TimeSpan.FromHours(6)),
                    claudeAt == null ? ClaudeProblem : null, false, first));
                first = false;
            }
            if (showCodex)
            {
                AddSection(sp, Section(p.IsDark ? icCodexDark : icCodex, "Codex", p.Accent(CodexL, CodexD), codex == null ? new List<Group>() : new List<Group> { codex }, false,
                    Freshness.Of(codexAt, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(12)),
                    codexAt == null ? codexProblem : null, codexAt == null && codexProblem == null, first));
                first = false;
            }
            if (showAgy)
            {
                // Only the Gemini group; Antigravity's separate "Claude and GPT models" group is hidden.
                // If a plan reports no Gemini group, show whatever groups it has, with their names.
                var gem = agy.FindAll(g => g.Name == "Gemini");
                AddSection(sp, Section(icAgy, "Antigravity", p.Accent(AgyL, AgyD), gem.Count > 0 ? gem : agy, gem.Count == 0,
                    Freshness.Of(agyAt, TimeSpan.FromMinutes(12), TimeSpan.FromMinutes(12)),
                    agyAt == null ? agyProblem : null, agyAt == null && agyProblem == null, first));
                first = false;
            }
            if (sp.Children.Count == 0)
            {
                var row = new DockPanel();
                var b = ToggleButton();
                DockPanel.SetDock(b, Dock.Right);
                row.Children.Add(b);
                var m = Ui.Message(Bwui.Text.NothingShown);
                m.Margin = new Thickness(0, 0, Tokens.Space2, 0);
                row.Children.Add(m);
                sp.Children.Add(row);
            }
            return sp;
        }

        static void AddSection(StackPanel sp, UIElement section)
        {
            if (sp.Children.Count > 0) sp.Children.Add(Ui.Divider());
            sp.Children.Add(section);
        }

        UIElement Section(ImageSource icon, string name, Color accent, List<Group> groups, bool showGroupNames,
            Freshness f, string problem, bool loading, bool withToggle)
        {
            var sp = new StackPanel();

            // Header trailing slot: stale caption and/or the layout toggle.
            var trail = new StackPanel { Orientation = Orientation.Horizontal };
            if (f.Caption != null)
            {
                var c = Ui.Text(f.Caption, TextStyle.Caption);
                c.VerticalAlignment = VerticalAlignment.Center;
                trail.Children.Add(c);
            }
            if (withToggle)
            {
                var b = ToggleButton();
                b.Margin = new Thickness(trail.Children.Count > 0 ? Tokens.Space2 : 0, 0, 0, 0);
                trail.Children.Add(b);
            }
            sp.Children.Add(Ui.Header(Ui.Icon(icon, accent), name, trail.Children.Count > 0 ? trail : null));

            if (!f.HasData)
            {
                if (loading) sp.Children.Add(Ui.Skeleton(ContentWidth, 2));
                else sp.Children.Add(Ui.Message(problem ?? Bwui.Text.Loading));
                return sp;
            }

            var body = new StackPanel { Opacity = f.BodyOpacity };
            bool any = false;
            foreach (var g in groups)
            {
                // Only the windows this plan actually has.
                var ms = new List<KeyValuePair<string, Metric>>();
                if (!g.FiveHour.NA) ms.Add(new KeyValuePair<string, Metric>(Bwui.Text.T("5시간", "5-hour"), g.FiveHour));
                if (!g.Weekly.NA) ms.Add(new KeyValuePair<string, Metric>(Bwui.Text.T("주간", "Weekly"), g.Weekly));
                if (ms.Count == 0) continue;
                any = true;
                if (showGroupNames)
                {
                    var gl = Ui.Text(g.Name, TextStyle.Caption);
                    gl.Margin = new Thickness(0, Tokens.Space3, 0, 0);
                    body.Children.Add(gl);
                }
                if (!rings)
                {
                    foreach (var kv in ms)
                    {
                        var r = BarRow(kv.Key, kv.Value, accent);
                        r.Margin = new Thickness(0, Tokens.Space3, 0, 0);
                        body.Children.Add(r);
                    }
                }
                else
                {
                    var items = new List<UIElement>();
                    foreach (var kv in ms) items.Add(RingItem(kv.Key, kv.Value, accent));
                    var row = Ui.RingRow(items.ToArray());
                    row.Margin = new Thickness(0, Tokens.Space3, 0, 0);
                    body.Children.Add(row);
                }
            }
            // The plan reports no 5-hour or weekly limit (DESIGN.md §5, Empty).
            if (!any) body.Children.Add(Ui.Message(Bwui.Text.T("사용 한도 없음", "No usage limit")));
            sp.Children.Add(body);
            return sp;
        }

        // Remaining %, the reset countdown, and the status for a metric.
        static void Eff(Metric m, out double pct, out string reset)
        {
            if (m.Remaining < 0) { pct = -1; reset = ""; return; }
            if (m.ResetsAt.HasValue && m.ResetsAt.Value <= DateTime.UtcNow) { pct = 100; reset = Bwui.Text.T("초기화됨", "reset"); return; }
            pct = Math.Round(m.Remaining);
            reset = m.ResetsAt.HasValue ? Bwui.Text.Duration(m.ResetsAt.Value - DateTime.UtcNow) : "";
        }

        FrameworkElement BarRow(string label, Metric m, Color accent)
        {
            double pct; string reset;
            Eff(m, out pct, out reset);
            string cap = reset;
            if (m.ResetsAt.HasValue && m.ResetsAt.Value > DateTime.UtcNow) cap = Bwui.Text.T(reset + " 후 초기화", "resets in " + reset);
            return Ui.MeterRow(label, pct < 0 ? "—" : pct + "%", Math.Max(0, pct) / 100, cap, accent, Ui.StatusForRemaining(pct), ContentWidth);
        }

        FrameworkElement RingItem(string label, Metric m, Color accent)
        {
            double pct; string reset;
            Eff(m, out pct, out reset);
            string cap = reset;
            if (m.ResetsAt.HasValue && m.ResetsAt.Value > DateTime.UtcNow) cap = Bwui.Text.T(reset + " 후", "in " + reset);
            return Ui.Ring(Math.Max(0, pct) / 100, pct < 0 ? "—" : pct + "%", label, cap, accent, Ui.StatusForRemaining(pct));
        }

        Border ToggleButton()
        {
            var c = Ui.Br(Theme.Current.TextSecondary);
            UIElement glyph;
            if (!rings) glyph = new Ellipse { Width = 12, Height = 12, Stroke = c, StrokeThickness = 2 };
            else
            {
                var gp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                for (int i = 0; i < 2; i++)
                    gp.Children.Add(new Border { Width = i == 0 ? 12 : 8, Height = 2, CornerRadius = new CornerRadius(1), Background = c, Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left });
                glyph = gp;
            }
            return Ui.IconButton(glyph, LayoutLabel, ToggleLayout);
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIcons(string file, int idx, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    }
}
