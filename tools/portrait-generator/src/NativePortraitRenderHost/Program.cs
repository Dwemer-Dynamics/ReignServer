using System;
using System.Diagnostics;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text;
using System.Threading;

namespace Bannerlord.NativePortraitRenderHost
{
    internal static class Program
    {
        private const string JobEnvironmentVariable = "REIGN_NATIVE_PORTRAIT_JOB";
        private const string GamePathEnvironmentVariable = "BANNERLORD_GAME_PATH";
        private const string PrivateDesktopMarker = "--native-portrait-private-desktop";
        private static string _gameBin = string.Empty;

        [ThreadStatic]
        private static HashSet<string> _assembliesBeingResolved;

        private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string pathName);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(
            string desktopName,
            IntPtr device,
            IntPtr deviceMode,
            uint flags,
            uint desiredAccess,
            IntPtr securityAttributes);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StartupInfo
        {
            public int Size;
            public string Reserved;
            public string Desktop;
            public string Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2Size;
            public IntPtr Reserved2;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            string gameRoot = Environment.GetEnvironmentVariable(GamePathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(gameRoot))
            {
                return 31;
            }

            _gameBin = Path.Combine(gameRoot, "bin", "Win64_Shipping_Client");
            if (!File.Exists(Path.Combine(_gameBin, "TaleWorlds.Native.dll")))
            {
                return 32;
            }

            Directory.SetCurrentDirectory(_gameBin);
            SetDllDirectory(_gameBin);
            AppDomain.CurrentDomain.AssemblyResolve += ResolveGameAssembly;
            Environment.SetEnvironmentVariable(
                "PATH",
                _gameBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? string.Empty));

            bool hasRenderJob = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable(JobEnvironmentVariable));
            bool isPrivateDesktopChild = Array.Exists(
                args,
                value => string.Equals(value, PrivateDesktopMarker, StringComparison.Ordinal));
            if (hasRenderJob && !isPrivateDesktopChild)
            {
                return RunOnPrivateDesktop(args);
            }

            if (hasRenderJob)
            {
                StartRenderWindowSuppressor();
            }

            var gameArguments = new List<string>();
            foreach (string argument in args)
            {
                if (!string.Equals(argument, PrivateDesktopMarker, StringComparison.Ordinal))
                {
                    gameArguments.Add(argument);
                }
            }
            return RunInstalledBannerlord(gameArguments.ToArray());
        }

        private static int RunInstalledBannerlord(string[] arguments)
        {
            string starterPath = Path.Combine(_gameBin, "TaleWorlds.Starter.Library.dll");
            Assembly starter = Assembly.LoadFrom(starterPath);
            Type program = starter.GetType("TaleWorlds.Starter.Library.Program", true);
            MethodInfo main = program.GetMethod(
                "Main",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(string[]) },
                null);
            if (main == null)
            {
                throw new MissingMethodException(program.FullName, "Main(string[])");
            }
            object result = main.Invoke(null, new object[] { arguments });
            return result is int exitCode ? exitCode : 0;
        }

        private static int RunOnPrivateDesktop(string[] args)
        {
            string desktopName = "BannerlordPortraitWorker-" + Process.GetCurrentProcess().Id;
            IntPtr desktop = CreateDesktop(
                desktopName,
                IntPtr.Zero,
                IntPtr.Zero,
                0,
                0x01FF,
                IntPtr.Zero);
            if (desktop == IntPtr.Zero)
            {
                return 33;
            }

            string executable = Assembly.GetExecutingAssembly().Location;
            var command = new StringBuilder();
            command.Append(QuoteArgument(executable));
            foreach (string argument in args)
            {
                command.Append(' ').Append(QuoteArgument(argument));
            }
            command.Append(' ').Append(PrivateDesktopMarker);

            var startupInfo = new StartupInfo
            {
                Size = Marshal.SizeOf(typeof(StartupInfo)),
                Desktop = desktopName
            };
            ProcessInformation processInformation;
            if (!CreateProcess(
                executable,
                command,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                0,
                IntPtr.Zero,
                _gameBin,
                ref startupInfo,
                out processInformation))
            {
                CloseDesktop(desktop);
                return 34;
            }

            CloseHandle(processInformation.Thread);
            WaitForSingleObject(processInformation.Process, 0xFFFFFFFF);
            uint exitCode;
            int result = GetExitCodeProcess(processInformation.Process, out exitCode)
                ? unchecked((int)exitCode)
                : 35;
            CloseHandle(processInformation.Process);
            CloseDesktop(desktop);
            return result;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static Assembly ResolveGameAssembly(object sender, ResolveEventArgs eventArgs)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(eventArgs.Name);
            }
            catch
            {
                return null;
            }

            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (AssemblyName.ReferenceMatchesDefinition(loaded.GetName(), requested))
                    {
                        return loaded;
                    }
                }
                catch
                {
                    // Dynamic assemblies can reject GetName while the engine is starting.
                }
            }

            HashSet<string> resolving = _assembliesBeingResolved
                ?? (_assembliesBeingResolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            string simpleName = requested.Name ?? string.Empty;
            if (simpleName.Length == 0 || !resolving.Add(simpleName))
            {
                return null;
            }

            try
            {
                string candidate = Path.Combine(_gameBin, simpleName + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            }
            catch
            {
                return null;
            }
            finally
            {
                resolving.Remove(simpleName);
            }
        }

        private static void StartRenderWindowSuppressor()
        {
            int currentProcessId = Process.GetCurrentProcess().Id;
            var thread = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        EnumWindows((window, parameter) =>
                        {
                            uint ownerProcessId;
                            GetWindowThreadProcessId(window, out ownerProcessId);
                            if (ownerProcessId == (uint)currentProcessId)
                            {
                                // TaleWorlds can re-show its native render surface while the engine
                                // boots. Keep it off-screen as well as hidden so the worker never
                                // covers the Character Studio, even for a single loading frame.
                                SetWindowPos(window, new IntPtr(1), -32000, -32000, 1, 1, 0x0010 | 0x0080);
                                ShowWindow(window, 0);
                            }
                            return true;
                        }, IntPtr.Zero);

                        if (Process.GetCurrentProcess().HasExited)
                        {
                            return;
                        }
                    }
                    catch
                    {
                        return;
                    }
                    Thread.Sleep(5);
                }
            })
            {
                IsBackground = true,
                Name = "Native portrait render window suppressor"
            };
            thread.Start();
        }
    }
}
