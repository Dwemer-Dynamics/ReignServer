using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformationClass = 9;
        // Let the listener enter its accept loop before Chromium requests the UI.
        // A unique profile prevents Chromium from routing the app launch through
        // an older profile process and immediately exiting the process we own.
        private const int UnifiedControlCenterServerReadyDelayMs = 1000;
        private const int UnifiedControlCenterStartupValidationMs = 1500;
        private const string UnifiedControlCenterProfilesRootName = "unified_control_center_sessions";

        private static readonly object UnifiedControlCenterLock = new object();
        private static Process UnifiedControlCenterProcess;
        private static IntPtr UnifiedLifetimeJobHandle = IntPtr.Zero;
        private static bool UnifiedControlCenterClosingForServer;
        private static bool UnifiedControlCenterLaunchInProgress;
        private static string UnifiedControlCenterProfileDirectory = "";

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            IntPtr job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static void InitializeUnifiedProcessLifetime()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || UnifiedLifetimeJobHandle != IntPtr.Zero)
            {
                return;
            }

            IntPtr job = IntPtr.Zero;
            IntPtr information = IntPtr.Zero;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job == IntPtr.Zero)
                {
                    throw new InvalidOperationException("Windows could not create the Reign process group (error " + Marshal.GetLastWin32Error() + ").");
                }

                JobObjectExtendedLimitInformation limits = new JobObjectExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
                int size = Marshal.SizeOf(typeof(JobObjectExtendedLimitInformation));
                information = Marshal.AllocHGlobal(size);
                Marshal.StructureToPtr(limits, information, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, information, (uint)size))
                {
                    throw new InvalidOperationException("Windows could not configure the Reign process group (error " + Marshal.GetLastWin32Error() + ").");
                }

                if (!AssignProcessToJobObject(job, GetCurrentProcess()))
                {
                    throw new InvalidOperationException("Windows could not bind the Reign server to its process group (error " + Marshal.GetLastWin32Error() + ").");
                }

                // Keep this handle open for the entire server lifetime. If the visible
                // server window is closed or the process is killed, Windows closes the
                // handle and terminates the UI and every helper process in the group.
                UnifiedLifetimeJobHandle = job;
                job = IntPtr.Zero;
                LogOperational("lifetime.group_ready", new Dictionary<string, object>
                {
                    ["processId"] = Process.GetCurrentProcess().Id,
                    ["killOnClose"] = true
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Warning: Reign could not enable all-process close protection.");
                Console.WriteLine(ex.Message);
                LogOperational("lifetime.group_unavailable", new Dictionary<string, object> { ["error"] = ex.Message });
            }
            finally
            {
                if (information != IntPtr.Zero) Marshal.FreeHGlobal(information);
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }

        private static void OpenControlCenter(int port)
        {
            Process existing;
            lock (UnifiedControlCenterLock)
            {
                if (ShutdownRequested)
                {
                    return;
                }

                existing = UnifiedControlCenterProcess;
                if (existing != null && !HasProcessExited(existing))
                {
                    TryForegroundProcessWindow(existing.Id);
                    return;
                }
                if (UnifiedControlCenterLaunchInProgress)
                {
                    return;
                }

                UnifiedControlCenterProcess = null;
                UnifiedControlCenterClosingForServer = false;
                UnifiedControlCenterLaunchInProgress = true;
            }

            string browser = FindUnifiedBrowserExecutable();
            string url = "http://127.0.0.1:" + port + "/";
            if (string.IsNullOrWhiteSpace(browser))
            {
                Console.WriteLine("Microsoft Edge or Google Chrome is required for the unified Reign Control Center.");
                Console.WriteLine("The server is available at " + url + ", but no detached browser tab was opened.");
                LogOperational("ui.unified_browser_missing", new Dictionary<string, object> { ["url"] = url });
                lock (UnifiedControlCenterLock) UnifiedControlCenterLaunchInProgress = false;
                return;
            }

            Process process = null;
            string profileDirectory = "";
            try
            {
                CleanupStaleUnifiedControlCenterProfiles();
                profileDirectory = Path.Combine(
                    UnifiedControlCenterProfilesRoot(),
                    "session_" + Process.GetCurrentProcess().Id.ToString()
                        + "_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(profileDirectory);
                process = Process.Start(new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = BuildUnifiedControlCenterBrowserArguments(browser, url, profileDirectory),
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WindowStyle = ProcessWindowStyle.Normal
                });
                if (process == null)
                {
                    throw new InvalidOperationException("The browser process did not start.");
                }
                if (process.WaitForExit(UnifiedControlCenterStartupValidationMs))
                {
                    throw new InvalidOperationException(
                        "The browser process exited before the Control Center became ready.");
                }

                lock (UnifiedControlCenterLock)
                {
                    UnifiedControlCenterProcess = process;
                    UnifiedControlCenterProfileDirectory = profileDirectory;
                    UnifiedControlCenterLaunchInProgress = false;
                }
                process.Exited += UnifiedControlCenterExited;
                process.EnableRaisingEvents = true;

                Console.WriteLine("The Reign Control Center is attached to this server.");
                Console.WriteLine("Closing either window shuts down Reign completely.");
                LogOperational("ui.unified_opened", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["browser"] = browser,
                    ["processId"] = process.Id,
                    ["profileDirectory"] = profileDirectory,
                    ["startupValidationMs"] = UnifiedControlCenterStartupValidationMs
                });
                process = null;
            }
            catch (Exception ex)
            {
                lock (UnifiedControlCenterLock)
                {
                    UnifiedControlCenterLaunchInProgress = false;
                    if (ReferenceEquals(UnifiedControlCenterProcess, process))
                    {
                        UnifiedControlCenterProcess = null;
                        UnifiedControlCenterProfileDirectory = "";
                    }
                }
                try { process?.Dispose(); } catch { }
                TryDeleteUnifiedControlCenterProfile(profileDirectory);
                Console.WriteLine("Could not open the unified Reign Control Center.");
                Console.WriteLine(ex.Message);
                Console.WriteLine("The server remains available at " + url + ".");
                Console.WriteLine("Reopen the launcher once to try the Control Center again.");
                LogOperational("ui.unified_open_failed", new Dictionary<string, object>
                {
                    ["url"] = url,
                    ["browser"] = browser,
                    ["error"] = ex.Message
                });
            }
        }

        private static void UnifiedControlCenterExited(object sender, EventArgs args)
        {
            Process exitedProcess = sender as Process;
            bool isCurrent;
            bool closingForServer;
            bool shutdownRequested;
            lock (UnifiedControlCenterLock)
            {
                isCurrent = ReferenceEquals(UnifiedControlCenterProcess, exitedProcess);
                closingForServer = UnifiedControlCenterClosingForServer;
                shutdownRequested = ShutdownRequested;
                if (isCurrent)
                {
                    UnifiedControlCenterProcess = null;
                }
            }

            string decision = DecideUnifiedControlCenterExit(
                isCurrent,
                closingForServer,
                shutdownRequested);

            if (decision == "ignore")
            {
                return;
            }

            if (decision == "shutdown")
            {
                Console.WriteLine("Reign Control Center closed. Shutting down the server and helper processes...");
                RequestServerShutdown("unified_ui_closed");
            }
        }

        private static string DecideUnifiedControlCenterExit(
            bool isCurrent,
            bool closingForServer,
            bool shutdownRequested)
        {
            if (!isCurrent || closingForServer || shutdownRequested)
            {
                return "ignore";
            }

            return "shutdown";
        }

        private static void StopUnifiedControlCenter()
        {
            Process process;
            string profileDirectory;
            lock (UnifiedControlCenterLock)
            {
                UnifiedControlCenterClosingForServer = true;
                process = UnifiedControlCenterProcess;
                UnifiedControlCenterProcess = null;
                profileDirectory = UnifiedControlCenterProfileDirectory;
                UnifiedControlCenterProfileDirectory = "";
            }

            try
            {
                if (process != null && !HasProcessExited(process))
                {
                    process.CloseMainWindow();
                    if (!process.WaitForExit(1500)) process.Kill();
                }
            }
            catch
            {
                try { if (process != null && !HasProcessExited(process)) process.Kill(); } catch { }
            }
            finally
            {
                try { process?.Dispose(); } catch { }
                TryDeleteUnifiedControlCenterProfile(profileDirectory);
            }
        }

        private static void ScheduleUnifiedControlCenterOpen(int port)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(UnifiedControlCenterServerReadyDelayMs);
                if (!ShutdownRequested) OpenControlCenter(port);
            });
        }

        private static string UnifiedControlCenterProfilesRoot()
        {
            return Path.Combine(DataDir, UnifiedControlCenterProfilesRootName);
        }

        private static string BuildUnifiedControlCenterBrowserArguments(
            string browser,
            string url,
            string profileDirectory)
        {
            string arguments = "--app=" + QuoteCommandLineArgument(url)
                + " --user-data-dir=" + QuoteCommandLineArgument(profileDirectory)
                + " --no-first-run --disable-first-run-ui --no-default-browser-check"
                + " --disable-background-mode --disable-session-crashed-bubble"
                + " --disable-sync"
                + " --disable-features=msEdgeFirstRunExperience,msEdgeImplicitSignIn";

            // The Control Center is an isolated local application, not a browser
            // profile. Private mode prevents Edge or Chrome from attaching the
            // Windows/browser account to each short-lived Reign session.
            // Current Edge builds can otherwise start a short-lived compatibility
            // wrapper which relaunches the real app process and exits inside the
            // startup validation window. Reign must own the original browser
            // process so the unified lifetime group remains enforceable.
            return arguments + (IsMicrosoftEdge(browser)
                ? " --inprivate --edge-skip-compat-layer-relaunch"
                : " --incognito");
        }

        private static bool IsMicrosoftEdge(string browser)
        {
            return string.Equals(
                Path.GetFileName(browser ?? ""),
                "msedge.exe",
                StringComparison.OrdinalIgnoreCase);
        }

        private static void CleanupStaleUnifiedControlCenterProfiles()
        {
            string root = UnifiedControlCenterProfilesRoot();
            try
            {
                Directory.CreateDirectory(root);
                foreach (string directory in Directory.GetDirectories(root, "session_*"))
                    TryDeleteUnifiedControlCenterProfile(directory);

                // Retire the two fixed profiles used by older builds. Fixed
                // profiles caused Chromium process routing and accumulated cache.
                TryDeleteUnifiedControlCenterProfile(Path.Combine(DataDir, "unified_control_center_v2"), true);
                TryDeleteUnifiedControlCenterProfile(Path.Combine(DataDir, "unified_web_ui"), true);
            }
            catch
            {
            }
        }

        private static void TryDeleteUnifiedControlCenterProfile(string profileDirectory, bool legacy = false)
        {
            if (string.IsNullOrWhiteSpace(profileDirectory)) return;
            try
            {
                string fullPath = Path.GetFullPath(profileDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!IsAllowedUnifiedControlCenterProfilePath(fullPath, legacy)) return;
                if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
            }
            catch
            {
            }
        }

        private static bool IsAllowedUnifiedControlCenterProfilePath(string profileDirectory, bool legacy)
        {
            if (string.IsNullOrWhiteSpace(profileDirectory)) return false;
            try
            {
                string fullPath = Path.GetFullPath(profileDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string allowedRoot = Path.GetFullPath(legacy ? DataDir : UnifiedControlCenterProfilesRoot())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)) return false;
                string name = Path.GetFileName(fullPath);
                return legacy
                    ? name.Equals("unified_control_center_v2", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("unified_web_ui", StringComparison.OrdinalIgnoreCase)
                    : name.StartsWith("session_", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasProcessExited(Process process)
        {
            try { return process == null || process.HasExited; }
            catch { return true; }
        }

        private static bool IsUnifiedControlCenterRunning()
        {
            lock (UnifiedControlCenterLock)
            {
                return UnifiedControlCenterProcess != null && !HasProcessExited(UnifiedControlCenterProcess);
            }
        }

        private static int UnifiedControlCenterProcessId()
        {
            lock (UnifiedControlCenterLock)
            {
                return UnifiedControlCenterProcess != null && !HasProcessExited(UnifiedControlCenterProcess)
                    ? UnifiedControlCenterProcess.Id
                    : 0;
            }
        }

        private static string FindUnifiedBrowserExecutable()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string[] candidates =
            {
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(local, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(local, "Google", "Chrome", "Application", "chrome.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
            }
            return "";
        }
    }
}
