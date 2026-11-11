using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace TiltGuard
{
    /// <summary>
    ///   TiltGuard.exe                       taskbar strip + flyout (started at logon)
    ///   TiltGuard.exe --service             the PC-lockout enforcer (run by the Windows service manager)
    ///   TiltGuard.exe install [--server URL --key KEY]   install / update (asks for admin)
    ///   TiltGuard.exe uninstall             remove (refused while a lock is active)
    ///   TiltGuard.exe --dry-run             print what the enforcer would block right now, change nothing
    /// </summary>
    internal static class Program
    {
        public const string Version = "0.1.6";

        [STAThread]
        static int Main(string[] args)
        {
            var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            try
            {
                switch (cmd)
                {
                    case "--service":
                        ServiceBase.Run(new EnforcerService());
                        return 0;
                    case "install":
                        return Installer.Install(Arg(args, "--server"), Arg(args, "--key"));
                    case "uninstall":
                        return Installer.Uninstall();
                    case "--dry-run":
                        return DryRun();
                    case "--snapshot-strip":
                        TrayApp.SnapshotStrip(args.Length > 1 ? args[1] : "strip");
                        return 0;
                    case "--snapshot":
                        TrayApp.Snapshot(args.Length > 1 ? args[1] : "flyout.png");
                        return 0;
                    default:
                        return Tray();
                }
            }
            catch (Exception e)
            {
                if (cmd == "--service" || cmd == "--dry-run") Console.Error.WriteLine(e);
                else MessageBox.Show(e.Message, "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static string Arg(string[] a, string name)
        {
            var i = Array.FindIndex(a, x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        static int Tray()
        {
            using (var single = new Mutex(true, @"Local\TiltGuardTray", out var first))
            {
                if (!first) return 0;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
            }
            return 0;
        }

        static int DryRun()
        {
            Attach();
            var cfg = Config.Load();
            if (!cfg.Ok) { Console.WriteLine("no config (ProgramData or AppData)"); return 2; }
            using (var c = new GuardClient(cfg))
            {
                var st = c.Status();
                var d = Json.Dict(st, "desktop");
                Console.WriteLine($"server ok · locked={Json.Bool(d, "locked")} until={Json.Num(d, "until")} reason={Json.Str(d, "reason")}");
                Console.WriteLine("sites: " + string.Join(", ", Json.Strings(d, "sites")));
                Console.WriteLine("apps:  " + string.Join(", ", Json.Strings(d, "apps")) + $"  (nt8_flat={Json.Str(d, "nt8_flat") ?? "unknown"})");
                foreach (var a in Json.Dict(st, "accounts").Values.Select(Json.Dict))
                    Console.WriteLine($"  {TrayApp.Short(a)} {Json.Str(a, "name")}: day {Util.Money(Json.Num(Json.Dict(a, "stats"), "day_pl"))} locked={Json.Bool(a, "locked")}");
                var en = new Enforcer(true, m => Console.WriteLine("  " + m));
                en.Cycle(c);
            }
            return 0;
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
        static void Attach() { AttachConsole(-1); }
    }

    internal static class Installer
    {
        static string Exe => Path.Combine(Paths.InstallDir, "TiltGuard.exe");

        static int Elevate(string args)
        {
            var psi = new ProcessStartInfo(Application.ExecutablePath, args) { Verb = "runas", UseShellExecute = true };
            try { using (var p = Process.Start(psi)) { p.WaitForExit(); return p.ExitCode; } }
            catch { MessageBox.Show("Administrator rights are needed to install the PC lockout.", "Tilt Guard"); return 1; }
        }

        static void Sc(string args)
        {
            var p = Process.Start(new ProcessStartInfo("sc.exe", args) { CreateNoWindow = true, UseShellExecute = false });
            p.WaitForExit(20000);
        }

        static ServiceController Svc() => ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == EnforcerService.Name);

        public static int Install(string server, string key)
        {
            if (!Util.IsAdmin())
                return Elevate("install" + (server != null ? $" --server \"{server}\"" : "") + (key != null ? $" --key \"{key}\"" : ""));
            var cfg = Config.Load();
            if (server != null) cfg.Server = server.TrimEnd('/');
            if (key != null) cfg.Key = key;
            if (!cfg.Ok)
            {
                cfg.Server = Microsoft.VisualBasic.Interaction.InputBox("Guard server URL", "Tilt Guard install", "http://127.0.0.1:8940").TrimEnd('/');
                cfg.Key = Microsoft.VisualBasic.Interaction.InputBox("Device key (Guard → Setup page)", "Tilt Guard install", "");
                if (!cfg.Ok) return 1;
            }
            Util.SecureProgramData();
            cfg.Save(Paths.MachineConfig);

            var svc = Svc();
            if (svc != null && svc.Status != ServiceControllerStatus.Stopped)
            {
                svc.Stop();
                svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
            foreach (var p in Process.GetProcessesByName("TiltGuard").Where(p => p.Id != Process.GetCurrentProcess().Id))
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            Directory.CreateDirectory(Paths.InstallDir);
            if (!string.Equals(Path.GetFullPath(Application.ExecutablePath), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase))
                File.Copy(Application.ExecutablePath, Exe, true);

            if (svc == null)
                Sc($"create {EnforcerService.Name} binPath= \"\\\"{Exe}\\\" --service\" start= auto DisplayName= \"Tilt Guard lockout\"");
            else
                Sc($"config {EnforcerService.Name} binPath= \"\\\"{Exe}\\\" --service\" start= auto");
            Sc($"description {EnforcerService.Name} \"Blocks trading sites and apps while Tilt Guard says you are locked out.\"");
            Sc($"failure {EnforcerService.Name} reset= 86400 actions= restart/3000/restart/3000/restart/3000");
            Sc($"start {EnforcerService.Name}");

            using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                run.SetValue("TiltGuard", $"\"{Exe}\"");
            // start the strip un-elevated, in the user's session
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Exe}\"") { UseShellExecute = true });
            MessageBox.Show($"Tilt Guard {Program.Version} installed.\n\n• The strip is in the taskbar next to Mawaqit (right-click it for options).\n• The lockout service runs in the background and starts with Windows.",
                            "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        public static int Uninstall()
        {
            if (!Util.IsAdmin()) return Elevate("uninstall");
            var cfg = Config.Load();
            double until = 0;
            try { if (cfg.Ok) using (var c = new GuardClient(cfg)) until = Json.Bool(Json.Dict(c.Status(), "desktop"), "locked") ? Json.Num(Json.Dict(c.Status(), "desktop"), "until") ?? 1 : 0; }
            catch { }
            try { if (File.Exists(Paths.ServiceState)) until = Math.Max(until, (Json.Num(Json.Obj(File.ReadAllText(Paths.ServiceState)), "until") ?? 0) > Util.Now ? Json.Num(Json.Obj(File.ReadAllText(Paths.ServiceState)), "until") ?? 0 : 0); }
            catch { }
            if (until > Util.Now)
            {
                MessageBox.Show($"You are locked out until {Util.Local(until):ddd HH:mm}. Tilt Guard can be removed after that.", "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 3;
            }
            var svc = Svc();
            if (svc != null)
            {
                if (svc.Status != ServiceControllerStatus.Stopped) { svc.Stop(); svc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20)); }
                Sc($"delete {EnforcerService.Name}");
            }
            new Enforcer(false, m => Util.Log(Paths.ServiceLog, m)).RemoveAll();
            using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)) run?.DeleteValue("TiltGuard", false);
            foreach (var p in Process.GetProcessesByName("TiltGuard").Where(p => p.Id != Process.GetCurrentProcess().Id))
                try { p.Kill(); } catch { }
            MessageBox.Show("Tilt Guard was removed (service, startup entry, browser policies and hosts entries). The program folder stays in Program Files.",
                            "Tilt Guard", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }
}
