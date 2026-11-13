using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Dig.Core;

namespace Dig
{
    /// <summary>
    /// Windows service host. Same exe, no extra binary: `sc create` points at
    /// `DigServer.exe --service`, and the SCM calls ServiceBase.Run below.
    ///
    /// Installation is done with sc.exe instead of a custom installer so the whole
    /// thing stays scriptable and needs no MSI, no NuGet, no admin tooling.
    /// </summary>
    internal sealed class ServiceHost : ServiceBase
    {
        public const string Name = "dig";
        public const string DisplayName = "dig - DHT search engine";

        private static readonly ManualResetEvent Ready = new ManualResetEvent(false);
        private static readonly ManualResetEvent StopSignal = new ManualResetEvent(false);
        private static bool _started;

        public ServiceHost()
        {
            ServiceName = Name;
            CanStop = true;
            CanShutdown = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        // ------------------------------------------------------------------- run

        public static int Run(Config cfg)
        {
            try
            {
                Log.Info("SVC", "connecting to the service control manager");
                ServiceBase.Run(new ServiceHost());

                // ServiceBase.Run only returns once the dispatcher stops. If the core
                // never started, that is a failed start, not a clean exit.
                if (!_started)
                {
                    Log.Error("SVC", "service dispatcher exited before the core started");
                    return 1;
                }
                Log.Info("SVC", "service stopped cleanly");
                return 0;
            }
            catch (Exception ex)
            {
                // Not started by the SCM (or the service is not installed): fall back
                // to a plain headless run so the operator still gets a working server.
                Log.Error("SVC", "cannot run as a service: " + ex.Message);
                Log.Info("SVC", "falling back to a headless console run");
                return Fallback(cfg);
            }
        }

        private static int Fallback(Config cfg)
        {
            // Logging is already configured by Main; make sure anyway, because a
            // fallback that writes nowhere is impossible to debug.
            Log.Configure(cfg);
            if (!Program.StartCore(cfg))
            {
                Log.Error("SVC", "fallback start failed");
                Program.Shutdown();
                return 2;
            }
            Log.Info("SVC", "fallback started, waiting for a stop signal");
            StopSignal.WaitOne();
            Program.Shutdown();
            return 0;
        }

        protected override void OnStart(string[] args)
        {
            Log.Info("SVC", "service start requested (interactive=" + Environment.UserInteractive +
                            ", session=" + Process.GetCurrentProcess().SessionId + ")");
            Thread worker = new Thread(delegate()
            {
                try
                {
                    Config cfg = Config.Load(AppDomain.CurrentDomain.BaseDirectory);
                    cfg.Normalize();
                    cfg.EnsureSecrets();
                    Log.Configure(cfg);
                    for (int i = 0; i < cfg.Warnings.Count; i++) Log.Warn("SVC", cfg.Warnings[i]);

                    if (Program.StartCore(cfg))
                    {
                        _started = true;
                        Log.Info("SVC", "service started, public=" + cfg.Port + " admin=" + cfg.AdminPort +
                                        " dht=udp/" + cfg.UdpPort);
                    }
                    else
                    {
                        Log.Error("SVC", "core did not start");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("SVC", "start failed: " + ex);
                }
                finally
                {
                    try { Ready.Set(); } catch { }
                }
            });
            worker.IsBackground = true;
            worker.Name = "svc-start";
            worker.Start();

            // The SCM gives us ~30 s; do not block longer than that.
            Ready.WaitOne(20000);
            if (!_started) Log.Warn("SVC", "service reported start before the core was ready");
        }

        protected override void OnStop()
        {
            Log.Info("SVC", "service stop requested");
            try { Program.RequestStop(); } catch { }
            try { StopSignal.Set(); } catch { }
            try { Program.Shutdown(); } catch { }
        }

        protected override void OnShutdown()
        {
            OnStop();
            base.OnShutdown();
        }

        // --------------------------------------------------------------- install

        public static int Install(Config cfg)
        {
            if (!IsAdmin())
            {
                Console.WriteLine("Administrator rights are required to install a service.");
                Console.WriteLine("Relaunching with a UAC prompt...");
                return Relaunch("--install-service");
            }

            string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DigServer.exe");
            if (!File.Exists(exe))
            {
                Console.Error.WriteLine("DigServer.exe not found next to this process.");
                return 1;
            }

            Console.WriteLine("=== installing service '" + Name + "' ===");
            Console.WriteLine("  exe : " + exe);

            // Remove a stale registration first; ignore failures.
            Run("sc.exe", "stop " + Name);
            Run("sc.exe", "delete " + Name);

            string binPath = "\"" + exe + "\" --service";
            int code = Run("sc.exe",
                "create " + Name +
                " binPath= \"" + binPath + "\"" +
                " DisplayName= \"" + DisplayName + "\"" +
                " start= auto" +
                " obj= LocalSystem" +
                " depend= Tcpip");

            if (code != 0)
            {
                Console.Error.WriteLine("sc create failed with code " + code);
                return code;
            }

            Run("sc.exe", "description " + Name + " \"BitTorrent DHT search engine and indexer\"");
            // restart on failure: 5s, 10s, then 30s
            Run("sc.exe", "failure " + Name + " reset= 86400 actions= restart/5000/restart/10000/restart/30000");

            code = Run("sc.exe", "start " + Name);
            if (code != 0)
            {
                Console.Error.WriteLine("sc start failed with code " + code + " - check the event log and logs\\");
                return code;
            }

            Console.WriteLine();
            Console.WriteLine("  service : " + Name + " (auto start)");
            Console.WriteLine("  public  : http://localhost:" + cfg.Port + cfg.BasePath() + "/");
            Console.WriteLine("  admin   : http://localhost:" + cfg.AdminPort + cfg.BasePath() + "/");
            Console.WriteLine("  logs    : " + cfg.Resolve(cfg.LogDir));
            Console.WriteLine();
            Console.WriteLine("  Next: run tools\\firewall.cmd so the DHT port is reachable.");
            Console.WriteLine("  Control: sc query " + Name + " | sc stop " + Name);
            Console.WriteLine();
            return 0;
        }

        public static int Uninstall()
        {
            if (!IsAdmin())
            {
                Console.WriteLine("Administrator rights are required to remove a service.");
                Console.WriteLine("Relaunching with a UAC prompt...");
                return Relaunch("--uninstall-service");
            }

            Console.WriteLine("=== removing service '" + Name + "' ===");
            Run("sc.exe", "stop " + Name);
            Thread.Sleep(1500);
            int code = Run("sc.exe", "delete " + Name);
            Console.WriteLine(code == 0 ? "  removed" : "  sc delete returned " + code + " (already gone?)");
            return 0;
        }

        private static int Run(string file, string arguments)
        {
            try
            {
                ProcessStartInfo info = new ProcessStartInfo(file, arguments);
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                info.RedirectStandardOutput = true;
                info.RedirectStandardError = true;
                using (Process p = Process.Start(info))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                    if (!string.IsNullOrEmpty(stdout) && stdout.Trim().Length > 0)
                        Console.WriteLine("  " + stdout.Trim());
                    if (!string.IsNullOrEmpty(stderr) && stderr.Trim().Length > 0)
                        Console.WriteLine("  " + stderr.Trim());
                    return p.HasExited ? p.ExitCode : 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("  " + file + " failed: " + ex.Message);
                return 1;
            }
        }

        private static int Relaunch(string argument)
        {
            try
            {
                string exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DigServer.exe");
                ProcessStartInfo info = new ProcessStartInfo(exe, argument);
                info.UseShellExecute = true;
                info.Verb = "runas";
                using (Process p = Process.Start(info))
                {
                    p.WaitForExit(120000);
                    return p.HasExited ? p.ExitCode : 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Elevation cancelled or failed: " + ex.Message);
                return 1;
            }
        }

        private static bool IsAdmin()
        {
            try
            {
                WindowsPrincipal principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }
}
