// Claude Usage Tray — lightweight tray widget for Claude plan limits + local Claude Code token stats.
// Targets .NET Framework 4.x (C# 5) so it builds with the csc.exe that ships with Windows.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ClaudeUsageTray
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        public static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUsageTray", "crash.log");

        static void LogCrash(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("s") + "  " + e + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, "ClaudeUsageTray.SingleInstance", out created))
            {
                if (!created) return;
                // Log crashes instead of dying silently; UI-thread errors are logged and the app keeps running.
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogCrash(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogCrash(e.ExceptionObject as Exception);
                SetProcessDPIAware();
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp(args));
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    static class J
    {
        public static object Get(object o, params string[] path)
        {
            foreach (var p in path)
            {
                var d = o as Dictionary<string, object>;
                if (d == null || !d.TryGetValue(p, out o)) return null;
            }
            return o;
        }
        public static string Str(object o) { return o as string; }
        public static double Num(object o, double def)
        {
            if (o == null || o is string) return def;
            try { return Convert.ToDouble(o, CultureInfo.InvariantCulture); } catch { return def; }
        }
        public static long Long(object o) { return (long)Num(o, 0); }
        public static DateTime? Time(object o)
        {
            var s = o as string;
            DateTime t;
            if (s != null && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t)) return t;
            return null;
        }
    }

    static class Fmt
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string Tokens(long n)
        {
            if (n < 1000) return n.ToString(Inv);
            if (n < 100000) return (n / 1000.0).ToString("0.#", Inv) + "k";
            if (n < 1000000) return (n / 1000).ToString(Inv) + "k";
            if (n < 100000000) return (n / 1e6).ToString("0.##", Inv) + "M";
            if (n < 1000000000) return (n / 1e6).ToString("0", Inv) + "M";
            return (n / 1e9).ToString("0.##", Inv) + "B";
        }

        public static string Span(TimeSpan t)
        {
            if (t.TotalSeconds < 0) return "now";
            if (t.TotalDays >= 1) return (int)t.TotalDays + "d " + t.Hours + "h";
            if (t.TotalHours >= 1) return (int)t.TotalHours + "h " + t.Minutes.ToString("00") + "m";
            if (t.TotalMinutes >= 1) return (int)t.TotalMinutes + "m";
            return (int)t.TotalSeconds + "s";
        }

        public static string Ago(TimeSpan t)
        {
            if (t.TotalSeconds < 5) return "just now";
            return Span(t) + " ago";
        }

        public static string Clock(DateTime local) { return local.ToString("HH:mm", Inv); }
        public static string Day(DateTime local) { return local.ToString("ddd", Inv); }

        public static string Model(string id)
        {
            var parts = id.Replace("claude-", "").Split('-');
            string fam = null;
            var nums = new List<string>();
            foreach (var p in parts)
            {
                if (p.Length > 0 && p.All(char.IsDigit)) { if (p.Length <= 2) nums.Add(p); }
                else if (fam == null && p.Length > 0) fam = char.ToUpperInvariant(p[0]) + p.Substring(1);
            }
            if (fam == null) return id;
            return nums.Count > 0 ? fam + " " + string.Join(".", nums) : fam;
        }

        public static string Family(string id)
        {
            var s = id.ToLowerInvariant();
            foreach (var f in new[] { "opus", "sonnet", "haiku", "fable" }) if (s.Contains(f)) return f;
            return "other";
        }
    }

    // ---------------------------------------------------------------- local transcripts

    class Rec
    {
        public DateTime Utc;
        public string Model;
        public long In, Out, CacheW, CacheR;
    }

    class Agg
    {
        public long Req, In, Out, CacheW, CacheR;
        public void Add(Rec r) { Req++; In += r.In; Out += r.Out; CacheW += r.CacheW; CacheR += r.CacheR; }
    }

    class LocalScanner
    {
        class FileState { public long Offset; }

        static readonly TimeSpan Keep = TimeSpan.FromDays(9);
        readonly Dictionary<string, FileState> files = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Rec> recs = new Dictionary<string, Rec>();
        readonly JavaScriptSerializer js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static bool WslRunning()
        {
            foreach (var name in new[] { "vmmemWSL", "vmmem" })
            {
                var ps = Process.GetProcessesByName(name);
                bool any = ps.Length > 0;
                foreach (var p in ps) p.Dispose();
                if (any) return true;
            }
            return false;
        }

        public static List<string> WslHomes()
        {
            var homes = new List<string>();
            if (!WslRunning()) return homes;
            try
            {
                using (var lxss = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (lxss == null) return homes;
                    foreach (var sub in lxss.GetSubKeyNames())
                    {
                        using (var k = lxss.OpenSubKey(sub))
                        {
                            var name = k == null ? null : k.GetValue("DistributionName") as string;
                            if (name == null || name.StartsWith("docker-desktop")) continue;
                            var root = @"\\wsl.localhost\" + name;
                            try { homes.AddRange(Directory.GetDirectories(root + @"\home")); } catch { }
                            homes.Add(root + @"\root");
                        }
                    }
                }
            }
            catch { }
            return homes;
        }

        public static List<string> ClaudeDirs()
        {
            var dirs = new List<string>();
            var cfg = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            dirs.Add(!string.IsNullOrEmpty(cfg) ? cfg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
            foreach (var h in WslHomes()) dirs.Add(Path.Combine(h, ".claude"));
            return dirs;
        }

        public Rec[] Scan()
        {
            var cutoff = DateTime.UtcNow - Keep;
            foreach (var dir in ClaudeDirs())
            {
                var projects = Path.Combine(dir, "projects");
                IEnumerable<string> found;
                try
                {
                    if (!Directory.Exists(projects)) continue;
                    found = Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories).ToList();
                }
                catch { continue; }

                foreach (var f in found)
                {
                    try
                    {
                        var info = new FileInfo(f);
                        FileState st;
                        if (!files.TryGetValue(f, out st))
                        {
                            if (info.LastWriteTimeUtc < cutoff) continue;
                            st = new FileState();
                            files[f] = st;
                        }
                        if (info.Length < st.Offset) st.Offset = 0;
                        if (info.Length > st.Offset) ReadFrom(f, st);
                    }
                    catch { }
                }
            }

            foreach (var key in recs.Where(kv => kv.Value.Utc < cutoff).Select(kv => kv.Key).ToList()) recs.Remove(key);
            return recs.Values.ToArray();
        }

        void ReadFrom(string path, FileState st)
        {
            byte[] buf;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                fs.Seek(st.Offset, SeekOrigin.Begin);
                var len = fs.Length - st.Offset;
                if (len <= 0 || len > int.MaxValue) return;
                buf = new byte[len];
                int read = 0, n;
                while (read < buf.Length && (n = fs.Read(buf, read, buf.Length - read)) > 0) read += n;
                if (read < buf.Length) Array.Resize(ref buf, read);
            }
            int end = Array.LastIndexOf(buf, (byte)'\n');
            if (end < 0) return; // only consume complete lines
            st.Offset += end + 1;
            var text = Encoding.UTF8.GetString(buf, 0, end);
            foreach (var line in text.Split('\n')) Parse(line);
        }

        void Parse(string line)
        {
            if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 ||
                line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return;
            object d;
            try { d = js.DeserializeObject(line); } catch { return; }
            if (J.Str(J.Get(d, "type")) != "assistant") return;
            var msg = J.Get(d, "message");
            var usage = J.Get(msg, "usage");
            var model = J.Str(J.Get(msg, "model"));
            var ts = J.Time(J.Get(d, "timestamp"));
            if (usage == null || model == null || model.StartsWith("<") || ts == null) return;

            var key = J.Str(J.Get(msg, "id")) + "|" + J.Str(J.Get(d, "requestId"));
            var r = new Rec
            {
                Utc = ts.Value,
                Model = model,
                In = J.Long(J.Get(usage, "input_tokens")),
                Out = J.Long(J.Get(usage, "output_tokens")),
                CacheW = J.Long(J.Get(usage, "cache_creation_input_tokens")),
                CacheR = J.Long(J.Get(usage, "cache_read_input_tokens")),
            };
            Rec prev;
            if (recs.TryGetValue(key, out prev))
            {
                // Claude Code logs one line per content block; keep the most complete usage.
                prev.In = Math.Max(prev.In, r.In);
                prev.Out = Math.Max(prev.Out, r.Out);
                prev.CacheW = Math.Max(prev.CacheW, r.CacheW);
                prev.CacheR = Math.Max(prev.CacheR, r.CacheR);
            }
            else recs[key] = r;
        }
    }

    // ---------------------------------------------------------------- plan limits API

    class Creds
    {
        public string Token, Plan;
        public DateTime ExpiresUtc;

        static Creds Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var d = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path));
                var o = J.Get(d, "claudeAiOauth");
                var tok = J.Str(J.Get(o, "accessToken"));
                if (tok == null) return null;
                var sub = J.Str(J.Get(o, "subscriptionType")) ?? "";
                var tier = J.Str(J.Get(o, "rateLimitTier")) ?? "";
                return new Creds
                {
                    Token = tok,
                    Plan = PlanName(sub, tier),
                    ExpiresUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(J.Num(J.Get(o, "expiresAt"), 0)),
                };
            }
            catch { return null; }
        }

        public static string PlanName(string sub, string tier)
        {
            sub = sub ?? ""; tier = tier ?? "";
            if (tier.Contains("max_20x")) return "Max 20x";
            if (tier.Contains("max_5x")) return "Max 5x";
            if (sub.StartsWith("claude_")) sub = sub.Substring(7);
            return sub.Length > 0 ? char.ToUpperInvariant(sub[0]) + sub.Substring(1) : "Claude";
        }

        // Read-only: never refreshes the token (that would rotate Claude Code's refresh token under it).
        public static Creds Best()
        {
            var all = LocalScanner.ClaudeDirs().Select(d => Load(Path.Combine(d, ".credentials.json")))
                .Where(c => c != null).ToList();
            var valid = all.FirstOrDefault(c => c.ExpiresUtc > DateTime.UtcNow.AddMinutes(1));
            return valid ?? all.OrderByDescending(c => c.ExpiresUtc).FirstOrDefault();
        }
    }

    class Limit
    {
        public string Kind, Label, Severity;
        public double Pct;
        public DateTime? ResetUtc;
    }

    class ApiResult
    {
        public object Raw;
        public string Error, Plan;
        public bool RateLimited;
        public DateTime FetchedUtc;
        public TimeSpan? Skew;

        const string Url = "https://api.anthropic.com/api/oauth/usage";
        const string ProfileUrl = "https://api.anthropic.com/api/oauth/profile";

        // The tier cached in .credentials.json is frozen at login time; the profile endpoint is current.
        static string profilePlan;
        static DateTime profileAt = DateTime.MinValue;

        static HttpWebRequest Request(string url, string token)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
            req.Accept = "application/json";
            req.UserAgent = "claude-usage-tray/1.0";
            req.Timeout = 15000;
            return req;
        }

        static void RefreshPlan(string token)
        {
            if (DateTime.UtcNow - profileAt < TimeSpan.FromMinutes(30)) return;
            profileAt = DateTime.UtcNow;
            try
            {
                using (var resp = (HttpWebResponse)Request(ProfileUrl, token).GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    var org = J.Get(new JavaScriptSerializer().DeserializeObject(rd.ReadToEnd()), "organization");
                    var tier = J.Str(J.Get(org, "rate_limit_tier"));
                    if (tier != null) profilePlan = Creds.PlanName(J.Str(J.Get(org, "organization_type")), tier);
                }
            }
            catch { }
        }

        public static ApiResult Fetch()
        {
            var res = new ApiResult { FetchedUtc = DateTime.UtcNow };
            try
            {
                var c = Creds.Best();
                if (c == null) { res.Error = "No Claude Code login found"; return res; }
                RefreshPlan(c.Token);
                res.Plan = profilePlan ?? c.Plan;
                var req = Request(Url, c.Token);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var rd = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    DateTime server;
                    if (DateTime.TryParse(resp.Headers["Date"], CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out server))
                        res.Skew = server - DateTime.UtcNow;
                    res.Raw = new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.DeserializeObject(rd.ReadToEnd());
                }
            }
            catch (WebException we)
            {
                var hr = we.Response as HttpWebResponse;
                int code = hr == null ? 0 : (int)hr.StatusCode;
                if (code == 401) res.Error = "Login expired — use Claude Code once to refresh it";
                else if (code == 429) { res.Error = "Rate limited — next try in 10 min"; res.RateLimited = true; }
                else res.Error = code > 0 ? "API error " + code : "Offline";
            }
            catch (Exception e) { res.Error = e.Message; }
            return res;
        }

        public List<Limit> Limits()
        {
            var list = new List<Limit>();
            var arr = J.Get(Raw, "limits") as object[];
            if (arr != null)
            {
                foreach (var l in arr)
                {
                    var kind = J.Str(J.Get(l, "kind")) ?? "";
                    string label;
                    if (kind == "session") label = "Session · 5h";
                    else if (kind == "weekly_all") label = "Week · all models";
                    else if (kind == "weekly_scoped")
                        label = "Week · " + (J.Str(J.Get(l, "scope", "model", "display_name"))
                                              ?? J.Str(J.Get(l, "scope", "surface", "display_name")) ?? "scoped");
                    else label = kind.Replace('_', ' ');
                    list.Add(new Limit
                    {
                        Kind = kind, Label = label,
                        Pct = J.Num(J.Get(l, "percent"), 0),
                        Severity = J.Str(J.Get(l, "severity")) ?? "normal",
                        ResetUtc = J.Time(J.Get(l, "resets_at")),
                    });
                }
                return list;
            }
            // Older response shape.
            foreach (var pair in new[] { new[] { "five_hour", "Session · 5h", "session" }, new[] { "seven_day", "Week · all models", "weekly_all" },
                                         new[] { "seven_day_opus", "Week · Opus", "weekly_scoped" }, new[] { "seven_day_sonnet", "Week · Sonnet", "weekly_scoped" } })
            {
                var o = J.Get(Raw, pair[0]);
                if (o == null) continue;
                list.Add(new Limit { Kind = pair[2], Label = pair[1], Pct = J.Num(J.Get(o, "utilization"), 0), Severity = "normal", ResetUtc = J.Time(J.Get(o, "resets_at")) });
            }
            return list;
        }
    }

    // ---------------------------------------------------------------- theme

    class Palette
    {
        public Color Bg, Text, Secondary, Tertiary, Track, Hairline, Accent, Ink, Warn, Bad, Good, Sonnet, Haiku, Fable;
        public bool Light;
        public static string Force; // "light" / "dark" override (snapshots)

        static bool RegFlag(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var v = k == null ? null : k.GetValue(name);
                    return v is int && (int)v == 1;
                }
            }
            catch { return false; }
        }

        public static bool TaskbarLight() { return RegFlag("SystemUsesLightTheme"); }

        public static Palette Current()
        {
            bool light = Force != null ? Force == "light" : RegFlag("AppsUseLightTheme");
            return light
                ? new Palette
                {
                    Light = true, Bg = C(0xFBFAF7), Text = C(0x191817), Secondary = C(0x6B6862), Tertiary = C(0xA29F98),
                    Track = C(0xEFEDE8), Hairline = C(0xE6E3DD), Accent = C(0xD4613C), Ink = C(0x2B2926),
                    Warn = C(0xC98A1B), Bad = C(0xD64541), Good = C(0x3E9B5A),
                    Sonnet = C(0x5B84AE), Haiku = C(0x5E9468), Fable = C(0x8D6CB5),
                }
                : new Palette
                {
                    Bg = C(0x1C1B1A), Text = C(0xF3F1EC), Secondary = C(0xA8A59D), Tertiary = C(0x6F6D67),
                    Track = C(0x2D2C2A), Hairline = C(0x2E2D2B), Accent = C(0xE07A55), Ink = C(0xE9E4D8),
                    Warn = C(0xF0B44C), Bad = C(0xF0605A), Good = C(0x7CC48B),
                    Sonnet = C(0x7FA3C7), Haiku = C(0x93B99A), Fable = C(0xB79BD6),
                };
        }

        static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R * t + b.R * (1 - t)), (int)(a.G * t + b.G * (1 - t)), (int)(a.B * t + b.B * (1 - t)));
        }

        // Quiet, non-accent bars.
        public Color Faint { get { return Mix(Text, Bg, Light ? 0.20f : 0.24f); } }

        // Pastel heat scale: green at 0% through amber to red at 100%.
        public Color Usage(double pct)
        {
            double t = Math.Max(0, Math.Min(100, pct)) / 100.0;
            double hue = 135 - 131 * t;
            return Hsl(hue, Light ? 0.52 : 0.62, Light ? 0.55 : 0.70);
        }

        static Color Hsl(double h, double s, double l)
        {
            double c = (1 - Math.Abs(2 * l - 1)) * s, hp = h / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; } else if (hp < 2) { r = x; g = c; } else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; } else if (hp < 5) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
        }

        public Color ForLimit(Limit l)
        {
            return Usage(l.Severity == "critical" || l.Severity == "exceeded" ? 100 : l.Pct);
        }

        public Color ForFamily(string fam)
        {
            switch (fam)
            {
                case "opus": return Accent;
                case "sonnet": return Sonnet;
                case "haiku": return Haiku;
                case "fable": return Fable;
                default: return Tertiary;
            }
        }
    }

    // ---------------------------------------------------------------- tray app

    class TrayApp : ApplicationContext
    {
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "ClaudeUsageTray";

        readonly NotifyIcon tray = new NotifyIcon();
        readonly Popup popup;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly LocalScanner scanner = new LocalScanner();
        readonly ToolStripMenuItem startupItem;

        public Rec[] Recs = new Rec[0];
        public ApiResult Api;
        public ApiResult LastGood;
        public DateTime? LastScanUtc;
        public string Plan = "";
        public TimeSpan Skew = TimeSpan.Zero;

        readonly string snapshot, snapHover;
        int ticks;
        bool scanning, fetching;
        // Polling policy for /api/oauth/usage (an undocumented endpoint that rate limits per token):
        // fetch once at launch, then every PollMinutes, plus whenever the user clicks Refresh.
        // Nothing else triggers a fetch: opening the popup only rescans local transcripts, and
        // errors (429 included) wait for the next regular poll rather than retrying sooner.
        // A Refresh click restarts the 10-minute cycle. See README.md.
        const int PollMinutes = 10;
        DateTime nextFetchUtc = DateTime.MinValue;
        readonly HashSet<string> notified = new HashSet<string>();

        public DateTime NowUtc { get { return DateTime.UtcNow + Skew; } }
        public DateTime NowLocal { get { return ToLocal(NowUtc); } }
        public DateTime ToLocal(DateTime utc) { return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local); }

        public TrayApp(string[] args)
        {
            if (args.Length >= 2 && args[0] == "--snapshot")
            {
                snapshot = args[1];
                if (args.Length >= 3) Palette.Force = args[2];
                if (args.Length >= 4) snapHover = args[3];
            }
            popup = new Popup(this);
            var h = popup.Handle; // create handle so BeginInvoke works before first show

            var menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { RefreshAll(true); });
            menu.Items.Add("Open usage page", null, delegate { Open("https://claude.ai/settings/usage"); });
            menu.Items.Add(new ToolStripSeparator());
            startupItem = new ToolStripMenuItem("Start with Windows", null, delegate { ToggleStartup(); });
            startupItem.Checked = StartupEnabled();
            menu.Items.Add(startupItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitThread(); });

            tray.ContextMenuStrip = menu;
            tray.Text = "Claude usage — loading";
            SetIcon(-1, 0);
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) popup.Toggle(); };

            timer.Tick += delegate { Tick(); };
            timer.Start();
            RefreshAll(true);
        }

        protected override void ExitThreadCore()
        {
            timer.Stop();
            tray.Visible = false;
            tray.Dispose();
            popup.Dispose();
            base.ExitThreadCore();
        }

        void Tick()
        {
            ticks++;
            if (snapshot != null && ticks == 6) { popup.ForceHover = snapHover; popup.Rebuild(); popup.SaveFrame(snapshot); ExitThread(); return; }
            if (ticks % 20 == 0) Scan();
            if (DateTime.UtcNow >= nextFetchUtc) Fetch();
            if (popup.Visible) popup.Rebuild();
            if (ticks % 30 == 0) UpdateTray();
        }

        public void RefreshAll(bool force)
        {
            if (force) nextFetchUtc = DateTime.MinValue;
            Scan();
            Fetch();
        }

        // Popup opened: refresh local transcript stats only; the API stays on its schedule.
        public void Rescan() { Scan(); }

        void Scan()
        {
            if (scanning) return;
            scanning = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Rec[] r = null;
                try { r = scanner.Scan(); } catch { }
                popup.BeginInvoke((Action)delegate
                {
                    scanning = false;
                    if (r != null) { Recs = r; LastScanUtc = DateTime.UtcNow; }
                    Trim();
                    if (popup.Visible) popup.Rebuild();
                });
            });
        }

        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr h);

        // Parsing transcripts leaves big transient buffers behind; give them back to the OS.
        public static void Trim()
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); } catch { }
        }

        void Fetch()
        {
            if (fetching) return;
            fetching = true;
            nextFetchUtc = DateTime.UtcNow.AddMinutes(PollMinutes);
            ThreadPool.QueueUserWorkItem(delegate
            {
                var res = ApiResult.Fetch();
                popup.BeginInvoke((Action)delegate
                {
                    fetching = false;
                    Api = res;
                    if (res.Plan != null) Plan = res.Plan;
                    if (res.Skew.HasValue) Skew = Math.Abs(res.Skew.Value.TotalSeconds) > 30 ? res.Skew.Value : TimeSpan.Zero;
                    if (res.Raw != null) LastGood = res;
                    UpdateTray();
                    Notify();
                    if (popup.Visible) popup.Rebuild();
                });
            });
        }

        public List<Limit> Limits() { return LastGood == null ? new List<Limit>() : LastGood.Limits(); }

        void UpdateTray()
        {
            var limits = Limits();
            var pal = Palette.Current();
            var session = limits.FirstOrDefault(l => l.Kind == "session");
            var week = limits.FirstOrDefault(l => l.Kind == "weekly_all");
            if (session == null) { SetIcon(-1, 0); tray.Text = Trim("Claude usage — " + (Api != null && Api.Error != null ? Api.Error : "no data")); return; }

            var worst = limits.OrderByDescending(l => l.Pct).First();
            bool critical = worst.Severity == "critical" || worst.Severity == "exceeded";
            SetIcon(session.Pct, critical ? 100 : worst.Pct);
            var text = "Claude · 5h " + Math.Round(session.Pct) + "%";
            if (session.ResetUtc.HasValue) text += " (" + Fmt.Span(session.ResetUtc.Value - NowUtc) + ")";
            if (week != null) text += " · week " + Math.Round(week.Pct) + "%";
            if (Api != null && Api.Error != null) text += " · stale";
            tray.Text = Trim(text);
        }

        static string Trim(string s) { return s.Length > 63 ? s.Substring(0, 62) + "…" : s; }

        void Notify()
        {
            foreach (var l in Limits())
            {
                foreach (var t in new[] { 80, 95 })
                {
                    if (l.Pct < t) continue;
                    var key = l.Kind + l.Label + t + (l.ResetUtc.HasValue ? l.ResetUtc.Value.Ticks.ToString() : "");
                    if (!notified.Add(key)) continue;
                    var reset = l.ResetUtc.HasValue ? " Resets in " + Fmt.Span(l.ResetUtc.Value - NowUtc) + "." : "";
                    tray.ShowBalloonTip(5000, "Claude: " + l.Label + " at " + Math.Round(l.Pct) + "%", "You've used " + Math.Round(l.Pct) + "% of this limit." + reset, ToolTipIcon.Warning);
                }
            }
        }

        // A monochrome glyph that sits with the system tray icons: a hairline dial with a pie
        // filling clockwise from twelve. Colour appears only as a signal (amber 80%+, red 95%+);
        // exact figures live in the tooltip. pct < 0 means no data.
        void SetIcon(double pct, double worst)
        {
            int s = SystemInformation.SmallIconSize.Width;
            bool lightBar = Palette.TaskbarLight();
            var fg = lightBar ? Color.FromArgb(28, 28, 28) : Color.FromArgb(245, 245, 245);
            Color tint = worst >= 95 ? (lightBar ? Color.FromArgb(0xD6, 0x45, 0x41) : Color.FromArgb(0xF0, 0x60, 0x5A))
                       : worst >= 80 ? (lightBar ? Color.FromArgb(0xC9, 0x8A, 0x1B) : Color.FromArgb(0xF0, 0xB4, 0x4C))
                       : fg;
            const int ss = 4;   // drawn at 4x and downsampled, for clean edges at 16px
            using (var big = new Bitmap(s * ss, s * ss, PixelFormat.Format32bppPArgb))
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float u = s * ss / 16f;   // one unit = one pixel of a 16px icon
                    float c = 8 * u;

                    float ringR = 7f * u, ringW = 1.25f * u;
                    var ringRect = new RectangleF(c - ringR, c - ringR, 2 * ringR, 2 * ringR);
                    if (pct >= 100)
                    {
                        // Limit reached: the dial closes into one solid disc.
                        float R = ringR + ringW / 2;
                        using (var b = new SolidBrush(tint)) g.FillEllipse(b, c - R, c - R, 2 * R, 2 * R);
                    }
                    else
                    {
                        var ringColor = Color.FromArgb(pct < 0 ? (lightBar ? 90 : 110) : (lightBar ? 150 : 170), fg);
                        using (var p = new Pen(ringColor, ringW)) g.DrawEllipse(p, ringRect);
                    }

                    if (pct > 0 && pct < 100)
                    {
                        float pieR = 4.9f * u;
                        var pieRect = new RectangleF(c - pieR, c - pieR, 2 * pieR, 2 * pieR);
                        // A hint of fill even at 1%, so "in use" never reads as "empty".
                        float sweep = Math.Max(8f, (float)(Math.Min(100, pct) * 3.6));
                        using (var b = new SolidBrush(tint))
                            g.FillPie(b, pieRect.X, pieRect.Y, pieRect.Width, pieRect.Height, -90f, sweep);
                    }
                }
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(big, new Rectangle(0, 0, s, s));
                }
                var hicon = bmp.GetHicon();
                var icon = (Icon)Icon.FromHandle(hicon).Clone();
                DestroyIcon(hicon);
                var old = tray.Icon;
                tray.Icon = icon;
                if (old != null) old.Dispose();
            }
        }

        static void Open(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        static bool StartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
        }

        void ToggleStartup()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (StartupEnabled()) k.DeleteValue(RunName, false);
                else k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
            }
            startupItem.Checked = StartupEnabled();
        }
    }

    // ---------------------------------------------------------------- popup

    class Popup : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        class Hit { public RectangleF R; public string Key; public Action Click; }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly StringFormat Typo = MakeTypo();
        static StringFormat MakeTypo()
        {
            var f = (StringFormat)StringFormat.GenericTypographic.Clone();
            f.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
            return f;
        }

        readonly TrayApp app;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        readonly List<Hit> hits = new List<Hit>();
        Bitmap frame;
        DateTime hiddenAt = DateTime.MinValue, animStart;
        float k = 1f, a = 1f, slide;
        string hover;
        int borderColor = -1;
        public string ForceHover;

        // per-render state
        Graphics g;
        Palette pal;
        float P, W, fontK;
        Font fHead, fHeadLight, fHero, fUnit, fHero2, fUnit2, fLabel, fBody, fSmall;

        public Popup(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            KeyPreview = true;
            Text = "Claude usage";
            Deactivate += delegate { HidePopup(); };
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) HidePopup(); };
            MouseMove += (s, e) => SetHover(HitAt(e.Location));
            MouseLeave += delegate { SetHover(null); };
            MouseClick += (s, e) => { var h = HitAt(e.Location); if (h != null && h.Click != null) h.Click(); };
            anim.Tick += delegate { AnimStep(); };
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80;        // WS_EX_TOOLWINDOW: keep out of Alt+Tab
                cp.ClassStyle |= 0x20000;  // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4); } catch { } // rounded corners
        }

        Hit HitAt(Point p)
        {
            for (int i = hits.Count - 1; i >= 0; i--) if (hits[i].R.Contains(p)) return hits[i];
            return null;
        }

        void SetHover(Hit h)
        {
            Cursor = h != null && h.Click != null ? Cursors.Hand : Cursors.Default;
            var key = h == null ? null : h.Key;
            if (key == hover) return;
            hover = key;
            if (!anim.Enabled) Rebuild();
        }

        void HidePopup()
        {
            if (!Visible) return;
            anim.Stop();
            Hide();
            hiddenAt = DateTime.UtcNow;
            hover = null;
            var old = frame; frame = null; if (old != null) old.Dispose();
            TrayApp.Trim();
        }

        public void Toggle()
        {
            if (Visible) { HidePopup(); return; }
            if ((DateTime.UtcNow - hiddenAt).TotalMilliseconds < 250) return; // click on tray icon that just closed us
            app.Rescan();
            a = 0; slide = 1; animStart = DateTime.UtcNow;
            Rebuild();
            Show();
            Activate();
            SetForegroundWindow(Handle);
            anim.Start();
        }

        static float Ease(double t) { t = Math.Max(0, Math.Min(1, t)); return (float)(1 - Math.Pow(1 - t, 3)); }

        void AnimStep()
        {
            double ms = (DateTime.UtcNow - animStart).TotalMilliseconds;
            a = Ease(ms / 700);
            slide = 1 - Ease(ms / 240);
            if (ms >= 700) { a = 1; slide = 0; anim.Stop(); }
            Rebuild();
        }

        void Place()
        {
            var scr = Screen.FromPoint(Cursor.Position);
            var wa = scr.WorkingArea;
            var b = scr.Bounds;
            int m = (int)(12 * k), off = (int)(slide * 14 * k);
            int x = wa.Right - Width - m, y = wa.Bottom - Height - m + off;
            if (wa.Top > b.Top) y = wa.Top + m - off;
            if (wa.Left > b.Left) x = wa.Left + m;
            Location = new Point(x, y);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (frame != null) e.Graphics.DrawImageUnscaled(frame, 0, 0);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        public void SaveFrame(string path) { using (var b = frame.Clone(new Rectangle(0, 0, Width, Height), frame.PixelFormat)) b.Save(path); }

        static Font F(string family, float px, string fallback)
        {
            try { using (new FontFamily(family)) { } return new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font(fallback, px, FontStyle.Regular, GraphicsUnit.Pixel); }
        }

        void EnsureFonts()
        {
            if (fHead != null && fontK == k) return;
            foreach (var f in new[] { fHead, fHeadLight, fHero, fUnit, fHero2, fUnit2, fLabel, fBody, fSmall }) if (f != null) f.Dispose();
            fontK = k;
            fHead = F("Segoe UI Variable Text Semibold", 14 * k, "Segoe UI Semibold");
            fHeadLight = F("Segoe UI Variable Text", 14 * k, "Segoe UI");
            fHero = F("Segoe UI Variable Display Semib", 44 * k, "Segoe UI Semibold");
            fUnit = F("Segoe UI Variable Display", 20 * k, "Segoe UI");
            fHero2 = F("Segoe UI Variable Display Semib", 22 * k, "Segoe UI Semibold");
            fUnit2 = F("Segoe UI Variable Display", 13 * k, "Segoe UI");
            fLabel = F("Segoe UI Variable Text Semibold", 12.5f * k, "Segoe UI Semibold");
            fBody = F("Segoe UI Variable Text", 12.5f * k, "Segoe UI");
            fSmall = F("Segoe UI Variable Small", 11.5f * k, "Segoe UI");
        }

        public void Rebuild()
        {
            if (ForceHover != null) hover = ForceHover;
            k = DeviceDpi / 96f;
            pal = Palette.Current();
            EnsureFonts();
            W = (float)Math.Round(348 * k);
            P = 24 * k;
            int maxH = Screen.FromPoint(Cursor.Position).WorkingArea.Height - (int)(24 * k);
            if (frame == null || frame.Width != (int)W || frame.Height != maxH)
            {
                if (frame != null) frame.Dispose();
                frame = new Bitmap((int)W, Math.Max(300, maxH));
            }
            int h;
            using (g = Graphics.FromImage(frame))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(pal.Bg);
                h = Math.Min(frame.Height, (int)Math.Ceiling(Render()));
            }
            g = null;

            int cr = pal.Hairline.R | (pal.Hairline.G << 8) | (pal.Hairline.B << 16);
            if (cr != borderColor && IsHandleCreated)
            {
                borderColor = cr;
                try { DwmSetWindowAttribute(Handle, 34, ref cr, 4); } catch { } // DWMWA_BORDER_COLOR
            }
            Size = new Size((int)W, h);
            Place();
            Invalidate();
        }

        // ---- drawing primitives (y values are text baselines)

        float Measure(string s, Font f) { return g.MeasureString(s, f, PointF.Empty, Typo).Width; }

        static float Asc(Font f)
        {
            var ff = f.FontFamily;
            return f.Size * ff.GetCellAscent(f.Style) / ff.GetEmHeight(f.Style);
        }

        float Txt(string s, Font f, Color c, float x, float baseline)
        {
            using (var b = new SolidBrush(c)) g.DrawString(s, f, b, x, baseline - Asc(f), Typo);
            return Measure(s, f);
        }

        float TxtR(string s, Font f, Color c, float right, float baseline)
        {
            float w = Measure(s, f);
            Txt(s, f, c, right - w, baseline);
            return w;
        }

        void TxtC(string s, Font f, Color c, float cx, float baseline) { Txt(s, f, c, cx - Measure(s, f) / 2, baseline); }

        void Capsule(RectangleF r, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            float d = Math.Min(r.Width, r.Height);
            using (var p = new GraphicsPath())
            using (var b = new SolidBrush(c))
            {
                if (r.Width >= r.Height)
                {
                    p.AddArc(r.X, r.Y, d, d, 90, 180);
                    p.AddArc(r.Right - d, r.Y, d, d, 270, 180);
                }
                else
                {
                    p.AddArc(r.X, r.Y, d, d, 180, 180);
                    p.AddArc(r.X, r.Bottom - d, d, d, 0, 180);
                }
                p.CloseFigure();
                g.FillPath(b, p);
            }
        }

        void Dot(float cx, float cy, float r, Color c)
        {
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
        }

        // Arc shaded along its length on the usage scale, so the tip's colour is where you are.
        void Ring(RectangleF r, float stroke, double pct)
        {
            var rr = RectangleF.Inflate(r, -stroke / 2, -stroke / 2);
            using (var p = new Pen(pal.Track, stroke)) g.DrawEllipse(p, rr);
            if (pct <= 0 || rr.Width <= 0 || rr.Height <= 0) return;
            float sweep = (float)(Math.Min(100, pct) * 3.6);
            // Equal segments, never a sliver: GDI+ throws OutOfMemoryException on near-zero sweeps
            // (seen on the first animation frames and when float steps drift past the end).
            if (sweep >= 0.5f)
            {
                int n = (int)Math.Ceiling(sweep / 3f);
                float seg = sweep / n;
                for (int i = 0; i < n; i++)
                {
                    float s = i * seg;
                    using (var p = new Pen(pal.Usage((s + seg / 2) / 3.6), stroke))
                        g.DrawArc(p, rr, -90 + s, seg + (i < n - 1 ? 0.8f : 0));
                }
            }
            float R = rr.Width / 2, cx = rr.X + R, cy = rr.Y + R;
            Dot(cx, cy - R, stroke / 2, pal.Usage(0));
            double end = (sweep - 90) * Math.PI / 180;
            Dot(cx + R * (float)Math.Cos(end), cy + R * (float)Math.Sin(end), stroke / 2, pal.Usage(pct));
        }

        void Add(RectangleF r, string key, Action click) { hits.Add(new Hit { R = r, Key = key, Click = click }); }

        int HoverIndex(string prefix)
        {
            int i;
            if (hover != null && hover.StartsWith(prefix) && int.TryParse(hover.Substring(prefix.Length), out i)) return i;
            return -1;
        }

        // Section title on the left, a quiet "readout" on the right that hover rewrites.
        float Section(float y, string title, string readout, bool live)
        {
            y += 30 * k;
            float room = W - 2 * P - Txt(title, fLabel, pal.Text, P, y) - 16 * k;
            if (readout != null)
            {
                // Drop trailing "  ·  " parts until the readout fits beside the title.
                var parts = readout.Split(new[] { "  ·  " }, StringSplitOptions.None).ToList();
                while (parts.Count > 1 && Measure(string.Join("  ·  ", parts), fSmall) > room) parts.RemoveAt(parts.Count - 1);
                TxtR(string.Join("  ·  ", parts), fSmall, live ? pal.Text : pal.Tertiary, W - P, y);
            }
            return y + 14 * k;
        }

        static string N(long n) { return n.ToString("#,0", Inv); }

        string DayName(DateTime local, DateTime now)
        {
            if (local.Date == now.Date) return "Today";
            if (local.Date == now.Date.AddDays(-1)) return "Yesterday";
            return local.ToString("ddd", Inv);
        }

        // ---- layout

        float Render()
        {
            hits.Clear();
            var now = app.NowLocal;
            float cw = W - 2 * P;
            float y;

            // header ------------------------------------------------------
            y = 34 * k;
            float x = P + Txt("Claude", fHead, pal.Text, P, y);
            if (app.Plan.Length > 0) Txt(app.Plan, fHeadLight, pal.Tertiary, x + 7 * k, y);

            string st;
            Color dot;
            if (app.Api == null) { st = "Syncing"; dot = pal.Tertiary; }
            else if (app.Api.Error != null) { st = "Stale"; dot = pal.Warn; }
            else
            {
                var ago = DateTime.UtcNow - app.Api.FetchedUtc;
                st = ago.TotalSeconds < 60 ? "Live" : Fmt.Span(ago) + " ago";
                dot = pal.Good;
            }
            bool hovRefresh = hover == "refresh";
            var stText = hovRefresh ? "Refresh" : st;
            float sw = Measure(stText, fSmall);
            float sx = W - P - sw;
            Txt(stText, fSmall, hovRefresh ? pal.Text : pal.Tertiary, sx, y - 1 * k);
            Dot(sx - 8 * k, y - 5 * k, 3 * k, dot);
            Add(new RectangleF(sx - 16 * k, y - 18 * k, sw + 20 * k, 26 * k), "refresh", delegate { app.RefreshAll(true); });

            if (app.Api != null && app.Api.Error != null)
            {
                y += 20 * k;
                Txt(app.Api.Error, fSmall, pal.Warn, P, y);
            }
            if (app.Skew != TimeSpan.Zero)
            {
                y += 18 * k;
                Txt("System clock is " + Fmt.Span(app.Skew.Duration()) + (app.Skew.Ticks > 0 ? " behind" : " ahead") + " · corrected", fSmall, pal.Warn, P, y);
            }

            // hero: concentric rings ----------------------------------------
            var limits = app.Limits();
            var ses = limits.FirstOrDefault(l => l.Kind == "session");
            var wk = limits.FirstOrDefault(l => l.Kind == "weekly_all");
            float top = y + 26 * k;
            float D = 118 * k, stroke = 11 * k, gap = 3 * k;
            var outer = new RectangleF(P, top + 4 * k, D, D);
            var inner = RectangleF.Inflate(outer, -(stroke + gap), -(stroke + gap));
            var sesColor = pal.Usage(ses == null ? 0 : ses.Pct);
            var wkColor = pal.Usage(wk == null ? 0 : wk.Pct);
            Ring(outer, stroke, ses == null ? 0 : ses.Pct * a);
            Ring(inner, stroke, wk == null ? 0 : wk.Pct * a);

            float rx = P + D + 26 * k;
            float b1 = top + 16 * k;
            Dot(rx + 3.5f * k, b1 - 4.5f * k, 3.5f * k, sesColor);
            Txt("Session", fLabel, pal.Secondary, rx + 13 * k, b1);
            float b2 = b1 + 44 * k;
            var sesNum = ses == null ? "–" : Math.Round(ses.Pct * a).ToString(Inv);
            float nw = Txt(sesNum, fHero, pal.Text, rx - 2 * k, b2);
            if (ses != null) Txt("%", fUnit, pal.Secondary, rx - 2 * k + nw + 2 * k, b2);
            float b3 = b2 + 20 * k;
            if (ses != null && ses.ResetUtc.HasValue)
                Txt("Resets in " + Fmt.Span(ses.ResetUtc.Value - app.NowUtc) + " · " + Fmt.Clock(app.ToLocal(ses.ResetUtc.Value)), fSmall, pal.Tertiary, rx, b3);
            float b4 = b3 + 26 * k;
            Dot(rx + 3.5f * k, b4 - 4.5f * k, 3.5f * k, wkColor);
            Txt("Week", fLabel, pal.Secondary, rx + 13 * k, b4);
            float b5 = b4 + 25 * k;
            var wkNum = wk == null ? "–" : Math.Round(wk.Pct * a).ToString(Inv);
            float ww = Txt(wkNum, fHero2, pal.Text, rx - 1 * k, b5);
            if (wk != null)
            {
                ww += Txt("%", fUnit2, pal.Secondary, rx - 1 * k + ww + 1 * k, b5) + 1 * k;
                if (wk.ResetUtc.HasValue)
                {
                    var wr = app.ToLocal(wk.ResetUtc.Value);
                    Txt("  Resets " + Fmt.Day(wr) + " " + Fmt.Clock(wr), fSmall, pal.Tertiary, rx - 1 * k + ww, b5);
                }
            }
            y = Math.Max(outer.Bottom, b5 + 6 * k);

            // other limits (scoped weeks etc.)
            foreach (var l in limits.Where(l => l.Kind != "session" && l.Kind != "weekly_all"))
            {
                y += 26 * k;
                var name = l.Label.StartsWith("Week · ") ? l.Label.Substring(7) + " this week" : l.Label;
                Txt(name, fBody, pal.Secondary, P, y);
                TxtR(Math.Round(l.Pct) + "%", fBody, pal.Text, W - P, y);
                Capsule(new RectangleF(P, y + 8 * k, cw, 3 * k), pal.Track);
                if (l.Pct > 0) Capsule(new RectangleF(P, y + 8 * k, Math.Max(3 * k, (float)(cw * Math.Min(100, l.Pct) / 100 * a)), 3 * k), pal.ForLimit(l));
                y += 8 * k;
            }

            var extra = app.LastGood == null ? null : J.Get(app.LastGood.Raw, "extra_usage");
            if (extra != null && J.Get(extra, "is_enabled") is bool && (bool)J.Get(extra, "is_enabled"))
            {
                double div = Math.Pow(10, J.Num(J.Get(extra, "decimal_places"), 2));
                y += 24 * k;
                Txt("Extra usage", fBody, pal.Secondary, P, y);
                TxtR((J.Str(J.Get(extra, "currency")) ?? "") + " " + (J.Num(J.Get(extra, "used_credits"), 0) / div).ToString("0.00", Inv)
                     + " of " + (J.Num(J.Get(extra, "monthly_limit"), 0) / div).ToString("0.00", Inv), fBody, pal.Text, W - P, y);
            }

            // local stats ------------------------------------------------------
            var recs = app.Recs;
            var todayStart = now.Date;
            var weekStart = todayStart.AddDays(-6);
            var today = new Agg();
            var hours = new Agg[24];
            var days = new Agg[7];
            for (int i = 0; i < 24; i++) hours[i] = new Agg();
            for (int i = 0; i < 7; i++) days[i] = new Agg();
            var models = new Dictionary<string, Agg>();
            foreach (var r in recs)
            {
                var lt = app.ToLocal(r.Utc);
                if (lt >= todayStart) { today.Add(r); hours[lt.Hour].Add(r); }
                if (lt < weekStart) continue;
                int di = (int)(lt.Date - weekStart).TotalDays;
                if (di >= 0 && di < 7) days[di].Add(r);
                var name = Fmt.Model(r.Model);
                Agg ag;
                if (!models.TryGetValue(name, out ag)) models[name] = ag = new Agg();
                ag.Add(r);
            }

            // today, by hour ------------------------------------------------------
            int hh = HoverIndex("h:");
            string readout = hh >= 0
                ? hh.ToString("00") + ":00  ·  " + Fmt.Tokens(hours[hh].Out) + " out  ·  " + N(hours[hh].Req) + " req"
                : (app.LastScanUtc == null ? "Reading transcripts…" : Fmt.Tokens(today.Out) + " out  ·  " + N(today.Req) + " requests");
            y = Section(y + 6 * k, "Today", readout, hh >= 0);
            float H = 46 * k, slot = cw / 24f, bw = Math.Min(6 * k, slot * 0.55f);
            long hmax = Math.Max(1, hours.Max(h => h.Out));
            for (int i = 0; i < 24; i++)
            {
                float cx = P + slot * i + slot / 2;
                Add(new RectangleF(cx - slot / 2, y, slot, H + 6 * k), "h:" + i, null);
                if (hours[i].Out == 0)
                {
                    Dot(cx, y + H - 1.5f * k, 1.5f * k, i > now.Hour ? Palette.Mix(pal.Track, pal.Bg, 0.6f) : pal.Track);
                    continue;
                }
                float bh = Math.Max(bw, (float)(H * hours[i].Out / (double)hmax) * a);
                var c = i == now.Hour ? pal.Accent : (i == hh ? pal.Text : pal.Faint);
                Capsule(new RectangleF(cx - bw / 2, y + H - bh, bw, bh), c);
            }
            y += H + 18 * k;
            foreach (var t in new[] { 0, 6, 12, 18 }) TxtC(t.ToString("00"), fSmall, pal.Tertiary, P + slot * t + slot / 2, y);

            // last 7 days -----------------------------------------------------------
            int dh = HoverIndex("d:");
            long weekOut = days.Sum(d => d.Out), weekReq = days.Sum(d => d.Req);
            readout = dh >= 0
                ? weekStart.AddDays(dh).ToString("ddd d MMM", Inv) + "  ·  " + Fmt.Tokens(days[dh].Out) + " out  ·  " + N(days[dh].Req) + " req"
                : Fmt.Tokens(weekOut) + " out  ·  " + N(weekReq) + " requests";
            y = Section(y, "Last 7 days", readout, dh >= 0);
            float DH = 60 * k, dslot = cw / 7f, dbw = 20 * k;
            long dmax = Math.Max(1, days.Max(d => d.Out));
            for (int i = 0; i < 7; i++)
            {
                float cx = P + dslot * i + dslot / 2;
                Add(new RectangleF(cx - dslot / 2, y, dslot, DH + 24 * k), "d:" + i, null);
                if (days[i].Out == 0) Capsule(new RectangleF(cx - dbw / 2, y + DH - 3 * k, dbw, 3 * k), pal.Track);
                else
                {
                    float bh = Math.Max(dbw * 0.6f, (float)(DH * days[i].Out / (double)dmax) * a);
                    var c = i == 6 ? pal.Accent : (i == dh ? pal.Text : pal.Faint);
                    Capsule(new RectangleF(cx - dbw / 2, y + DH - bh, dbw, bh), c);
                }
                bool isToday = i == 6;
                TxtC(isToday ? "Today" : weekStart.AddDays(i).ToString("ddd", Inv), fSmall,
                    isToday || i == dh ? pal.Text : pal.Tertiary, cx, y + DH + 18 * k);
            }
            y += DH + 18 * k;

            // models -----------------------------------------------------------------
            if (models.Count > 0)
            {
                var list = models.OrderByDescending(m => m.Value.Out).Take(5).ToList();
                long total = Math.Max(1, list.Sum(m => m.Value.Out));
                int mh = HoverIndex("m:");
                readout = mh >= 0 ? N(list[mh].Value.Req) + " requests  ·  " + Fmt.Tokens(list[mh].Value.In + list[mh].Value.CacheW) + " in"
                                  : "7 days";
                y = Section(y, "Models", readout, mh >= 0);

                var famSeen = new Dictionary<string, int>();
                var colors = new List<Color>();
                foreach (var m in list)
                {
                    var fam = Fmt.Family(m.Key);
                    int nth; famSeen.TryGetValue(fam, out nth); famSeen[fam] = nth + 1;
                    colors.Add(Palette.Mix(pal.ForFamily(fam), pal.Bg, 1f - 0.38f * nth));
                }

                // one segmented capsule
                float segGap = 3 * k, avail = cw - segGap * (list.Count - 1);
                var widths = list.Select(m => Math.Max(4 * k, (float)(avail * m.Value.Out / (double)total))).ToArray();
                float excess = widths.Sum() - avail;
                widths[0] -= excess;
                float bx = P, by = y + 2 * k;
                g.SetClip(new RectangleF(P - 1, by - 1, cw * a + 2, 10 * k));
                for (int i = 0; i < list.Count; i++)
                {
                    var c = mh >= 0 && mh != i ? Palette.Mix(colors[i], pal.Bg, 0.3f) : colors[i];
                    Capsule(new RectangleF(bx, by, widths[i], 6 * k), c);
                    bx += widths[i] + segGap;
                }
                g.ResetClip();
                y = by + 6 * k;

                for (int i = 0; i < list.Count; i++)
                {
                    y += 25 * k;
                    bool dim = mh >= 0 && mh != i;
                    var m = list[i];
                    Dot(P + 3.5f * k, y - 4.5f * k, 3.5f * k, colors[i]);
                    Txt(m.Key, fBody, dim ? pal.Tertiary : pal.Text, P + 14 * k, y);
                    var pct = Math.Round(100.0 * m.Value.Out / total);
                    float pw = TxtR((pct < 1 && m.Value.Out > 0 ? "<1" : pct.ToString(Inv)) + "%", fBody, dim ? pal.Tertiary : pal.Text, W - P, y);
                    TxtR(Fmt.Tokens(m.Value.Out), fBody, pal.Tertiary, W - P - pw - 14 * k, y);
                    Add(new RectangleF(P - 4 * k, y - 17 * k, cw + 8 * k, 25 * k), "m:" + i, null);
                }
            }

            // 5h windows timeline ----------------------------------------------------
            var blocks = Blocks(recs);
            var t1 = app.NowUtc.AddHours(4);
            var t0 = t1.AddHours(-24);
            Func<DateTime, float> X = t => P + cw * (float)Math.Max(0, Math.Min(1, (t - t0).TotalHours / 24.0));
            int bh2 = HoverIndex("b:");
            var liveIdx = blocks.FindIndex(b => app.NowUtc < b.Item1.AddHours(5));
            if (bh2 >= 0 && bh2 < blocks.Count)
            {
                var b = blocks[bh2];
                var s = app.ToLocal(b.Item1);
                readout = DayName(s, now) + " " + Fmt.Clock(s) + "–" + Fmt.Clock(s.AddHours(5)) + "  ·  " + N(b.Item2.Req) + " req  ·  " + Fmt.Tokens(b.Item2.Out) + " out";
            }
            else if (liveIdx >= 0)
                readout = "Live until " + Fmt.Clock(app.ToLocal(blocks[liveIdx].Item1.AddHours(5))) + "  ·  " + N(blocks[liveIdx].Item2.Req) + " req";
            else readout = "Idle";
            var liveColor = pal.Usage(ses == null ? 0 : ses.Pct);
            y = Section(y, "5h windows", readout, bh2 >= 0);
            float ty = y + 4 * k, th = 8 * k;
            Capsule(new RectangleF(P, ty, cw, th), pal.Track);
            for (int i = 0; i < blocks.Count; i++)
            {
                var s = blocks[i].Item1;
                var e = s.AddHours(5);
                if (e <= t0) continue;
                float x0 = X(s) + 1.5f * k, x1 = X(e) - 1.5f * k;
                bool live = i == liveIdx;
                var c = live ? liveColor : (i == bh2 ? pal.Text : pal.Faint);
                if (live)
                {
                    float xn = X(app.NowUtc);
                    Capsule(new RectangleF(x0, ty, Math.Max(th, x1 - x0), th), Palette.Mix(liveColor, pal.Bg, 0.35f));
                    Capsule(new RectangleF(x0, ty, Math.Max(th, (xn - x0) * a), th), c);
                }
                else Capsule(new RectangleF(x0, ty, Math.Max(th, (x1 - x0) * a), th), c);
                Add(new RectangleF(x0, ty - 10 * k, Math.Max(th, x1 - x0), th + 20 * k), "b:" + i, null);
            }
            float nowX = X(app.NowUtc);
            using (var pen = new Pen(pal.Text, 1.5f * k)) g.DrawLine(pen, nowX, ty - 4 * k, nowX, ty + th + 4 * k);
            y = ty + th + 20 * k;
            TxtC("Now", fSmall, pal.Text, nowX, y);
            var tickLocal = app.ToLocal(t0);
            var tick = tickLocal.Date.AddHours(Math.Ceiling(tickLocal.Hour / 6.0) * 6);
            for (; tick < app.ToLocal(t1); tick = tick.AddHours(6))
            {
                float tx = P + cw * (float)((tick - tickLocal).TotalHours / 24.0);
                if (Math.Abs(tx - nowX) < 26 * k || tx < P + 8 * k || tx > W - P - 8 * k) continue;
                TxtC(tick.ToString("HH", Inv), fSmall, pal.Tertiary, tx, y);
            }

            // footer -------------------------------------------------------------------
            y += 22 * k;
            using (var pen = new Pen(pal.Hairline, 1)) g.DrawLine(pen, P, y, W - P, y);
            y += 26 * k;
            var rows = J.Get(app.LastGood == null ? null : app.LastGood.Raw, "seven_day_breakdown", "rows") as object[];
            var parts = rows == null ? new List<string>() : rows.Where(r => J.Num(J.Get(r, "percent"), 0) > 0)
                .Select(r => J.Str(J.Get(r, "display_name")) + " " + Math.Round(J.Num(J.Get(r, "percent"), 0)) + "%").ToList();
            if (parts.Count > 0) Txt(string.Join("  ·  ", parts) + " of week", fSmall, pal.Tertiary, P, y);
            bool hovLink = hover == "link";
            float lw = TxtR("Usage settings  ↗", fSmall, hovLink ? pal.Text : pal.Secondary, W - P, y);
            Add(new RectangleF(W - P - lw - 6 * k, y - 16 * k, lw + 12 * k, 24 * k), "link",
                delegate { try { Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true }); } catch { } });

            return y + 20 * k;
        }

        // ccusage-style blocks: start at the floored hour of the first request, last 5h. Oldest first.
        static List<Tuple<DateTime, Agg>> Blocks(Rec[] recs)
        {
            var list = new List<Tuple<DateTime, Agg>>();
            var cutoff = DateTime.UtcNow.AddHours(-30);
            DateTime start = DateTime.MinValue;
            Agg cur = null;
            foreach (var r in recs.Where(r => r.Utc >= cutoff).OrderBy(r => r.Utc))
            {
                if (cur == null || r.Utc >= start.AddHours(5))
                {
                    start = new DateTime(r.Utc.Year, r.Utc.Month, r.Utc.Day, r.Utc.Hour, 0, 0, DateTimeKind.Utc);
                    cur = new Agg();
                    list.Add(Tuple.Create(start, cur));
                }
                cur.Add(r);
            }
            return list;
        }
    }
}
