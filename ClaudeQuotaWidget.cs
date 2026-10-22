// Claude Quota Widget — a tiny taskbar overlay that shows your Claude 5-hour and weekly usage.
// Plain C# 5 / .NET Framework 4.x so it compiles with the csc.exe that ships with Windows (see build.bat).
//
// Data: reads the Claude Code login token from %USERPROFILE%\.claude\.credentials.json
//       (or %CLAUDE_CONFIG_DIR%\.credentials.json) and calls the same usage endpoint Claude Code's /usage uses.
// Never writes to the credentials file.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Claude Quota Widget")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace ClaudeQuota
{
    // ---------------------------------------------------------------- data

    class Limit
    {
        public string Name;
        public double Pct;          // 0..100
        public DateTime? ResetsAt;  // local time
    }

    class Usage
    {
        public Limit Session;
        public Limit Week;
        public List<Limit> Extra = new List<Limit>();
        public DateTime FetchedAt;
    }

    static class Api
    {
        const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

        public static string CredPath()
        {
            string dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (string.IsNullOrEmpty(dir))
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            return Path.Combine(dir, ".credentials.json");
        }

        static string ReadToken(out string error)
        {
            error = null;
            string path = CredPath();
            if (!File.Exists(path))
            {
                error = "Claude Code login not found.\nInstall Claude Code, run `claude` and log in.";
                return null;
            }
            try
            {
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                object o;
                if (root != null && root.TryGetValue("claudeAiOauth", out o))
                {
                    var oa = o as Dictionary<string, object>;
                    object tok;
                    if (oa != null && oa.TryGetValue("accessToken", out tok) && tok is string)
                        return (string)tok;
                }
                error = "No Claude.ai login in Claude Code.\nRun `claude` and /login with your Claude account.";
            }
            catch (Exception ex) { error = "Couldn't read credentials: " + ex.Message; }
            return null;
        }

        static Limit ParseLimit(Dictionary<string, object> root, string key, string name)
        {
            object o;
            if (!root.TryGetValue(key, out o)) return null;
            var d = o as Dictionary<string, object>;
            if (d == null) return null;
            var l = new Limit { Name = name };
            object u;
            if (d.TryGetValue("utilization", out u) && u != null)
                l.Pct = Convert.ToDouble(u, CultureInfo.InvariantCulture);
            object r;
            if (d.TryGetValue("resets_at", out r) && r is string)
            {
                DateTimeOffset dto;
                if (DateTimeOffset.TryParse((string)r, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                    l.ResetsAt = dto.LocalDateTime;
            }
            return l;
        }

        public static Usage Fetch(out string error)
        {
            string token = ReadToken(out error);
            if (token == null) return null;
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(UsageUrl);
                req.Method = "GET";
                req.Timeout = 15000;
                req.ReadWriteTimeout = 15000;
                req.Accept = "application/json";
                req.UserAgent = "ClaudeQuotaWidget/1.0";
                req.Headers["Authorization"] = "Bearer " + token;
                req.Headers["anthropic-beta"] = "oauth-2025-04-20";
                string body;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = sr.ReadToEnd();

                var root = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                var u = new Usage { FetchedAt = DateTime.Now };
                u.Session = ParseLimit(root, "five_hour", "5-hour session");
                u.Week = ParseLimit(root, "seven_day", "Weekly (all models)");
                var opus = ParseLimit(root, "seven_day_opus", "Weekly Opus");
                var sonnet = ParseLimit(root, "seven_day_sonnet", "Weekly Sonnet");
                if (opus != null) u.Extra.Add(opus);
                if (sonnet != null) u.Extra.Add(sonnet);
                if (u.Session == null && u.Week == null) { error = "Unexpected response from usage API."; return null; }
                return u;
            }
            catch (WebException wex)
            {
                var hr = wex.Response as HttpWebResponse;
                if (hr != null)
                {
                    int code = (int)hr.StatusCode;
                    if (code == 401 || code == 403)
                        error = "Login expired.\nOpen a terminal and run `claude` once to refresh it.";
                    else if (code == 429)
                        error = "Rate-limited by the usage API — will retry.";
                    else
                        error = "Usage API error " + code + ".";
                    hr.Close();
                }
                else error = "Offline: " + wex.Message;
            }
            catch (Exception ex) { error = "Error: " + ex.Message; }
            return null;
        }
    }

    // ---------------------------------------------------------------- settings

    class Settings
    {
        public int IntervalMin = 5;
        public int OffsetX = 0;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeQuotaWidget"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                        int n;
                        if (!int.TryParse(v, out n)) continue;
                        if (k == "interval") s.IntervalMin = n;
                        else if (k == "offsetx") s.OffsetX = n;
                    }
            }
            catch { }
            if (s.IntervalMin != 1 && s.IntervalMin != 5 && s.IntervalMin != 10) s.IntervalMin = 5;
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, "interval=" + IntervalMin + "\r\noffsetx=" + OffsetX + "\r\n");
            }
            catch { }
        }
    }

    static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "ClaudeQuotaWidget";

        public static bool Enabled
        {
            get
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return k != null && k.GetValue(Name) != null;
            }
            set
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (value) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(Name, false);
                }
            }
        }
    }

    // ---------------------------------------------------------------- win32

    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int W, H; public SIZE(int w, int h) { W = w; H = h; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string win);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT dst, ref SIZE size, IntPtr hdcSrc, ref POINT src, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr proc);

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;

        public static void MakeDpiAware()
        {
            try { if (SetProcessDpiAwareness(2) == 0) return; } catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static void TrimMemory()
        {
            try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
        }

        public static string ClassOf(IntPtr h)
        {
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

        // Is a full-screen app (game, video, presentation) in front on the taskbar's monitor?
        public static bool FullscreenAppInFront(IntPtr taskbar)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = ClassOf(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            RECT wr;
            if (!GetWindowRect(fg, out wr)) return false;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(MonitorFromWindow(fg, 2), ref mi)) return false;
            if (MonitorFromWindow(fg, 2) != MonitorFromWindow(taskbar, 2)) return false;
            return wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top &&
                   wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
        }
    }

    // ---------------------------------------------------------------- drawing helpers

    static class Draw
    {
        public static readonly Color Orange = Color.FromArgb(217, 119, 87);
        public static readonly Color Amber = Color.FromArgb(242, 179, 61);
        public static readonly Color Red = Color.FromArgb(229, 72, 77);

        public static Color ForPct(double p) { return p >= 90 ? Red : p >= 75 ? Amber : Orange; }

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Bar(Graphics g, RectangleF r, double pct, Color track, Color fill)
        {
            float rad = r.Height / 2f;
            using (var p = Round(r, rad)) using (var b = new SolidBrush(track)) g.FillPath(b, p);
            float w = (float)(r.Width * Math.Max(0, Math.Min(100, pct)) / 100.0);
            if (w <= 0.5f) return;
            w = Math.Max(w, r.Height); // keep the rounded cap visible at small values
            using (var p = Round(new RectangleF(r.X, r.Y, w, r.Height), rad)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        }

        public static string Countdown(DateTime? t)
        {
            if (!t.HasValue) return "—";
            TimeSpan ts = t.Value - DateTime.Now;
            if (ts.TotalSeconds <= 0) return "now";
            if (ts.TotalDays >= 1) return string.Format("{0}d {1}h {2:00}m", (int)ts.TotalDays, ts.Hours, ts.Minutes);
            if (ts.TotalHours >= 1) return string.Format("{0}h {1:00}m {2:00}s", (int)ts.TotalHours, ts.Minutes, ts.Seconds);
            return string.Format("{0}m {1:00}s", ts.Minutes, ts.Seconds);
        }

        public static string Clock(DateTime? t)
        {
            if (!t.HasValue) return "";
            DateTime v = t.Value;
            if (v.Date == DateTime.Today) return "today " + v.ToString("HH:mm");
            if (v.Date == DateTime.Today.AddDays(1)) return "tomorrow " + v.ToString("HH:mm");
            return v.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture);
        }
    }

    // ---------------------------------------------------------------- hover card

    class Card : Form
    {
        public Usage Usage;
        public string Error;
        public int IntervalMin;
        float s = 1f;

        public Card()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(32, 32, 32);
            DoubleBuffered = true;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        public void Place(Rectangle anchor, float scale)
        {
            s = scale;
            int w = (int)(300 * s);
            int h;
            using (var bmp = new Bitmap(1, 1)) using (var g = Graphics.FromImage(bmp)) h = (int)Math.Ceiling(LayoutCard(g, false, w));
            int x = anchor.Left;
            int y = anchor.Top - h - (int)(10 * s);
            Rectangle wa = Screen.FromRectangle(anchor).Bounds;
            if (x + w > wa.Right - 8) x = wa.Right - 8 - w;
            if (y < wa.Top) y = anchor.Bottom + (int)(10 * s);
            Bounds = new Rectangle(x, y, w, h);
            Region = new Region(Draw.Round(new RectangleF(0, 0, w, h), 8 * s));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var pen = new Pen(Color.FromArgb(60, 60, 60)))
            using (var p = Draw.Round(new RectangleF(0.5f, 0.5f, Width - 1, Height - 1), 8 * s))
                g.DrawPath(pen, p);
            LayoutCard(g, true, Width);
        }

        // Lays out (and optionally draws) the card; returns total height.
        float LayoutCard(Graphics g, bool draw, int width)
        {
            float pad = 14 * s, y = pad, inner = width - pad * 2;
            using (var fTitle = new Font("Segoe UI Semibold", 13 * s, GraphicsUnit.Pixel))
            using (var fBig = new Font("Segoe UI Semibold", 12.5f * s, GraphicsUnit.Pixel))
            using (var fBody = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel))
            using (var fSmall = new Font("Segoe UI", 11 * s, GraphicsUnit.Pixel))
            using (var white = new SolidBrush(Color.FromArgb(240, 240, 240)))
            using (var gray = new SolidBrush(Color.FromArgb(160, 160, 160)))
            using (var amber = new SolidBrush(Draw.Amber))
            {
                var right = new StringFormat { Alignment = StringAlignment.Far };
                if (draw) g.DrawString("Claude usage", fTitle, white, pad, y);
                y += 22 * s;

                if (Usage != null)
                {
                    foreach (var l in new[] { Usage.Session, Usage.Week })
                    {
                        if (l == null) continue;
                        y += 6 * s;
                        if (draw)
                        {
                            g.DrawString(l.Name, fBody, white, pad, y);
                            using (var b = new SolidBrush(Draw.ForPct(l.Pct)))
                                g.DrawString(Math.Round(l.Pct) + "%", fBig, b, new RectangleF(pad, y - 1 * s, inner, 20 * s), right);
                        }
                        y += 20 * s;
                        if (draw) Draw.Bar(g, new RectangleF(pad, y, inner, 6 * s), l.Pct, Color.FromArgb(70, 70, 70), Draw.ForPct(l.Pct));
                        y += 11 * s;
                        if (draw)
                        {
                            string line = l.ResetsAt.HasValue
                                ? "Resets in " + Draw.Countdown(l.ResetsAt) + "   ·   " + Draw.Clock(l.ResetsAt)
                                : "Not started — no cooldown";
                            g.DrawString(line, fSmall, gray, pad, y);
                        }
                        y += 18 * s;
                    }
                    foreach (var l in Usage.Extra)
                    {
                        y += 4 * s;
                        if (draw)
                        {
                            g.DrawString(l.Name, fSmall, gray, pad, y);
                            string r = Math.Round(l.Pct) + "%" + (l.ResetsAt.HasValue ? "  ·  " + Draw.Clock(l.ResetsAt) : "");
                            g.DrawString(r, fSmall, gray, new RectangleF(pad, y, inner, 18 * s), right);
                        }
                        y += 16 * s;
                    }
                }

                if (!string.IsNullOrEmpty(Error))
                {
                    y += 8 * s;
                    SizeF sz = g.MeasureString(Error, fSmall, (int)inner);
                    if (draw) g.DrawString(Error, fSmall, amber, new RectangleF(pad, y, inner, sz.Height + 2));
                    y += sz.Height;
                }

                y += 10 * s;
                if (draw)
                {
                    string foot = (Usage != null ? "Updated " + Usage.FetchedAt.ToString("HH:mm:ss") + "  ·  " : "") +
                                  "every " + IntervalMin + " min  ·  right-click for options";
                    using (var pen = new Pen(Color.FromArgb(55, 55, 55))) g.DrawLine(pen, pad, y - 5 * s, width - pad, y - 5 * s);
                    g.DrawString(foot, fSmall, gray, pad, y);
                }
                y += 16 * s + pad;
            }
            return y;
        }
    }

    // ---------------------------------------------------------------- taskbar widget

    class Widget : Form
    {
        Settings cfg = Settings.Load();
        Usage usage;
        string error;
        bool busy, hover, dragging, hiddenForFullscreen;
        int dragStartX, dragStartOffset;
        DateTime lastAutoResetFetch = DateTime.MinValue;
        float s = 1f;
        Rectangle bounds;
        IntPtr taskbar;

        readonly System.Windows.Forms.Timer pollTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer guardTimer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer cardTimer = new System.Windows.Forms.Timer();
        readonly Card card = new Card();
        ContextMenuStrip menu;

        public Widget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Claude Quota";

            BuildMenu();

            pollTimer.Interval = cfg.IntervalMin * 60000;
            pollTimer.Tick += delegate { Refresh_(); };
            guardTimer.Interval = 1000;
            guardTimer.Tick += delegate { Guard(); };
            cardTimer.Interval = 1000;
            cardTimer.Tick += delegate { if (card.Visible) card.Invalidate(); };

            SystemEvents.DisplaySettingsChanged += delegate { Reposition(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Reposition();
            pollTimer.Start();
            guardTimer.Start();
            Refresh_();
        }

        // ---------- data

        void Refresh_()
        {
            if (busy) return;
            busy = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                Usage u = Api.Fetch(out err);
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        busy = false;
                        if (u != null) usage = u;
                        error = err;
                        Render();
                        if (card.Visible) { SyncCard(); card.Invalidate(); }
                        Native.TrimMemory();
                    });
                }
                catch { busy = false; }
            });
        }

        void SetInterval(int min)
        {
            cfg.IntervalMin = min;
            cfg.Save();
            pollTimer.Stop();
            pollTimer.Interval = min * 60000;
            pollTimer.Start();
            BuildMenu();
        }

        // ---------- position / z-order

        void Reposition()
        {
            taskbar = Native.FindWindow("Shell_TrayWnd", null);
            Native.RECT tb;
            if (taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out tb))
            {
                Rectangle sb = Screen.PrimaryScreen.Bounds;
                tb = new Native.RECT { Left = sb.Left, Right = sb.Right, Top = sb.Bottom - 48, Bottom = sb.Bottom };
            }
            int tbH = tb.Bottom - tb.Top;
            bool horizontal = (tb.Right - tb.Left) > tbH;
            if (!horizontal) tbH = 48;
            s = Math.Max(1f, tbH / 48f);
            int w = (int)(178 * s);
            int x = tb.Left + (int)(10 * s) + cfg.OffsetX;
            x = Math.Max(tb.Left, Math.Min(x, tb.Right - w));
            bounds = new Rectangle(x, tb.Top, w, tbH);
            Render();
        }

        void Guard()
        {
            if (taskbar == IntPtr.Zero || !Native.IsWindowVisible(taskbar)) Reposition();

            bool fs = taskbar != IntPtr.Zero && Native.FullscreenAppInFront(taskbar);
            if (fs != hiddenForFullscreen)
            {
                hiddenForFullscreen = fs;
                if (fs) { Hide(); card.Hide(); } else { Show(); Render(); }
            }
            if (!fs)
            {
                // The taskbar raises itself when clicked; push ourselves back on top. Cheap no-op if already there.
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }

            // When a window resets, fetch fresh numbers once instead of showing a stale bar.
            if (usage != null && !busy && (DateTime.Now - lastAutoResetFetch).TotalSeconds > 90)
            {
                foreach (var l in new[] { usage.Session, usage.Week })
                    if (l != null && l.ResetsAt.HasValue && l.ResetsAt.Value < DateTime.Now && usage.FetchedAt < l.ResetsAt.Value)
                    {
                        lastAutoResetFetch = DateTime.Now;
                        Refresh_();
                        break;
                    }
            }
        }

        // ---------- rendering (per-pixel alpha so it blends with the taskbar)

        void Render()
        {
            if (!IsHandleCreated || bounds.Width <= 0) return;
            int w = bounds.Width, h = bounds.Height;
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.Clear(Color.Transparent);

                    // Near-invisible plate so the whole area receives the mouse; brighter on hover like native taskbar buttons.
                    var plate = new RectangleF(1, 4 * s, w - 2, h - 8 * s);
                    using (var p = Draw.Round(plate, 5 * s))
                    using (var b = new SolidBrush(hover ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(2, 255, 255, 255)))
                        g.FillPath(b, p);

                    using (var fLabel = new Font("Segoe UI", 11 * s, GraphicsUnit.Pixel))
                    using (var fPct = new Font("Segoe UI Semibold", 11.5f * s, GraphicsUnit.Pixel))
                    using (var white = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                    using (var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
                    {
                        var right = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
                        var left = new StringFormat { LineAlignment = StringAlignment.Center };

                        if (usage == null)
                        {
                            var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                            g.DrawString(error == null ? "Claude · loading…" : "Claude · hover for info", fLabel, dim, new RectangleF(0, 0, w, h), center);
                        }
                        else
                        {
                            float rowH = 15 * s, gap = 4 * s;
                            float top = (h - (rowH * 2 + gap)) / 2f;
                            DrawRow(g, "5h", usage.Session, top, rowH, w, fLabel, fPct, white, dim, left, right);
                            DrawRow(g, "7d", usage.Week, top + rowH + gap, rowH, w, fLabel, fPct, white, dim, left, right);
                        }

                        if (error != null && usage != null)
                            using (var b = new SolidBrush(Draw.Amber))
                                g.FillEllipse(b, w - 6 * s, 7 * s, 4 * s, 4 * s); // small "stale" dot
                    }
                }
                Push(bmp);
            }
        }

        void DrawRow(Graphics g, string label, Limit l, float y, float rowH, int w,
                     Font fLabel, Font fPct, Brush white, Brush dim, StringFormat left, StringFormat right)
        {
            float x0 = 8 * s, barX = 28 * s, pctW = 40 * s;
            float barW = w - barX - pctW - 6 * s;
            g.DrawString(label, fLabel, dim, new RectangleF(x0, y, 24 * s, rowH), left);
            double pct = l != null ? l.Pct : 0;
            float bh = 5 * s;
            Draw.Bar(g, new RectangleF(barX, y + (rowH - bh) / 2f, barW, bh), pct,
                     Color.FromArgb(55, 255, 255, 255), Draw.ForPct(pct));
            g.DrawString(l != null ? Math.Round(pct) + "%" : "–", fPct, white,
                         new RectangleF(barX + barW, y, pctW, rowH), right);
        }

        void Push(Bitmap bmp)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBmp = bmp.GetHbitmap(Color.FromArgb(0));
                old = Native.SelectObject(mem, hBmp);
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(bounds.Left, bounds.Top);
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                Native.ReleaseDC(IntPtr.Zero, screen);
                if (hBmp != IntPtr.Zero) { Native.SelectObject(mem, old); Native.DeleteObject(hBmp); }
                Native.DeleteDC(mem);
            }
        }

        // ---------- mouse

        void SyncCard()
        {
            card.Usage = usage;
            card.Error = error;
            card.IntervalMin = cfg.IntervalMin;
            card.Place(bounds, s);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            Render();
            SyncCard();
            card.Show();
            cardTimer.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hover = false;
            Render();
            card.Hide();
            cardTimer.Stop();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragStartX = Cursor.Position.X;
                dragStartOffset = cfg.OffsetX;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            int dx = Cursor.Position.X - dragStartX;
            if (Math.Abs(dx) < 3) return;
            cfg.OffsetX = dragStartOffset + dx;
            card.Hide();
            Reposition();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                if (cfg.OffsetX != dragStartOffset) cfg.Save();
                else Refresh_(); // plain click = refresh now
            }
            else if (e.Button == MouseButtons.Right)
            {
                card.Hide();
                Native.SetForegroundWindow(Handle);
                menu.Show(Cursor.Position);
            }
        }

        // ---------- menu

        void BuildMenu()
        {
            if (menu != null) menu.Dispose();
            menu = new ContextMenuStrip();
            menu.Items.Add("Refresh now", null, delegate { Refresh_(); });

            var every = new ToolStripMenuItem("Update every");
            foreach (int m in new[] { 1, 5, 10 })
            {
                int mm = m;
                var it = new ToolStripMenuItem(m + (m == 1 ? " minute" : " minutes"), null, delegate { SetInterval(mm); });
                it.Checked = cfg.IntervalMin == m;
                every.DropDownItems.Add(it);
            }
            menu.Items.Add(every);

            var start = new ToolStripMenuItem("Start with Windows");
            try { start.Checked = Startup.Enabled; } catch { }
            start.Click += delegate
            {
                try { Startup.Enabled = !Startup.Enabled; start.Checked = Startup.Enabled; } catch { }
            };
            menu.Items.Add(start);

            menu.Items.Add("Reset position", null, delegate { cfg.OffsetX = 0; cfg.Save(); Reposition(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Application.Exit(); });
        }
    }

    // ---------------------------------------------------------------- entry

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "ClaudeQuotaWidget_SingleInstance", out created))
            {
                if (!created) return;
                Native.MakeDpiAware();
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Widget());
            }
        }
    }
}
