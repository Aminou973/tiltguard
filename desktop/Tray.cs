using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TiltGuard
{
    /// <summary>Everything the user sees: the taskbar strip next to Mawaqit, the flyout, the menu and the toasts.</summary>
    internal sealed class TrayApp : ApplicationContext
    {
        public Config Cfg;
        public Dictionary<string, object> Status;       // last /api/guard/status
        public Dictionary<string, object> Service;      // ProgramData\TiltGuard\state.json (written by the service)
        public double LastOk;
        public string LastError = "";
        readonly NotifyIcon icon;
        readonly ContextMenuStrip menu;
        StripForm strip;
        FlyoutForm flyout;
        readonly System.Windows.Forms.Timer uiTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        readonly HashSet<string> notified = new HashSet<string>();
        readonly MessageWindow messages;
        GuardClient client;

        public TrayApp()
        {
            Cfg = Config.Load();
            menu = BuildMenu();
            icon = new NotifyIcon { Icon = AppIcon(), Text = "Tilt Guard", Visible = true, ContextMenuStrip = menu };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };
            messages = new MessageWindow(this);
            strip = new StripForm(this);
            strip.Show();
            strip.Embed();
            if (!Cfg.Ok) Setup();
            uiTimer.Tick += (s, e) => { strip?.Invalidate(); flyout?.RefreshView(); };
            uiTimer.Start();
            new Thread(PollLoop) { IsBackground = true, Name = "poll" }.Start();
        }

        public static Icon AppIcon()
        {
            try { using (var s = typeof(TrayApp).Assembly.GetManifestResourceStream("TiltGuard.tiltguard.ico")) return new Icon(s); }
            catch { return SystemIcons.Shield; }
        }

        // ------------------------------------------------------------------ data
        void PollLoop()
        {
            while (true)
            {
                try
                {
                    if (!Cfg.Ok) Cfg = Config.Load();
                    if (Cfg.Ok)
                    {
                        if (client == null) client = new GuardClient(Cfg);
                        var st = client.Status();
                        Status = st;
                        LastOk = Util.Now;
                        LastError = "";
                        strip?.BeginInvoke(new Action(CheckNotifications));
                    }
                }
                catch (Exception e) { LastError = e.Message; client?.Dispose(); client = null; }
                try { Service = File.Exists(Paths.ServiceState) ? Json.Obj(File.ReadAllText(Paths.ServiceState)) : null; } catch { }
                Thread.Sleep(3000);
            }
        }

        public List<Dictionary<string, object>> Accounts()
        {
            var acc = Json.Dict(Status, "accounts");
            return acc == null ? new List<Dictionary<string, object>>() : acc.Values.Select(Json.Dict).Where(d => d != null).ToList();
        }

        public Dictionary<string, object> Desktop => Json.Dict(Status, "desktop");
        public bool Online => Util.Now - LastOk < 20;

        public static string Short(Dictionary<string, object> a)
        {
            var firm = (Json.Str(a, "firm") ?? "").ToLowerInvariant();
            var map = new Dictionary<string, string> { { "tradeify", "TFY" }, { "myfundedfutures", "MFF" }, { "apex", "APX" }, { "topstep", "TST" },
                                                       { "lucid", "LUC" }, { "fundednext", "FNX" }, { "earn2trade", "E2T" }, { "take profit trader", "TPT" } };
            var label = Json.Str(a, "label") ?? "";
            var abbr = map.TryGetValue(firm, out var s) ? s
                : new string((label.Length > 0 ? label : Json.Str(a, "name") ?? "?").Where(char.IsLetterOrDigit).Take(3).ToArray()).ToUpperInvariant();
            var q = label.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                         .FirstOrDefault(w => !firm.Contains(w.ToLowerInvariant()) && !w.Equals(abbr, StringComparison.OrdinalIgnoreCase));
            if (q == null) return abbr;
            return abbr + " " + (q.Length > 6 ? q.Substring(0, 5) : q);
        }

        /// <summary>Accounts open right now (platform connected or a position on); falls back to the ones traded today.</summary>
        public List<Dictionary<string, object>> ActiveAccounts()
        {
            var all = Accounts();
            var open = all.Where(a => Json.Bool(a, "online") || (Json.Num(Json.Dict(a, "stats"), "pos_abs") ?? 0) > 0).ToList();
            if (open.Count > 0) return open;
            return all.Where(a => (Json.Num(Json.Dict(a, "stats"), "trades") ?? 0) > 0 || Json.Dict(a, "lock") != null).ToList();
        }

        void CheckNotifications()
        {
            var day = Json.Str(Status, "day") ?? "";
            foreach (var a in Accounts())
            {
                var s = Json.Dict(a, "stats"); var r = Json.Dict(a, "rules");
                var lim = Json.Num(r, "max_daily_loss"); var pl = Json.Num(s, "day_pl") ?? 0;
                var nm = Short(a);
                if (lim.HasValue && lim > 0)
                {
                    var used = -pl / lim.Value;
                    if (used >= 0.8) Notify($"{day}-{nm}-80", $"{nm}: 80% of the daily loss used", $"Day P&L {Util.Money(pl)} — {Util.Money(lim.Value + pl, false)} left before the lockout.");
                    else if (used >= 0.5) Notify($"{day}-{nm}-50", $"{nm}: half of the daily loss used", $"Day P&L {Util.Money(pl)}. Slow down.");
                }
                var cd = Json.Dict(a, "cooldown");
                if (cd != null) Notify("cd-" + Json.Str(cd, "id"), $"{nm}: cooldown", Json.Str(cd, "reason") ?? "");
            }
            var d = Desktop;
            if (d != null && Json.Bool(d, "locked"))
                Notify("lock-" + Json.Num(d, "until"), "Locked out", $"{Json.Str(d, "reason")}\nUntil {Util.Local(Json.Num(d, "until") ?? 0):ddd HH:mm}. Write today's review in the journal.");
            var nx = Json.Dict(d, "next_scheduled");
            if (nx != null && (Json.Num(nx, "at") ?? 0) - Util.Now < 600)
                Notify("soon-" + Json.Num(nx, "at"), "Lockout in 10 minutes", Json.Str(nx, "reason") ?? "");
        }

        void Notify(string key, string title, string text)
        {
            if (!notified.Add(key)) return;
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = string.IsNullOrEmpty(text) ? " " : text;
            icon.ShowBalloonTip(8000);
        }

        // ------------------------------------------------------------------ actions
        ContextMenuStrip BuildMenu()
        {
            var m = new ContextMenuStrip();
            var done = new ToolStripMenuItem("Done for today — lock me out");
            done.DropDownItems.Add("Until tomorrow morning", null, (s, e) => LockOut(null));
            done.DropDownItems.Add("For 1 hour", null, (s, e) => LockOut(60));
            done.DropDownItems.Add("For 2 hours", null, (s, e) => LockOut(120));
            done.DropDownItems.Add("For 4 hours", null, (s, e) => LockOut(240));
            m.Items.Add(done);
            // lock ONE account (the others keep trading, the PC stays usable) - rebuilt each time the menu opens
            var one = new ToolStripMenuItem("Lock one account");
            one.DropDownItems.Add("(loading…)").Enabled = false;
            m.Items.Add(one);
            m.Opening += (s, e) => FillLockOne(one);
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Open the guard", null, (s, e) => Open("/"));
            m.Items.Add("Open the journal", null, (s, e) => OpenJournal(""));
            m.Items.Add(new ToolStripSeparator());
            var pos = new ToolStripMenuItem("Strip position");
            pos.DropDownItems.Add("Move left", null, (s, e) => Nudge(-12));
            pos.DropDownItems.Add("Move right", null, (s, e) => Nudge(12));
            pos.DropDownItems.Add("Next to Mawaqit (auto)", null, (s, e) => Nudge(0, true));
            m.Items.Add(pos);
            m.Items.Add("Server and key…", null, (s, e) => Setup());
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Hide the strip (the lockout keeps running)", null, (s, e) => ExitThread());
            return m;
        }

        public void ShowMenu(Point screen) => menu.Show(screen);

        public void Open(string path)
        {
            if (!Cfg.Ok) return;
            try { Process.Start(Cfg.Server + path); } catch { }
        }

        public void OpenJournal(string path)
        {
            if (!Cfg.Ok) return;
            var root = Cfg.Server.EndsWith("/guard", StringComparison.OrdinalIgnoreCase) ? Cfg.Server.Substring(0, Cfg.Server.Length - 6) : Cfg.Server;
            try { Process.Start(root + "/" + path.TrimStart('/')); } catch { }
        }

        public void LockOut(double? minutes)
        {
            if (!Cfg.Ok) { Setup(); return; }
            var until = minutes.HasValue ? $"for {minutes / 60:0.#} h" : "until tomorrow morning";
            var reason = Microsoft.VisualBasic.Interaction.InputBox(
                $"Lock every account and this PC {until}?\n\nOpen positions are closed, orders are blocked, trading sites and apps are blocked.\nThere is no early unlock.\n\nReason (optional):",
                "Tilt Guard — lock me out", "Done for today");
            if (string.IsNullOrEmpty(reason)) return;           // Cancel
            try
            {
                using (var c = new GuardClient(Cfg)) Status = c.ManualLock(reason, minutes);
                LastOk = Util.Now;
                strip?.Invalidate();
                icon.BalloonTipTitle = "Locked out";
                icon.BalloonTipText = "See you tomorrow. The journal is open for today's review.";
                icon.ShowBalloonTip(6000);
                OpenJournal("journal/" + (Json.Str(Status, "day") ?? ""));
            }
            catch (Exception e) { MessageBox.Show("The lockout failed: " + e.Message, "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void FillLockOne(ToolStripMenuItem one)
        {
            one.DropDownItems.Clear();
            var list = ActiveAccounts();
            if (list.Count == 0) list = Accounts();
            foreach (var a in list)
            {
                var name = Json.Str(a, "name");
                if (string.IsNullOrEmpty(name)) continue;
                var label = Json.Str(a, "label");
                var locked = Json.Bool(a, "locked");
                var item = new ToolStripMenuItem((string.IsNullOrEmpty(label) ? name : label + "  (" + name + ")") + (locked ? "  — locked" : "")) { Enabled = !locked };
                item.DropDownItems.Add("Until the next session", null, (s, e) => LockOne(name, null));
                item.DropDownItems.Add("For 30 minutes", null, (s, e) => LockOne(name, 30));
                item.DropDownItems.Add("For 1 hour", null, (s, e) => LockOne(name, 60));
                item.DropDownItems.Add("For 2 hours", null, (s, e) => LockOne(name, 120));
                item.DropDownItems.Add("For 4 hours", null, (s, e) => LockOne(name, 240));
                one.DropDownItems.Add(item);
            }
            if (one.DropDownItems.Count == 0) one.DropDownItems.Add("(no account reporting)").Enabled = false;
        }

        public void LockOne(string account, double? minutes)
        {
            if (!Cfg.Ok) { Setup(); return; }
            var until = minutes.HasValue ? (minutes >= 60 ? $"for {minutes / 60:0.#} h" : $"for {minutes:0} min") : "until the next session";
            var reason = Microsoft.VisualBasic.Interaction.InputBox(
                $"Lock {account} {until}?\n\nIts positions are closed and new orders blocked. Your other accounts keep trading and the PC stays usable.\nThere is no early unlock.\n\nReason (optional):",
                "Tilt Guard — lock one account", "Done on this account");
            if (string.IsNullOrEmpty(reason)) return;           // Cancel
            try
            {
                using (var c = new GuardClient(Cfg)) Status = c.ManualLock(reason, minutes, account);
                LastOk = Util.Now;
                strip?.Invalidate();
                icon.BalloonTipTitle = account + " locked";
                icon.BalloonTipText = "Positions closed, new orders blocked " + until + ". Your other accounts keep trading.";
                icon.ShowBalloonTip(6000);
            }
            catch (Exception e) { MessageBox.Show("The lock failed: " + e.Message, "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void Nudge(int px, bool reset = false)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\TiltGuard"))
            {
                var cur = reset ? 0 : (int)(k.GetValue("StripOffset") ?? 0) + px;
                k.SetValue("StripOffset", cur, RegistryValueKind.DWord);
            }
            strip?.Reposition();
        }

        public void Setup()
        {
            var server = Microsoft.VisualBasic.Interaction.InputBox("Guard server URL", "Tilt Guard setup", string.IsNullOrEmpty(Cfg.Server) ? "http://127.0.0.1:8940" : Cfg.Server);
            if (string.IsNullOrEmpty(server)) return;
            var key = Microsoft.VisualBasic.Interaction.InputBox("Device key (Guard → Setup page)", "Tilt Guard setup", Cfg.Key);
            if (string.IsNullOrEmpty(key)) return;
            Cfg = new Config { Server = server.Trim().TrimEnd('/'), Key = key.Trim() };
            try { Cfg.Save(Paths.UserConfig); } catch (Exception e) { MessageBox.Show(e.Message); }
            client?.Dispose(); client = null;
        }

        DateTime flyoutClosed = DateTime.MinValue;
        /// <summary>Test aid: render the flyout with live data to a PNG (TiltGuard.exe --snapshot file.png).</summary>
        public static void Snapshot(string file)
        {
            var app = (TrayApp)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TrayApp));
            app.Cfg = Config.Load();
            app.LastError = "";
            using (var c = new GuardClient(app.Cfg)) { app.Status = c.Status(); app.LastOk = Util.Now; }
            try { app.Service = File.Exists(Paths.ServiceState) ? Json.Obj(File.ReadAllText(Paths.ServiceState)) : null; } catch { }
            using (var f = new FlyoutForm(app))
            {
                f.Height = f.LayoutCards();
                f.Left = -3000; f.Top = 0;            // off-screen, but really shown so the buttons render
                f.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        /// <summary>Test aid: render the strip for each open account to PNGs (TiltGuard.exe --snapshot-strip prefix).</summary>
        public static void SnapshotStrip(string prefix)
        {
            var app = (TrayApp)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(TrayApp));
            app.Cfg = Config.Load(); app.LastError = "";
            using (var c = new GuardClient(app.Cfg)) { app.Status = c.Status(); app.LastOk = Util.Now; }
            int n = Math.Max(1, app.ActiveAccounts().Count);
            for (int i = 0; i < n; i++)
            {
                StripForm.ForceIndex = i;
                using (var f = new StripForm(app) { Width = 200, Height = 44 })
                using (var bmp = new Bitmap(f.Width, f.Height))
                {
                    f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                    bmp.Save($"{prefix}{i + 1}.png", System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        public void ToggleFlyout()
        {
            if (flyout != null && !flyout.IsDisposed && flyout.Visible) { flyout.Close(); return; }
            if ((DateTime.UtcNow - flyoutClosed).TotalMilliseconds < 400) return;   // the click that closed it
            flyout = new FlyoutForm(this);
            flyout.FormClosed += (s, e) => flyoutClosed = DateTime.UtcNow;
            flyout.ShowNear(strip);
        }

        public void RecreateStrip()
        {
            // Explorer restarted: our child window died with the old taskbar
            try { strip?.Dispose(); } catch { }
            strip = new StripForm(this);
            strip.Show();
            strip.Embed();
        }

        protected override void ExitThreadCore()
        {
            icon.Visible = false;
            icon.Dispose();
            strip?.Dispose();
            messages.DestroyHandle();
            base.ExitThreadCore();
        }

        /// <summary>Hidden window that hears "TaskbarCreated" (Explorer restart) to re-embed the strip.</summary>
        sealed class MessageWindow : NativeWindow
        {
            readonly TrayApp app;
            readonly uint taskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            public MessageWindow(TrayApp a) { app = a; CreateHandle(new CreateParams()); }
            protected override void WndProc(ref Message m)
            {
                if ((uint)m.Msg == taskbarCreated) app.RecreateStrip();
                base.WndProc(ref m);
            }
        }
    }

    /// <summary>Two-line strip embedded in the taskbar (child of Shell_TrayWnd), placed right after the Mawaqit strip.</summary>
    internal sealed class StripForm : Form
    {
        public static int? ForceIndex;   // test aid (--snapshot): render a given account
        readonly TrayApp app;
        IntPtr taskbar;
        bool embedded;
        Color key;

        public StripForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            key = Light() ? Color.FromArgb(238, 238, 238) : Color.FromArgb(30, 30, 30);
            BackColor = key;
            Size = new Size(200, 40);
            MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) app.ToggleFlyout(); else app.ShowMenu(Cursor.Position); };
            Cursor = Cursors.Hand;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        static bool Light()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")) return (int)(k?.GetValue("SystemUsesLightTheme") ?? 0) == 1; }
            catch { return false; }
        }

        float Dpi => Math.Max(1f, (embedded ? Native.GetDpiForWindow(Handle) : (uint)DeviceDpi) / 96f);
        int S(int px) => (int)Math.Round(px * Dpi);

        public void Embed()
        {
            taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero)
            {
                var style = Native.GetWindowLong(Handle, Native.GWL_STYLE);
                Native.SetWindowLong(Handle, Native.GWL_STYLE, (style & ~Native.WS_POPUP) | Native.WS_CHILD);
                embedded = Native.SetParent(Handle, taskbar) != IntPtr.Zero;
            }
            Native.SetLayeredWindowAttributes(Handle, (uint)ColorTranslator.ToWin32(key), 255, Native.LWA_COLORKEY);
            if (!embedded)
            {
                // fallback: a top-most tool window over the taskbar
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            Reposition();
            var t = new System.Windows.Forms.Timer { Interval = 5000 };
            t.Tick += (s, e) => { if (!IsDisposed) Reposition(); };
            t.Start();
        }

        public void Reposition()
        {
            if (taskbar == IntPtr.Zero || !Native.GetWindowRect(taskbar, out var tb)) return;
            int offset = 0;
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\TiltGuard")) offset = (int)(k?.GetValue("StripOffset") ?? 0); } catch { }
            int anchor = AnchorRight(tb);
            int h = Math.Min(tb.Height, S(44));
            int x = (anchor > 0 ? anchor + S(8) : tb.Left + S(270)) + offset;
            // fit between the prayer strip and the (centered) Start button
            int start = StartButtonLeft();
            int w = start > x ? Math.Max(S(120), Math.Min(S(200), start - x - S(6))) : S(176);
            int y = tb.Top + (tb.Height - h) / 2;
            if (embedded) Native.MoveWindow(Handle, x - tb.Left, y - tb.Top, w, h, true);
            else Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        int startLeft, startChecked;
        /// <summary>Left edge (screen px) of the Windows 11 Start button (UI Automation), cached 30 s.</summary>
        int StartButtonLeft()
        {
            if (Environment.TickCount - startChecked < 30000 && startLeft > 0) return startLeft;
            startChecked = Environment.TickCount;
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(taskbar);
                var btn = root.FindFirst(System.Windows.Automation.TreeScope.Descendants,
                    new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.AutomationIdProperty, "StartButton"));
                if (btn != null) startLeft = (int)btn.Current.BoundingRectangle.Left;
            }
            catch { }
            return startLeft;
        }

        /// <summary>Right edge (screen px) of the Mawaqit strip, or of other third-party widgets in the left part of the taskbar.</summary>
        int AnchorRight(Native.RECT tb)
        {
            int mawaqit = 0, other = 0;
            uint mine = (uint)Process.GetCurrentProcess().Id;
            Native.GetWindowThreadProcessId(taskbar, out var explorer);
            Native.EnumProc visit = (h, l) =>
            {
                if (h == Handle || !Native.IsWindowVisible(h)) return true;
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pid == mine || pid == explorer) return true;
                if (!Native.GetWindowRect(h, out var r) || r.Width <= 0 || r.Width > tb.Width / 2) return true;
                bool onTaskbar = r.Top >= tb.Top - 2 && r.Bottom <= tb.Bottom + 2 && r.Left >= tb.Left && r.Right <= tb.Left + tb.Width / 2;
                if (!onTaskbar) return true;
                string name = "";
                try { using (var p = Process.GetProcessById((int)pid)) name = p.ProcessName; } catch { }
                if (System.Text.RegularExpressions.Regex.IsMatch(name, "mawaqit|awqat|salaat|salat|prayer", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    mawaqit = Math.Max(mawaqit, r.Right);
                else other = Math.Max(other, r.Right);
                return true;
            };
            Native.EnumChildWindows(taskbar, visit, IntPtr.Zero);
            Native.EnumWindows(visit, IntPtr.Zero);
            return mawaqit > 0 ? mawaqit : other;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(key);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            bool light = key.R > 128;
            Color fg = light ? Color.FromArgb(20, 20, 20) : Color.FromArgb(240, 240, 240);
            Color muted = light ? Color.FromArgb(90, 90, 90) : Color.FromArgb(165, 165, 165);
            Color green = light ? Color.FromArgb(16, 140, 70) : Color.FromArgb(70, 215, 130);
            Color red = light ? Color.FromArgb(200, 40, 60) : Color.FromArgb(255, 105, 120);
            Color amber = light ? Color.FromArgb(170, 110, 0) : Color.FromArgb(245, 175, 50);
            var brushes = new Dictionary<Color, SolidBrush>();
            Brush B(Color c) { if (!brushes.TryGetValue(c, out var b)) brushes[c] = b = new SolidBrush(c); return b; }
            using (var f1 = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
            using (var f2 = new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Point))
            try
            {
                float lineH = f1.GetHeight(g);
                float y1 = Math.Max(0, (Height - 2 * lineH) / 2), y2 = y1 + lineH;
                float x = S(2);
                var active = app.ActiveAccounts();
                string text; Color col;
                if (active.Count == 0)
                {
                    g.DrawString(app.Cfg.Ok ? (app.Online ? "Tilt Guard" : "Tilt Guard · offline") : "Tilt Guard · set up", f2, B(fg), x, y1);
                    (text, col) = StatusLine(app.Accounts(), fg, muted, red, amber);
                    if (text.StartsWith("OK")) (text, col) = (app.Online ? "no account open" : text, muted);
                }
                else
                {
                    // one open account at a time, switching every 5 s
                    int i = ForceIndex.HasValue ? ForceIndex.Value % active.Count : (int)((uint)Environment.TickCount / 5000 % (uint)active.Count);
                    var a = active[i];
                    var pl = Json.Num(Json.Dict(a, "stats"), "day_pl") ?? 0;
                    var label = TrayApp.Short(a) + "  ";
                    g.DrawString(label, f1, B(muted), x, y1);
                    x += g.MeasureString(label, f1).Width;
                    g.DrawString(Util.Money(pl), f2, B(pl > 0 ? green : pl < 0 ? red : fg), x, y1);
                    if (active.Count > 1)
                    {
                        var dots = $"{i + 1}/{active.Count}";
                        var w = g.MeasureString(dots, f1).Width;
                        g.DrawString(dots, f1, B(muted), Width - w - S(2), y1);
                    }
                    (text, col) = AccountLine(a, fg, muted, red, amber);
                }
                g.DrawString(text, text.Contains("LOCKED") || text.Contains("COOLDOWN") ? f2 : f1, B(col), S(2), y2);
            }
            finally { foreach (var b in brushes.Values) b.Dispose(); }
        }

        (string, Color) AccountLine(Dictionary<string, object> a, Color fg, Color muted, Color red, Color amber)
        {
            var lk = Json.Dict(a, "lock"); var cd = Json.Dict(a, "cooldown");
            if (lk != null) return ("LOCKED until " + Util.Until(Json.Num(lk, "until") ?? 0), red);
            var d = app.Desktop;
            if (Json.Bool(d, "locked")) return ("PC LOCKED until " + Util.Until(Json.Num(d, "until") ?? 0), red);
            if (cd != null) return ("COOLDOWN " + Util.Left(Json.Num(cd, "until") ?? 0), amber);
            if (Json.Dict(a, "window") != null) return ("outside trading window", amber);
            var s = Json.Dict(a, "stats"); var r = Json.Dict(a, "rules");
            var left = Json.Num(s, "loss_left"); var lim = Json.Num(r, "max_daily_loss");
            var mt = Json.Num(r, "max_trades"); var n = (int)(Json.Num(s, "trades") ?? 0);
            var nx = Json.Dict(d, "next_scheduled");
            if (nx != null && (Json.Num(nx, "at") ?? 0) - Util.Now < 3600)
                return ($"auto-lock in {Math.Ceiling(((Json.Num(nx, "at") ?? 0) - Util.Now) / 60):0} min", amber);
            if (!left.HasValue && !mt.HasValue) return ("NO RULES · unprotected", amber);
            var parts = new List<string>();
            if (left.HasValue) parts.Add("left " + Util.Money(left, false));
            parts.Add(mt.HasValue ? $"{n}/{mt} trades" : $"{n} trades");
            bool tight = left.HasValue && lim.HasValue && lim > 0 && left <= 0.25 * lim || mt.HasValue && n >= mt.Value - 1 && n > 0;
            return (string.Join(" · ", parts), tight ? amber : fg);
        }

        (string, Color) StatusLine(List<Dictionary<string, object>> accts, Color fg, Color muted, Color red, Color amber)
        {
            if (!app.Cfg.Ok) return ("click to set up", amber);
            var d = app.Desktop;
            var svcUntil = Json.Num(app.Service, "until") ?? 0;
            double until = Json.Bool(d, "locked") ? Json.Num(d, "until") ?? 0 : (svcUntil > Util.Now ? svcUntil : 0);
            if (until > Util.Now) return (until - Util.Now > 3600 ? "LOCKED until " + Util.Until(until) : "LOCKED · " + Util.Left(until) + " left", red);
            if (!app.Online) return ("offline · " + (app.LastError.Length > 0 ? "no server" : "connecting"), muted);
            // one line per account; urgent ones (locked / cooldown / soon) win, the rest rotate every 4 s
            var urgent = new List<(string, Color)>();
            var normal = new List<(string, Color)>();
            foreach (var a in accts)
            {
                var tag = TrayApp.Short(a);
                var s = Json.Dict(a, "stats"); var r = Json.Dict(a, "rules");
                var lk = Json.Dict(a, "lock"); var cd = Json.Dict(a, "cooldown");
                if (lk != null) { urgent.Add(($"{tag} LOCKED until {Util.Until(Json.Num(lk, "until") ?? 0)}", red)); continue; }
                if (cd != null) { urgent.Add(($"{tag} COOLDOWN {Util.Left(Json.Num(cd, "until") ?? 0)}", amber)); continue; }
                if (Json.Dict(a, "window") != null) { normal.Add(($"{tag} outside window", amber)); continue; }
                var left = Json.Num(s, "loss_left"); var lim = Json.Num(r, "max_daily_loss");
                var mt = Json.Num(r, "max_trades"); var n = (int)(Json.Num(s, "trades") ?? 0);
                if (!left.HasValue && !mt.HasValue) { normal.Add(($"{tag} no rules set", muted)); continue; }
                var parts = new List<string>();
                if (left.HasValue) parts.Add("left " + Util.Money(left, false));
                parts.Add(mt.HasValue ? $"{n}/{mt}" : $"{n} trades");
                bool tight = left.HasValue && lim.HasValue && lim > 0 && left <= 0.25 * lim
                             || mt.HasValue && n >= mt.Value - 1 && n > 0;
                normal.Add(($"{tag} {string.Join(" · ", parts)}", tight ? amber : fg));
            }
            var nx = Json.Dict(d, "next_scheduled");
            if (nx != null)
            {
                var mins = ((Json.Num(nx, "at") ?? 0) - Util.Now) / 60;
                if (mins < 60) urgent.Add(($"auto-lock in {Math.Ceiling(mins):0} min", amber));
            }
            var pool = urgent.Count > 0 ? urgent : normal;
            if (pool.Count == 0) return ("OK", fg);
            return pool[(int)((uint)Environment.TickCount / 4000 % (uint)pool.Count)];
        }
    }

    /// <summary>The panel that opens above the strip: per-account cards, lockout state and the big button.</summary>
    internal sealed class FlyoutForm : Form
    {
        readonly TrayApp app;
        readonly Button done, guard, journal;
        readonly List<KeyValuePair<Rectangle, string>> lockHits = new List<KeyValuePair<Rectangle, string>>();
        readonly Color bg = Color.FromArgb(20, 22, 28), card = Color.FromArgb(31, 34, 43), line = Color.FromArgb(46, 50, 64), text = Color.FromArgb(235, 237, 242),
                       muted = Color.FromArgb(140, 147, 167), green = Color.FromArgb(52, 211, 128), red = Color.FromArgb(242, 86, 114),
                       amber = Color.FromArgb(245, 165, 36), accent = Color.FromArgb(139, 108, 255);

        public FlyoutForm(TrayApp app)
        {
            this.app = app;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            DoubleBuffered = true;
            BackColor = bg;
            Width = 392;
            done = MakeButton("Done for today — lock every account", red, (s, e) => { Close(); app.LockOut(null); });
            guard = MakeButton("Guard", card, (s, e) => app.Open("/"));
            journal = MakeButton("Journal", card, (s, e) => app.OpenJournal(""));
            Controls.AddRange(new Control[] { done, guard, journal });
            Deactivate += (s, e) => Close();
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };
            MouseClick += OnCardClick;
            MouseMove += (s, e) => Cursor = lockHits.Any(h => h.Key.Contains(e.Location)) ? Cursors.Hand : Cursors.Default;
        }

        Button MakeButton(string t, Color c, EventHandler click)
        {
            var b = new Button { Text = t, FlatStyle = FlatStyle.Flat, BackColor = c, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9.5f), Height = 34, Cursor = Cursors.Hand };
            b.FlatAppearance.BorderSize = c == card ? 1 : 0;
            b.FlatAppearance.BorderColor = line;
            b.Click += click;
            return b;
        }

        // per-account lock: the "Lock" pill on a card opens the durations
        void OnCardClick(object sender, MouseEventArgs e)
        {
            foreach (var h in lockHits)
            {
                if (!h.Key.Contains(e.Location)) continue;
                var name = h.Value;
                var m = new ContextMenuStrip();
                m.Items.Add("Lock " + name).Enabled = false;
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add("Until the next session", null, (s, a) => { Close(); app.LockOne(name, null); });
                m.Items.Add("For 30 minutes", null, (s, a) => { Close(); app.LockOne(name, 30); });
                m.Items.Add("For 1 hour", null, (s, a) => { Close(); app.LockOne(name, 60); });
                m.Items.Add("For 2 hours", null, (s, a) => { Close(); app.LockOne(name, 120); });
                m.Items.Add("For 4 hours", null, (s, a) => { Close(); app.LockOne(name, 240); });
                m.Show(this, e.Location);
                return;
            }
        }

        public void ShowNear(Form strip)
        {
            Native.GetWindowRect(strip.Handle, out var r);
            Height = LayoutCards();
            var wa = Screen.FromPoint(new Point(r.Left, r.Top)).WorkingArea;
            Left = Math.Max(wa.Left + 8, Math.Min(r.Left, wa.Right - Width - 8));
            Top = r.Top - Height - 10 < wa.Top ? r.Bottom + 10 : r.Top - Height - 10;
            Show();
            Activate();
        }

        public void RefreshView()
        {
            if (IsDisposed || !Visible) return;
            var h = LayoutCards();
            if (h != Height) { var bottom = Bottom; Height = h; Top = bottom - h; }
            Invalidate();
        }

        int CardH(Dictionary<string, object> a) => 108 + (Json.Dict(a, "native") != null ? 18 : 0) + (Json.Dict(a, "lock") != null ? 18 : 0);

        public int LayoutCards()
        {
            int y = 62 + app.Accounts().Sum(a => CardH(a) + 10) + 52;
            done.SetBounds(14, y, Width - 28, 36);
            guard.SetBounds(14, y + 44, (Width - 36) / 2, 32);
            journal.SetBounds(22 + (Width - 36) / 2, y + 44, (Width - 36) / 2, 32);
            return y + 90;
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        static string Fit(Graphics g, string s, Font f, float w)
        {
            if (g.MeasureString(s, f).Width <= w) return s;
            while (s.Length > 3 && g.MeasureString(s + "…", f).Width > w) s = s.Substring(0, s.Length - 1);
            return s + "…";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            lockHits.Clear();
            var brushes = new Dictionary<Color, SolidBrush>();
            Brush B(Color c) { if (!brushes.TryGetValue(c, out var b)) brushes[c] = b = new SolidBrush(c); return b; }
            using (var pen = new Pen(line)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            using (var h1 = new Font("Segoe UI Semibold", 12.5f))
            using (var f = new Font("Segoe UI", 9f))
            using (var fs = new Font("Segoe UI", 8f))
            using (var fb = new Font("Segoe UI Semibold", 9.5f))
            using (var pillF = new Font("Segoe UI Semibold", 8f))
            using (var big = new Font("Segoe UI Semibold", 17f))
            {
                // header: shield dot + title, session + real version, account count
                g.FillEllipse(B(app.Online ? green : red), 16, 19, 8, 8);
                g.DrawString("Tilt Guard", h1, B(text), 28, 10);
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                var sub = app.Online ? $"session {Json.Str(app.Status, "day")} · v{ver.Major}.{ver.Minor}.{ver.Build}" : "offline — " + app.LastError;
                g.DrawString(sub, fs, B(muted), 29, 34);
                var accts = app.Accounts();
                int nLocked = accts.Count(a => Json.Dict(a, "lock") != null);
                var cnt = $"{accts.Count} accounts · {nLocked} locked";
                g.DrawString(cnt, fs, B(muted), Width - 16 - g.MeasureString(cnt, fs).Width, 36);
                int y = 62;
                foreach (var a in accts)
                {
                    int ch = CardH(a);
                    var rc = new RectangleF(14, y, Width - 28, ch);
                    var lk = Json.Dict(a, "lock"); var cd = Json.Dict(a, "cooldown");
                    string state; Color sc;
                    if (lk != null) { state = "LOCKED · " + Util.Until(Json.Num(lk, "until") ?? 0); sc = red; }
                    else if (cd != null) { state = "COOLDOWN · " + Util.Left(Json.Num(cd, "until") ?? 0); sc = amber; }
                    else if (Json.Dict(a, "window") != null) { state = "OUTSIDE WINDOW"; sc = amber; }
                    else { state = Json.Bool(a, "online") ? "LIVE" : "IDLE"; sc = Json.Bool(a, "online") ? green : muted; }
                    using (var p = Round(rc, 9)) { g.FillPath(B(card), p); using (var pen = new Pen(lk != null ? Color.FromArgb(90, sc) : line)) g.DrawPath(pen, p); }
                    using (var p = Round(new RectangleF(rc.X, rc.Y, 4, rc.Height), 2)) g.FillPath(B(sc), p);       // status stripe
                    // status pill (right) first, so the name is fitted into what is left
                    var psz = g.MeasureString(state, pillF);
                    var pr = new RectangleF(rc.Right - 12 - psz.Width - 12, y + 9, psz.Width + 12, 19);
                    using (var p = Round(pr, 9.5f)) g.FillPath(B(Color.FromArgb(38, sc)), p);
                    g.DrawString(state, pillF, B(sc), pr.X + 6, pr.Y + 3);
                    var s = Json.Dict(a, "stats"); var r = Json.Dict(a, "rules");
                    var name = Json.Str(a, "label"); if (string.IsNullOrEmpty(name)) name = Json.Str(a, "name");
                    var plat = Json.Str(a, "platform") ?? "";
                    g.DrawString(Fit(g, TrayApp.Short(a) + " · " + name, fb, pr.X - 28 - 8), fb, B(text), 26, y + 9);
                    g.DrawString((Json.Str(a, "firm") ?? "") + (plat != "" ? "  ·  " + plat : ""), fs, B(muted), 27, y + 28);
                    var pl = Json.Num(s, "day_pl") ?? 0;
                    g.DrawString(Util.Money(pl), big, B(pl > 0 ? green : pl < 0 ? red : text), 23, y + 42);
                    var mt = Json.Num(r, "max_trades");
                    var info = $"open {Util.Money(Json.Num(s, "open_pl"))}   trades {Json.Num(s, "trades") ?? 0}{(mt.HasValue ? "/" + mt : "")}   streak {Json.Num(s, "streak") ?? 0}";
                    g.DrawString(info, fs, B(muted), 150, y + 52);
                    // loss meter
                    var lim = Json.Num(r, "max_daily_loss");
                    float my = y + 82;
                    if (lim.HasValue && lim > 0)
                    {
                        float used = (float)Math.Max(0, Math.Min(1, -pl / lim.Value));
                        float mw = Width - 28 - 26 - (lk == null ? 92 : 14);
                        using (var p = Round(new RectangleF(26, my, mw, 6), 3)) g.FillPath(B(Color.FromArgb(46, 50, 64)), p);
                        if (used > 0.01) using (var p = Round(new RectangleF(26, my, Math.Max(6, mw * used), 6), 3)) g.FillPath(B(used > .8 ? red : used > .5 ? amber : green), p);
                        g.DrawString($"loss {Util.Money(Math.Max(0, -pl), false)} / {Util.Money(lim, false)}", fs, B(muted), 25, my + 8);
                    }
                    else g.DrawString("no daily loss limit", fs, B(muted), 25, my + 2);
                    // per-account Lock pill (only when not locked)
                    if (lk == null)
                    {
                        var lr = new Rectangle((int)rc.Right - 12 - 74, (int)my - 6, 74, 24);
                        using (var p = Round(lr, 12)) { g.FillPath(B(Color.FromArgb(34, red)), p); using (var pen = new Pen(Color.FromArgb(120, red))) g.DrawPath(pen, p); }
                        var lt = "Lock"; var lsz = g.MeasureString(lt, pillF);
                        g.DrawString(lt, pillF, B(red), lr.X + (lr.Width - lsz.Width) / 2, lr.Y + (lr.Height - lsz.Height) / 2);
                        lockHits.Add(new KeyValuePair<Rectangle, string>(lr, Json.Str(a, "name")));
                    }
                    else g.DrawString(Fit(g, "● " + (Json.Str(lk, "reason") ?? "locked"), fs, rc.Width - 30), fs, B(Color.FromArgb(220, red)), 25, my + 24);
                    var nat = Json.Dict(a, "native");
                    if (nat != null)
                    {
                        var nl = "HyperProp · " + (Json.Bool(nat, "settings_locked") ? "rules frozen" : "rules not frozen") + (Json.Dict(nat, "lockout") != null ? " · native lockout on" : "");
                        g.DrawString(nl, fs, B(muted), 25, y + ch - 20);
                    }
                    y += ch + 10;
                }
                // PC lockout status (from the service)
                var svc = app.Service;
                string pc; Color pcc;
                if (svc == null) { pc = "PC lockout service not installed — run TiltGuard.exe install as admin"; pcc = amber; }
                else if (Util.Now - (Json.Num(svc, "updated") ?? 0) > 60) { pc = "PC lockout service not running"; pcc = red; }
                else if (Json.Bool(svc, "blocking")) { var u = Json.Num(svc, "until") ?? 0; pc = $"PC LOCKED until {Util.Until(u)} ({Util.Left(u)} left) · {Json.Strings(svc, "applied_sites").Count} sites + {Json.Strings(svc, "targets_apps").Count} apps blocked"; pcc = red; }
                else { pc = "PC lockout armed · " + Json.Strings(svc, "targets_sites").Count + " sites, " + Json.Strings(svc, "targets_apps").Count + " apps"; pcc = muted; }
                if (Json.Bool(svc, "nt8_waiting")) { pc += " · NinjaTrader stays open until every account is flat"; pcc = amber; }
                g.FillEllipse(B(pcc == muted ? green : pcc), 16, y + 9, 7, 7);
                g.DrawString(pc, fs, B(pcc), new RectangleF(28, y + 4, Width - 42, 36));
            }
            foreach (var b in brushes.Values) b.Dispose();
        }
    }
}
