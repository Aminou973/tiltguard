using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace TiltGuard
{
    /// <summary>
    /// The PC lockout. Runs as a Windows service (LocalSystem). While the guard reports an active hard lock it:
    ///   - adds the blocked sites to the Chrome / Edge / Brave URLBlocklist policies and to a marked hosts-file section,
    ///   - closes the blocked apps on sight (NinjaTrader only once every NT8 account is flat),
    /// and removes exactly what it added when the lock ends. The lock end time is cached on disk, so a reboot or a lost
    /// connection keeps the lockout until it expires - it never ends early.
    /// </summary>
    internal sealed class Enforcer
    {
        const string Begin = "# >>> Tilt Guard lockout (managed - removed automatically when the lock ends)";
        const string End = "# <<< Tilt Guard lockout";
        static readonly string Hosts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
        static readonly string[] PolicyKeys =
        {
            @"SOFTWARE\Policies\Google\Chrome\URLBlocklist",
            @"SOFTWARE\Policies\Microsoft\Edge\URLBlocklist",
            @"SOFTWARE\Policies\BraveSoftware\Brave\URLBlocklist",
        };

        readonly bool dry;
        readonly Action<string> log;
        Dictionary<string, object> state;           // persisted in state.json
        readonly Dictionary<int, DateTime> closing = new Dictionary<int, DateTime>();
        double lastServerOk;

        public Enforcer(bool dryRun, Action<string> logger)
        {
            dry = dryRun;
            log = logger;
            state = LoadState();
        }

        static Dictionary<string, object> LoadState()
        {
            try { if (File.Exists(Paths.ServiceState)) return Json.Obj(File.ReadAllText(Paths.ServiceState)) ?? new Dictionary<string, object>(); }
            catch { }
            return new Dictionary<string, object>();
        }

        void SaveState()
        {
            if (dry) return;
            try
            {
                Directory.CreateDirectory(Paths.ProgramData);
                var tmp = Paths.ServiceState + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(state));
                if (File.Exists(Paths.ServiceState)) File.Delete(Paths.ServiceState);
                File.Move(tmp, Paths.ServiceState);
            }
            catch (Exception e) { log("state save failed: " + e.Message); }
        }

        /// <summary>One cycle: ask the guard (or fall back on the cached lock) and apply / remove the blocks.</summary>
        public void Cycle(GuardClient client)
        {
            Dictionary<string, object> desk = null;
            try
            {
                var st = client.Status();
                desk = Json.Dict(st, "desktop");
                lastServerOk = Util.Now;
                state["server_ok"] = lastServerOk;
                state["server_error"] = "";
            }
            catch (Exception e)
            {
                state["server_error"] = e.Message;
            }

            bool locked; double until; string reason; List<string> sites, apps; bool? nt8Flat;
            if (desk != null)
            {
                locked = Json.Bool(desk, "locked");
                until = Json.Num(desk, "until") ?? 0;
                reason = Json.Str(desk, "reason") ?? "";
                sites = Json.Strings(desk, "sites");
                apps = Json.Strings(desk, "apps");
                nt8Flat = desk.TryGetValue("nt8_flat", out var f) && f is bool fb ? fb : (bool?)null;
                state["targets_sites"] = sites;
                state["targets_apps"] = apps;
                state["next_scheduled"] = Json.Dict(desk, "next_scheduled");
            }
            else
            {
                // offline: keep enforcing the cached lock until its end time (never shorter)
                until = Json.Num(state, "until") ?? 0;
                locked = until > Util.Now;
                reason = Json.Str(state, "reason") ?? "";
                sites = Json.Strings(state, "targets_sites");
                apps = Json.Strings(state, "targets_apps");
                nt8Flat = null;
            }
            if (locked)
            {
                // online: the server's end time is the truth; offline: the cached one is kept (never shorter)
                var cachedUntil = Json.Num(state, "until") ?? 0;
                state["until"] = desk != null ? until : Math.Max(until, cachedUntil > Util.Now ? cachedUntil : 0);
                state["reason"] = reason;
            }
            state["locked"] = locked;
            state["nt8_flat"] = nt8Flat;
            state["updated"] = Util.Now;
            state["version"] = Program.Version;

            if (locked) Block(sites); else Unblock();
            if (locked) CloseApps(apps, nt8Flat); else closing.Clear();
            SaveState();
        }

        /// <summary>Fast loop between server calls: keep blocked apps closed.</summary>
        public void Sweep()
        {
            if (!Json.Bool(state, "locked")) return;
            var nt8 = state.TryGetValue("nt8_flat", out var f) && f is bool fb ? fb : (bool?)null;
            CloseApps(Json.Strings(state, "targets_apps"), nt8);
        }

        // ------------------------------------------------------------------ websites
        void Block(List<string> sites)
        {
            var applied = Json.Strings(state, "applied_sites");
            if (applied.Count == sites.Count && !applied.Except(sites, StringComparer.OrdinalIgnoreCase).Any() && Json.Bool(state, "blocking"))
                return;
            log("block sites: " + string.Join(", ", sites));
            SetPolicies(sites);
            SetHosts(sites);
            FlushDns();
            state["applied_sites"] = sites;
            state["blocking"] = true;
            state["blocked_since"] = Json.Num(state, "blocked_since") ?? Util.Now;
        }

        void Unblock()
        {
            if (!Json.Bool(state, "blocking") && !state.ContainsKey("policy_backup")) return;
            log("lock over - removing blocks");
            RestorePolicies();
            SetHosts(new List<string>());
            FlushDns();
            state["blocking"] = false;
            state["applied_sites"] = new List<string>();
            state.Remove("blocked_since");
            state.Remove("until");
            state.Remove("reason");
        }

        void SetPolicies(List<string> sites)
        {
            var backup = Json.Dict(state, "policy_backup") ?? new Dictionary<string, object>();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (var path in PolicyKeys)
                {
                    try
                    {
                        if (!backup.ContainsKey(path))
                        {
                            // remember the user's own list once, before we touch it
                            using (var k = hklm.OpenSubKey(path))
                            {
                                var vals = k == null ? new List<string>() : k.GetValueNames().Select(n => Convert.ToString(k.GetValue(n))).Where(x => !string.IsNullOrEmpty(x)).ToList();
                                backup[path] = new Dictionary<string, object> { { "existed", k != null }, { "values", vals } };
                            }
                        }
                        var orig = Json.Strings(Json.Dict(backup, path), "values");
                        var all = orig.Concat(sites).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        if (dry) { log($"[dry] {path} = {string.Join(" | ", all)}"); continue; }
                        using (var k = hklm.CreateSubKey(path, true))
                        {
                            foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
                            for (int i = 0; i < all.Count; i++) k.SetValue((i + 1).ToString(), all[i], RegistryValueKind.String);
                        }
                    }
                    catch (Exception e) { log($"policy {path}: {e.Message}"); }
                }
            }
            state["policy_backup"] = backup;
        }

        void RestorePolicies()
        {
            var backup = Json.Dict(state, "policy_backup");
            if (backup == null) return;
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (var kv in backup)
                {
                    try
                    {
                        var b = Json.Dict(kv.Value);
                        var orig = Json.Strings(b, "values");
                        if (dry) { log($"[dry] restore {kv.Key} -> {string.Join(" | ", orig)}"); continue; }
                        if (!Json.Bool(b, "existed")) { hklm.DeleteSubKeyTree(kv.Key, false); continue; }
                        using (var k = hklm.CreateSubKey(kv.Key, true))
                        {
                            foreach (var n in k.GetValueNames()) k.DeleteValue(n, false);
                            for (int i = 0; i < orig.Count; i++) k.SetValue((i + 1).ToString(), orig[i], RegistryValueKind.String);
                        }
                    }
                    catch (Exception e) { log($"restore {kv.Key}: {e.Message}"); }
                }
            }
            state.Remove("policy_backup");
        }

        void SetHosts(List<string> sites)
        {
            try
            {
                var text = File.Exists(Hosts) ? File.ReadAllText(Hosts) : "";
                var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
                int a = lines.FindIndex(l => l.StartsWith(Begin)), b = lines.FindIndex(l => l.StartsWith(End));
                if (a >= 0 && b >= a) lines.RemoveRange(a, b - a + 1);
                while (lines.Count > 0 && lines[lines.Count - 1].Trim() == "") lines.RemoveAt(lines.Count - 1);
                if (sites.Count > 0)
                {
                    lines.Add("");
                    lines.Add(Begin);
                    foreach (var s in sites)
                        foreach (var h in new[] { s, "www." + s })
                        {
                            lines.Add("0.0.0.0 " + h);
                            lines.Add(":: " + h);
                        }
                    lines.Add(End);
                }
                var output = string.Join("\r\n", lines) + "\r\n";
                if (output == text) return;
                if (dry) { log("[dry] hosts section: " + sites.Count + " sites"); return; }
                var attrs = File.GetAttributes(Hosts);
                if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(Hosts, attrs & ~FileAttributes.ReadOnly);
                File.WriteAllText(Hosts, output, Encoding.ASCII);
            }
            catch (Exception e) { log("hosts: " + e.Message); }
        }

        void FlushDns()
        {
            if (dry) return;
            try { Native.DnsFlushResolverCache(); } catch { }
        }

        // ------------------------------------------------------------------ apps
        void CloseApps(List<string> apps, bool? nt8Flat)
        {
            bool waitingNt8 = false;
            foreach (var app in apps)
            {
                var name = app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? app.Substring(0, app.Length - 4) : app;
                Process[] procs;
                try { procs = Process.GetProcessesByName(name); } catch { continue; }
                bool isNt8 = name.Equals("NinjaTrader", StringComparison.OrdinalIgnoreCase);
                foreach (var p in procs)
                {
                    try
                    {
                        if (isNt8 && nt8Flat != true)
                        {
                            // never close NinjaTrader with a position open (or unknown): the guard flattens first
                            waitingNt8 = true;
                            continue;
                        }
                        if (dry) { log($"[dry] would close {p.ProcessName} ({p.Id})"); continue; }
                        if (isNt8)
                        {
                            if (!closing.TryGetValue(p.Id, out var since))
                            {
                                closing[p.Id] = DateTime.UtcNow;
                                log($"closing NinjaTrader ({p.Id}) gracefully");
                                p.CloseMainWindow();
                            }
                            else if ((DateTime.UtcNow - since).TotalSeconds > 25) { log($"killing NinjaTrader ({p.Id})"); p.Kill(); }
                        }
                        else
                        {
                            log($"closing {p.ProcessName} ({p.Id})");
                            p.Kill();
                        }
                    }
                    catch (Exception e) { log($"close {name}: {e.Message}"); }
                    finally { p.Dispose(); }
                }
            }
            state["nt8_waiting"] = waitingNt8;
        }

        /// <summary>Uninstall cleanup: remove everything we added (only allowed when no lock is active).</summary>
        public void RemoveAll()
        {
            RestorePolicies();
            SetHosts(new List<string>());
            FlushDns();
            state["blocking"] = false;
            SaveState();
        }
    }

    internal sealed class EnforcerService : ServiceBase
    {
        public const string Name = "TiltGuardService";
        Thread worker;
        volatile bool running;

        public EnforcerService()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            AutoLog = true;
        }

        static void Log(string m) => Util.Log(Paths.ServiceLog, m);

        protected override void OnStart(string[] args)
        {
            running = true;
            worker = new Thread(Run) { IsBackground = true, Name = "enforcer" };
            worker.Start();
        }

        protected override void OnStop()
        {
            running = false;   // blocks stay in place while a lock is active; they are only removed when it ends
            worker?.Join(5000);
        }

        protected override void OnShutdown() => OnStop();

        void Run()
        {
            Log("service start v" + Program.Version);
            var enforcer = new Enforcer(false, Log);
            var nextCycle = DateTime.MinValue;
            GuardClient client = null;
            while (running)
            {
                try
                {
                    if (DateTime.UtcNow >= nextCycle)
                    {
                        var cfg = Config.Load();
                        if (cfg.Ok)
                        {
                            if (client == null) client = new GuardClient(cfg);
                            enforcer.Cycle(client);
                        }
                        nextCycle = DateTime.UtcNow.AddSeconds(5);
                    }
                    else enforcer.Sweep();
                }
                catch (Exception e) { Log("cycle: " + e.Message); client?.Dispose(); client = null; }
                Thread.Sleep(1500);
            }
            Log("service stop");
        }
    }
}
