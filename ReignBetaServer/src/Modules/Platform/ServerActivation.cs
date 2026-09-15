using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static int ActiveServerPort;
        private static string ActiveTerminalWindowName = "";
        private static int ActiveTerminalTabIndex;

        private sealed class ActivationWindowCandidate
        {
            public IntPtr Handle;
            public int ProcessId;
            public string Title;
        }

        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [StructLayout(LayoutKind.Sequential)]
        private struct FlashWindowInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashWindowInfo info);

        private static int ActivateExistingServerFromLauncher(int port, string[] args)
        {
            bool healthy = IsExistingServerHealthy(port);
            if (healthy)
            {
                TryActivateExistingServer(port, args, true);
                return 0;
            }

            if (IsLocalPortAvailable(port))
            {
                return 2;
            }

            if (TryActivateExistingServer(port, args, false))
            {
                return 0;
            }

            Console.Error.WriteLine("Port " + port + " is occupied, but no existing Bannerlord Reign server window could be activated.");
            return 3;
        }

        private static bool TryActivateExistingServer(int port, string[] args, bool healthy)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return healthy;
            bool openBrowser = !HasArg(args ?? new string[0], "--no-browser");
            if (healthy && TryRequestExistingServerActivation(port, openBrowser))
            {
                return true;
            }

            bool focused = TryFocusExistingReignProcess();
            if (!focused)
            {
                string terminalWindow = ReadArgument(args, "--terminal-window-name", "");
                focused = TryFocusNamedTerminal(terminalWindow, ReadIntArgument(args, "--terminal-tab-index", 0));
            }
            return healthy || focused;
        }

        private static Dictionary<string, object> ActivateRunningServer(Dictionary<string, object> payload)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
                return new Dictionary<string, object> { ["ok"] = true, ["port"] = ActiveServerPort,
                    ["managedBy"] = "DwemerDistro", ["url"] = "http://127.0.0.1:" + ActiveServerPort + "/" };
            payload = payload ?? new Dictionary<string, object>();
            bool openBrowser = ReadBool(payload, "openBrowser", true);
            bool focused = TryFocusNamedTerminal(ActiveTerminalWindowName, ActiveTerminalTabIndex);
            if (!focused)
            {
                focused = TryForegroundProcessWindow(Process.GetCurrentProcess().Id);
            }
            if (openBrowser && ActiveServerPort > 0)
            {
                OpenControlCenter(ActiveServerPort);
            }

            LogOperational("server.activation_requested", new Dictionary<string, object>
            {
                ["port"] = ActiveServerPort,
                ["terminalWindowName"] = ActiveTerminalWindowName,
                ["terminalFocused"] = focused,
                ["browserOpened"] = openBrowser
            });
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["port"] = ActiveServerPort,
                ["terminalFocused"] = focused,
                ["browserOpened"] = openBrowser
            };
        }

        private static bool TryRequestExistingServerActivation(int port, bool openBrowser)
        {
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + "/api/activate");
                request.Method = "POST";
                request.ContentType = "application/json";
                request.Timeout = 2500;
                request.ReadWriteTimeout = 2500;
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["openBrowser"] = openBrowser }));
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    return response.StatusCode == HttpStatusCode.OK && body.IndexOf("\"ok\":true", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool IsLocalPortAvailable(int port)
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { probe.Stop(); } catch { }
            }
        }

        private static bool TryFocusNamedTerminal(string windowName, int tabIndex)
        {
            if (string.IsNullOrWhiteSpace(windowName)) return false;
            try
            {
                string windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
                string executable = File.Exists(windowsApps) ? windowsApps : "wt.exe";
                Process.Start(new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "-w " + QuoteCommandLineArgument(windowName) + " focus-tab -t " + Math.Max(0, tabIndex),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFocusExistingReignProcess()
        {
            int currentId = Process.GetCurrentProcess().Id;
            foreach (Process process in Process.GetProcessesByName("ReignBetaServer").Where(x => x.Id != currentId))
            {
                try
                {
                    if (TryForegroundProcessWindow(process.Id)) return true;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
            return false;
        }

        private static bool TryForegroundProcessWindow(int processId)
        {
            List<int> lineage = ProcessLineage(processId);
            if (lineage.Count == 0) return false;
            List<ActivationWindowCandidate> candidates = new List<ActivationWindowCandidate>();
            EnumWindows((window, parameter) =>
            {
                if (!IsWindowVisible(window)) return true;
                GetWindowThreadProcessId(window, out uint owner);
                if (!lineage.Contains((int)owner)) return true;
                int length = GetWindowTextLength(window);
                StringBuilder title = new StringBuilder(Math.Max(1, length + 1));
                GetWindowText(window, title, title.Capacity);
                candidates.Add(new ActivationWindowCandidate { Handle = window, ProcessId = (int)owner, Title = title.ToString() });
                return true;
            }, IntPtr.Zero);

            ActivationWindowCandidate selected = candidates
                .OrderByDescending(x => (x.Title ?? "").IndexOf("Bannerlord Reign Server", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(x => lineage.IndexOf(x.ProcessId))
                .FirstOrDefault();
            if (selected == null || selected.Handle == IntPtr.Zero) return false;
            ShowWindowAsync(selected.Handle, 9);
            BringWindowToTop(selected.Handle);
            bool foreground = SetForegroundWindow(selected.Handle);
            if (!foreground)
            {
                FlashWindowInfo flash = new FlashWindowInfo
                {
                    Size = (uint)Marshal.SizeOf(typeof(FlashWindowInfo)),
                    Window = selected.Handle,
                    Flags = 3,
                    Count = 3,
                    Timeout = 0
                };
                FlashWindowEx(ref flash);
            }
            return true;
        }

        private static List<int> ProcessLineage(int processId)
        {
            Dictionary<int, int> parents = new Dictionary<int, int>();
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT ProcessId,ParentProcessId FROM Win32_Process"))
                using (ManagementObjectCollection rows = searcher.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        int id = Convert.ToInt32((uint)row["ProcessId"]);
                        int parent = Convert.ToInt32((uint)row["ParentProcessId"]);
                        parents[id] = parent;
                    }
                }
            }
            catch
            {
            }

            List<int> result = new List<int>();
            HashSet<int> seen = new HashSet<int>();
            int current = processId;
            while (current > 0 && seen.Add(current) && result.Count < 12)
            {
                result.Add(current);
                if (!parents.TryGetValue(current, out current)) break;
            }
            return result;
        }

        private static string ReadArgument(string[] args, string name, string fallback)
        {
            args = args ?? new string[0];
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1] ?? fallback;
            }
            return fallback;
        }

        private static int ReadIntArgument(string[] args, string name, int fallback)
        {
            string value = ReadArgument(args, name, "");
            return int.TryParse(value, out int parsed) ? parsed : fallback;
        }

        private static string QuoteCommandLineArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private static List<Dictionary<string, object>> RunServerActivationSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["caseId"] = id,
                ["passed"] = passed,
                ["summary"] = summary
            });

            string[] args = { "--port", "5123", "--terminal-window-name", "BannerlordReignServer", "--terminal-tab-index", "2" };
            add("server_activation_argument_parsing",
                ReadArgument(args, "--terminal-window-name", "") == "BannerlordReignServer" && ReadIntArgument(args, "--terminal-tab-index", 0) == 2,
                "Launcher activation arguments are parsed deterministically.");

            TcpListener occupied = new TcpListener(IPAddress.Loopback, 0);
            occupied.Start();
            int port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            bool occupiedDetected = !IsLocalPortAvailable(port);
            occupied.Stop();
            bool releasedDetected = IsLocalPortAvailable(port);
            add("server_activation_port_detection", occupiedDetected && releasedDetected,
                "Activation distinguishes an occupied listener from a released port.");

            add("control_center_never_auto_relaunches",
                DecideUnifiedControlCenterExit(true, false, false) == "shutdown",
                "A validated Control Center exit shuts down the lifetime group and can never trigger an automatic relaunch burst.");

            add("control_center_stale_exit_is_ignored",
                DecideUnifiedControlCenterExit(false, false, false) == "ignore"
                    && DecideUnifiedControlCenterExit(true, true, false) == "ignore"
                    && DecideUnifiedControlCenterExit(true, false, true) == "ignore",
                "Stale callbacks and server-initiated closes cannot stop the Control Center.");

            string sessionProfile = Path.Combine(UnifiedControlCenterProfilesRoot(), "session_test");
            add("control_center_profile_cleanup_is_scoped",
                IsAllowedUnifiedControlCenterProfilePath(sessionProfile, false)
                    && IsAllowedUnifiedControlCenterProfilePath(Path.Combine(DataDir, "unified_web_ui"), true)
                    && !IsAllowedUnifiedControlCenterProfilePath(DataDir, false)
                    && !IsAllowedUnifiedControlCenterProfilePath(Path.Combine(DataDir, "campaigns"), true),
                "Startup profile cleanup can remove only dedicated Reign browser sessions and the two exact legacy profile folders.");

            string browserArguments = BuildUnifiedControlCenterBrowserArguments(
                "msedge.exe",
                "http://127.0.0.1:5101/",
                sessionProfile);
            add("control_center_blocks_browser_onboarding",
                browserArguments.Contains("--disable-sync")
                    && browserArguments.Contains("msEdgeFirstRunExperience")
                    && browserArguments.Contains("msEdgeImplicitSignIn")
                    && browserArguments.Contains("--inprivate"),
                "The dedicated Control Center cannot enable browser sync, implicit account sign-in, or Edge onboarding prompts.");
            add("control_center_owns_edge_process_without_compat_relaunch",
                browserArguments.Contains("--edge-skip-compat-layer-relaunch"),
                "The Edge app process cannot escape unified lifetime ownership through a short-lived compatibility relaunch wrapper.");
            return results;
        }
    }
}
