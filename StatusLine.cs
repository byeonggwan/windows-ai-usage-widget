// Claude Code statusLine handler.
// Reads the status line JSON from stdin, saves rate_limits to %LOCALAPPDATA%\AiUsageWidget\claude.json
// for the widget, and prints a short status line. Never touches login tokens.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

static class StatusLine
{
    static int Main()
    {
        string line = "";
        try
        {
            string input;
            using (var r = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
                input = r.ReadToEnd();

            var ser = new JavaScriptSerializer();
            var d = ser.Deserialize<Dictionary<string, object>>(input);

            string model = "";
            object mo;
            if (d.TryGetValue("model", out mo))
            {
                var md = mo as Dictionary<string, object>;
                object dn;
                if (md != null && md.TryGetValue("display_name", out dn) && dn != null) model = dn.ToString();
            }
            line = model;

            object rlo;
            var rl = d.TryGetValue("rate_limits", out rlo) ? rlo as Dictionary<string, object> : null;
            if (rl != null)
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AiUsageWidget");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "claude.json");
                double now = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                var outObj = new Dictionary<string, object>();
                outObj["captured_at"] = Math.Floor(now);
                outObj["rate_limits"] = rl;
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, ser.Serialize(outObj), new UTF8Encoding(false));
                File.Copy(tmp, path, true);
                File.Delete(tmp);

                line = (model.Length > 0 ? model + " · " : "") + "5시간 " + Remaining(rl, "five_hour") + " · 주간 " + Remaining(rl, "seven_day") + " 남음";
            }
        }
        catch { }

        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(line);
            using (var o = Console.OpenStandardOutput()) o.Write(bytes, 0, bytes.Length);
        }
        catch { }
        return 0;
    }

    static string Remaining(Dictionary<string, object> rl, string key)
    {
        object o;
        if (!rl.TryGetValue(key, out o)) return "100%";
        var w = o as Dictionary<string, object>;
        object u;
        if (w == null || !w.TryGetValue("used_percentage", out u) || u == null) return "?";
        double used = Convert.ToDouble(u, CultureInfo.InvariantCulture);
        return Math.Round(Math.Max(0, 100 - used)).ToString(CultureInfo.InvariantCulture) + "%";
    }
}
