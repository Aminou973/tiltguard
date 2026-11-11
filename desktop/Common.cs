using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

namespace TiltGuard
{
    /// <summary>Paths, config (server + device key) and the small JSON helpers shared by every mode.</summary>
    internal static class Paths
    {
        public static readonly string ProgramData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TiltGuard");
        public static readonly string UserData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TiltGuard");
        public static string MachineConfig => Path.Combine(ProgramData, "config.json");
        public static string UserConfig => Path.Combine(UserData, "config.json");
        public static string ServiceState => Path.Combine(ProgramData, "state.json");
        public static string ServiceLog => Path.Combine(ProgramData, "service.log");
        public static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TiltGuard");
    }

    internal sealed class Config
    {
        public string Server = "";
        public string Key = "";
        public bool Ok => Server.Length > 0 && Key.Length > 0;

        public static Config Load()
        {
            foreach (var p in new[] { Paths.MachineConfig, Paths.UserConfig })
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    var d = Json.Obj(File.ReadAllText(p));
                    var c = new Config { Server = (Json.Str(d, "server") ?? "").TrimEnd('/'), Key = Json.Str(d, "key") ?? "" };
                    if (c.Ok) return c;
                }
                catch { }
            }
            return new Config();
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Serialize(new Dictionary<string, object> { { "server", Server }, { "key", Key } }));
        }
    }

    internal static class Json
    {
        static readonly JavaScriptSerializer S = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public static Dictionary<string, object> Obj(string s) => S.Deserialize<Dictionary<string, object>>(s);
        public static string Serialize(object o) => S.Serialize(o);
        public static Dictionary<string, object> Dict(object o) => o as Dictionary<string, object>;
        public static Dictionary<string, object> Dict(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        public static string Str(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;
        public static bool Bool(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is bool b && b;
        public static double? Num(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null || v is bool) return null;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return null; }
        }
        public static List<string> Strings(Dictionary<string, object> d, string k)
        {
            var o = new List<string>();
            if (d != null && d.TryGetValue(k, out var v) && v is System.Collections.IEnumerable e && !(v is string))
                foreach (var x in e) if (x != null) o.Add(Convert.ToString(x, CultureInfo.InvariantCulture));
            return o;
        }
    }

    /// <summary>Talks to the guard server with the device key (same API as the extension / NT8 add-on).</summary>
    internal sealed class GuardClient : IDisposable
    {
        readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        readonly Config cfg;
        public GuardClient(Config cfg)
        {
            this.cfg = cfg;
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
        }

        public Dictionary<string, object> Status() => Send(HttpMethod.Get, "/api/guard/status", null);

        /// <summary>account = null locks every account + the PC; a name locks only that account (the others keep trading).</summary>
        public Dictionary<string, object> ManualLock(string reason, double? minutes, string account = null)
        {
            var body = new Dictionary<string, object> { { "reason", reason }, { "flatten", true } };
            if (minutes.HasValue) body["minutes"] = minutes.Value;
            if (!string.IsNullOrEmpty(account)) body["account"] = account;
            return Send(HttpMethod.Post, "/api/guard/manual-lock", body);
        }


        Dictionary<string, object> Send(HttpMethod m, string path, object body)
        {
            var req = new HttpRequestMessage(m, cfg.Server + path);
            req.Headers.Add("X-TG-Key", cfg.Key);
            if (body != null) req.Content = new StringContent(Json.Serialize(body), Encoding.UTF8, "application/json");
            var res = http.SendAsync(req).ConfigureAwait(false).GetAwaiter().GetResult();
            var text = res.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
            if (!res.IsSuccessStatusCode) throw new Exception($"{(int)res.StatusCode} {(text.Length > 200 ? text.Substring(0, 200) : text)}");
            return Json.Obj(text);
        }

        public void Dispose() => http.Dispose();
    }

    internal static class Util
    {
        public static double Now => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        public static DateTime Local(double ts) => new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();

        /// <summary>Time left, written so it can't be mistaken for a clock time: "20h 28m" / "12m 05s".</summary>
        public static string Left(double until)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, until - Now));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes:00}m" : $"{t.Minutes}m {t.Seconds:00}s";
        }

        /// <summary>Unlock moment in the PC's local time: "14:00" today, "Tue 14:00" otherwise.</summary>
        public static string Until(double until)
        {
            var d = Local(until);
            return d.Date == DateTime.Now.Date ? d.ToString("HH:mm") : d.ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }

        public static string Money(double? v, bool sign = true)
        {
            if (!v.HasValue) return "—";
            var a = Math.Abs(v.Value).ToString("#,0", CultureInfo.InvariantCulture);
            return v.Value < 0 ? "−$" + a : (sign && v.Value > 0 ? "+$" : "$") + a;
        }

        public static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        /// <summary>ProgramData\TiltGuard: SYSTEM + Administrators write, users read only.</summary>
        public static void SecureProgramData()
        {
            Directory.CreateDirectory(Paths.ProgramData);
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            var inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(Paths.ProgramData, sec);
        }

        public static void Log(string path, string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > 1_000_000) File.Copy(path, path + ".1", true);
                if (fi.Exists && fi.Length > 1_000_000) File.WriteAllText(path, "");
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}\r\n");
            }
            catch { }
        }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; public int Width => Right - Left; public int Height => Bottom - Top; }
        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string name);
        [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h, int x, int y, int w, int hgt, bool repaint);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
        [DllImport("dnsapi.dll")] public static extern uint DnsFlushResolverCache();

        public const int GWL_STYLE = -16, WS_CHILD = 0x40000000, WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
        public const uint LWA_COLORKEY = 1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2;
        public static readonly IntPtr HWND_TOP = IntPtr.Zero, HWND_TOPMOST = new IntPtr(-1);
    }
}
