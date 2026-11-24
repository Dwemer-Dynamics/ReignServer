using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Bannerlord.NativeCharacterImageGenerator;

internal sealed class OwnedPortraitRenderJob : IDisposable
{
    private const uint KillOnJobClose = 0x2000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int JobListAttribute = 0x0002000D;
    private const int BasicAccountingInformation = 1;
    private const int ExtendedLimitInformation = 9;
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(160);
    private static readonly TimeSpan ClosureWait = TimeSpan.FromSeconds(4);

    private readonly SafeFileHandle _job;
    private readonly Timer _deadlineTimer;
    private readonly DateTimeOffset _deadline;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly TimeSpan _budget;
    private bool _launched;
    private bool? _confirmedClosed;

    public OwnedPortraitRenderJob(DateTimeOffset deadline)
    {
        _deadline = deadline;
        var remaining = deadline - DateTimeOffset.UtcNow;
        _budget = remaining < MaximumLifetime ? remaining : MaximumLifetime;
        ThrowIfExpired();

        _job = CreateJobObjectW(IntPtr.Zero, null);
        try
        {
            if (_job.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create the native portrait job.");
            var limits = new ExtendedLimitInfo { BasicLimits = new() { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(_job, ExtendedLimitInformation, ref limits,
                    (uint)Marshal.SizeOf<ExtendedLimitInfo>()))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot contain the native portrait job.");
            ThrowIfExpired();
            var remainingBudget = _budget - Stopwatch.GetElapsedTime(_started);
            _deadlineTimer = new Timer(_ => TerminateExpiredJob(), null,
                remainingBudget > TimeSpan.Zero ? remainingBudget : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
        catch
        {
            _job.Dispose();
            throw;
        }
    }

    public bool IsExpired => DateTimeOffset.UtcNow >= _deadline
        || Stopwatch.GetElapsedTime(_started) >= _budget;

    public void ThrowIfExpired()
    {
        if (IsExpired)
            throw new TimeoutException("The Windows native portrait attempt reached its deadline.");
    }

    public Process Start(ProcessStartInfo recipe)
    {
        ThrowIfExpired();
        if (_launched || _confirmedClosed.HasValue)
            throw new InvalidOperationException("The native portrait attempt already used its worker.");
        _launched = true;

        IntPtr listSize = IntPtr.Zero;
        InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref listSize);
        if (listSize == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot prepare native portrait job ownership.");

        IntPtr attributes = Marshal.AllocHGlobal(listSize);
        IntPtr jobs = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        bool initialized = false;
        bool retainedJob = false;
        try
        {
            initialized = InitializeProcThreadAttributeList(attributes, 1, 0, ref listSize);
            if (!initialized)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot prepare native portrait job ownership.");
            _job.DangerousAddRef(ref retainedJob);
            jobs = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(jobs, _job.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)JobListAttribute, jobs,
                    (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot set native portrait job ownership.");

            var command = new StringBuilder(QuoteArgument(recipe.FileName));
            foreach (string argument in recipe.ArgumentList)
                command.Append(' ').Append(QuoteArgument(argument));
            var variables = recipe.Environment.OrderBy(variable => variable.Key, StringComparer.OrdinalIgnoreCase)
                .Select(variable => variable.Key + "=" + variable.Value);
            environment = Marshal.StringToHGlobalUni(string.Join('\0', variables) + '\0');
            var startup = new StartupInfoEx
            {
                Info = new() { Size = Marshal.SizeOf<StartupInfoEx>() },
                Attributes = attributes
            };
            ThrowIfExpired();
            // Job assignment is part of process creation, including when the owner exits before resume.
            if (!CreateProcessW(recipe.FileName, command, IntPtr.Zero, IntPtr.Zero, false,
                    CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                    environment, recipe.WorkingDirectory, ref startup, out var created))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start a contained native portrait worker.");
            using var thread = new SafeFileHandle(created.Thread, true);
            using var child = new SafeFileHandle(created.Process, true);
            ThrowIfExpired();
            var process = Process.GetProcessById((int)created.ProcessId);
            try
            {
                // Acquire the managed handle while the original process handle still identifies this instance.
                _ = process.SafeHandle;
                if (ResumeThread(thread) == uint.MaxValue)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resume the contained native portrait worker.");
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (retainedJob) _job.DangerousRelease();
            Marshal.FreeHGlobal(environment);
            Marshal.FreeHGlobal(jobs);
            Marshal.FreeHGlobal(attributes);
        }
    }

    public bool CloseAndConfirm()
    {
        if (_confirmedClosed.HasValue) return _confirmedClosed.Value;
        _confirmedClosed = false;
        _deadlineTimer.Dispose();
        try
        {
            if (!TryCountActive(out uint active)) return false;
            if (active != 0) TerminateJobObject(_job, 1);
            var timer = Stopwatch.StartNew();
            while (true)
            {
                if (!TryCountActive(out active)) return false;
                if (active == 0)
                {
                    _confirmedClosed = true;
                    return true;
                }
                if (timer.Elapsed >= ClosureWait) return false;
                Thread.Sleep(25);
            }
        }
        catch (ObjectDisposedException) { return false; }
        finally { _job.Dispose(); }
    }

    private bool TryCountActive(out uint active)
    {
        active = 0;
        if (!QueryInformationJobObject(_job, BasicAccountingInformation, out var accounting,
                (uint)Marshal.SizeOf<AccountingInfo>(), IntPtr.Zero))
            return false;
        active = accounting.ActiveProcesses;
        return true;
    }

    private void TerminateExpiredJob()
    {
        try
        {
            if (!TerminateJobObject(_job, 1))
                _job.Dispose(); // Kill-on-close still owns this job. No zero-count confirmation is available.
        }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _deadlineTimer.Dispose();
        _job.Dispose();
    }

    private static string QuoteArgument(string value)
    {
        var quoted = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInfo
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInfo
    {
        public BasicLimitInfo BasicLimits;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccountingInfo
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodUserTime, ThisPeriodKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public short ShowWindow, ReservedSize;
        public IntPtr Reserved2, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInfo info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out AccountingInfo info, uint length, IntPtr returnLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributes, int flags, IntPtr attribute,
        IntPtr value, IntPtr size, IntPtr previous, IntPtr returnedSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes,
        IntPtr threadAttributes, bool inheritHandles, uint flags, IntPtr environment, string? cwd,
        ref StartupInfoEx startupInfo, out ProcessInfo processInfo);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
}
