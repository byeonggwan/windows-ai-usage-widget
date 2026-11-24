// Data sources for the AI usage widget. None of them reads or refreshes login tokens.
// Claude: claude-app.json (desktop-app scheduled task) or claude.json (Claude Code statusLine).
// Codex: `codex app-server` account/rateLimits/read. Antigravity: `agy -p /usage`.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using IOPath = System.IO.Path;

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
        public static readonly string CodexIconDark = IOPath.Combine(Dir, "icons", "codex-dark.png");

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
        public static readonly string ClaudeIcon = IOPath.Combine(Dir, "icons", "claude.png");

        // The Antigravity CLI lands in different places depending on how it was installed:
        // the official installer (~\.gemini\bin), the documented user directory (%LOCALAPPDATA%\agy\bin),
        // winget (a portable package under WinGet\Packages), or anything else on PATH (winget's Links
        // folder, Chocolatey, Scoop). The Antigravity desktop app does not include it.
        public static string AgyExe()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var candidates = new List<string>
            {
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "bin", "agy.exe"),
                IOPath.Combine(local, "agy", "bin", "agy.exe"),
            };
            foreach (var root in new[] {
                IOPath.Combine(local, "Microsoft", "WinGet", "Packages"),
                IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Packages") })
            {
                try
                {
                    if (Directory.Exists(root))
                        foreach (var d in Directory.GetDirectories(root, "Google.AntigravityCLI_*"))
                            candidates.Add(IOPath.Combine(d, "agy.exe"));
                }
                catch { }
            }
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (dir.Trim().Length == 0) continue;
                try { candidates.Add(IOPath.Combine(Environment.ExpandEnvironmentVariables(dir.Trim().Trim('"')), "agy.exe")); }
                catch { }
            }
            foreach (var f in candidates)
                if (File.Exists(f)) return f;
            return null;
        }
        public static readonly string AntigravityExe = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "antigravity", "Antigravity.exe");
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

        public static string RunAgy(string exe)
        {
            if (exe == null || !File.Exists(exe)) return null;
            var psi = new ProcessStartInfo(exe, "-p /usage")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Paths.Dir
            };
            // agy checks for its own updates every 15 minutes and starts the updater in a new
            // console, which pops up a terminal window and takes focus. The widget only reads
            // usage, so it never triggers that; agy run by hand still updates itself.
            psi.EnvironmentVariables["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
            using (var p = Process.Start(psi))
            {
                var so = p.StandardOutput.ReadToEndAsync();
                p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(45000)) { try { p.Kill(); } catch { } return null; }
                return so.Result;
            }
        }
    }
}
