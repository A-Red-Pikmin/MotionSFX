using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MotionSFX.Core;

public sealed class PerformanceSnapshot
{
    public DateTime TimestampUtc { get; init; }

    // MotionSFX process metrics.
    public double CpuPercent { get; init; }
    public double RamMegabytes { get; init; }
    public double RamPercent { get; init; }
    public double GpuPercent { get; init; }

    // Whole-system hardware metrics.
    public double SystemCpuPercent { get; init; }
    public string SystemCpuSource { get; init; } = "Unavailable";
    public double SystemRamUsedMegabytes { get; init; }
    public double SystemRamTotalMegabytes { get; init; }
    public double SystemRamPercent { get; init; }
    public double SystemGpuPercent { get; init; }
}

// Samples MotionSFX process usage and whole-system resource usage through Windows APIs.
// Process GPU is its busiest engine; system GPU is the busiest engine summed across all process instances.
public sealed class PerformanceMonitor : IDisposable
{
    private const uint PdhFormatDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;

    private readonly Process _process = Process.GetCurrentProcess();
    private readonly int _logicalProcessors = Math.Max(1, Environment.ProcessorCount);
    private TimeSpan _lastProcessCpu;
    private DateTime _lastSampleUtc;
    private IntPtr _query;
    private IntPtr _gpuCounter;
    private IntPtr _systemCpuCounter;
    private bool _pdhReady;
    private bool _hasSystemCpuBaseline;
    private ulong _lastSystemIdleTime;
    private ulong _lastSystemKernelTime;
    private ulong _lastSystemUserTime;

    public int LogicalProcessorCount => _logicalProcessors;

    public PerformanceMonitor()
    {
        try
        {
            _lastProcessCpu = _process.TotalProcessorTime;
            _lastSampleUtc = DateTime.UtcNow;
            InitializeSystemCpuBaseline();

            if (PdhOpenQueryW(IntPtr.Zero, IntPtr.Zero, out _query) != 0 || _query == IntPtr.Zero)
                return;

            // PDH remains responsible for GPU engine counters. Its CPU counter is kept
            // as a fallback only; normal system CPU sampling uses GetSystemTimes, which
            // does not require administrator privileges or configured PDH counters.
            _ = PdhAddEnglishCounterW(_query, @"\Processor(_Total)\% Processor Time", IntPtr.Zero, out _systemCpuCounter);
            bool gpuReady = PdhAddEnglishCounterW(_query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _gpuCounter) == 0;
            _pdhReady = _systemCpuCounter != IntPtr.Zero || gpuReady;
            if (_pdhReady) PdhCollectQueryData(_query); // Prime rate-based counters.
        }
        catch
        {
            _pdhReady = false;
        }
    }

    public PerformanceSnapshot Sample()
    {
        DateTime now = DateTime.UtcNow;
        double elapsed = Math.Max(0.001, (now - _lastSampleUtc).TotalSeconds);
        double processCpu = 0;
        try
        {
            TimeSpan currentCpu = _process.TotalProcessorTime;
            processCpu = Math.Clamp((currentCpu - _lastProcessCpu).TotalSeconds / elapsed / _logicalProcessors * 100.0, 0, 100);
            _lastProcessCpu = currentCpu;
        }
        catch { }
        _lastSampleUtc = now;

        double processRamMb = 0, processRamPercent = 0;
        double systemRamUsedMb = 0, systemRamTotalMb = 0, systemRamPercent = 0;
        try
        {
            long workingSet = _process.WorkingSet64;
            processRamMb = workingSet / 1024.0 / 1024.0;
            MemoryStatus memory = new() { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref memory) && memory.TotalPhysical > 0)
            {
                systemRamTotalMb = memory.TotalPhysical / 1024.0 / 1024.0;
                systemRamUsedMb = (memory.TotalPhysical - memory.AvailablePhysical) / 1024.0 / 1024.0;
                systemRamPercent = Math.Clamp((memory.TotalPhysical - memory.AvailablePhysical) * 100.0 / memory.TotalPhysical, 0, 100);
                processRamPercent = Math.Clamp(workingSet * 100.0 / memory.TotalPhysical, 0, 100);
            }
        }
        catch { }

        double systemCpu = ReadSystemCpuPercent(out string systemCpuSource);
        double processGpu = -1, systemGpu = -1;
        if (_pdhReady && _query != IntPtr.Zero && PdhCollectQueryData(_query) == 0)
        {
            // Fall back to PDH only if the native system-time API is unavailable.
            if (systemCpu < 0)
            {
                systemCpu = ReadPdhDouble(_systemCpuCounter);
                systemCpuSource = systemCpu >= 0 ? "PDH fallback" : "Unavailable";
            }
            (processGpu, systemGpu) = ReadGpuUtilization();
        }
        else if (systemCpu < 0)
        {
            systemCpuSource = "Unavailable";
        }

        return new PerformanceSnapshot
        {
            TimestampUtc = now,
            CpuPercent = processCpu,
            RamMegabytes = processRamMb,
            RamPercent = processRamPercent,
            GpuPercent = processGpu,
            SystemCpuPercent = systemCpu,
            SystemCpuSource = systemCpuSource,
            SystemRamUsedMegabytes = systemRamUsedMb,
            SystemRamTotalMegabytes = systemRamTotalMb,
            SystemRamPercent = systemRamPercent,
            SystemGpuPercent = systemGpu
        };
    }


    private void InitializeSystemCpuBaseline()
    {
        if (!GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user))
            return;

        _lastSystemIdleTime = ToUInt64(idle);
        _lastSystemKernelTime = ToUInt64(kernel);
        _lastSystemUserTime = ToUInt64(user);
        _hasSystemCpuBaseline = true;
    }

    private double ReadSystemCpuPercent(out string source)
    {
        source = "Unavailable";
        if (!GetSystemTimes(out NativeFileTime idle, out NativeFileTime kernel, out NativeFileTime user))
            return -1;

        ulong currentIdle = ToUInt64(idle);
        ulong currentKernel = ToUInt64(kernel);
        ulong currentUser = ToUInt64(user);
        if (!_hasSystemCpuBaseline)
        {
            _lastSystemIdleTime = currentIdle;
            _lastSystemKernelTime = currentKernel;
            _lastSystemUserTime = currentUser;
            _hasSystemCpuBaseline = true;
            source = "GetSystemTimes (warming up)";
            return -1; // A second sample is needed to calculate utilization.
        }

        ulong idleDelta = currentIdle >= _lastSystemIdleTime ? currentIdle - _lastSystemIdleTime : 0;
        ulong kernelDelta = currentKernel >= _lastSystemKernelTime ? currentKernel - _lastSystemKernelTime : 0;
        ulong userDelta = currentUser >= _lastSystemUserTime ? currentUser - _lastSystemUserTime : 0;
        _lastSystemIdleTime = currentIdle;
        _lastSystemKernelTime = currentKernel;
        _lastSystemUserTime = currentUser;

        source = "GetSystemTimes";
        return CalculateSystemCpuPercent(kernelDelta, userDelta, idleDelta);
    }

    // GetSystemTimes reports kernel time inclusive of idle time. Busy time is therefore
    // (kernel + user) minus idle. Kept internal so the arithmetic can be regression-tested.
    internal static double CalculateSystemCpuPercent(ulong kernelDelta, ulong userDelta, ulong idleDelta)
    {
        ulong total = kernelDelta + userDelta;
        if (total == 0) return -1;
        ulong busy = total > idleDelta ? total - idleDelta : 0;
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private static ulong ToUInt64(NativeFileTime time) => ((ulong)time.High << 32) | time.Low;

    private double ReadPdhDouble(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return -1;
        try
        {
            return PdhGetFormattedCounterValue(counter, PdhFormatDouble, out PdhFormattedValue value, IntPtr.Zero) == 0 && value.CStatus == 0
                ? Math.Clamp(value.DoubleValue, 0, 100)
                : -1;
        }
        catch { return -1; }
    }

    private (double Process, double System) ReadGpuUtilization()
    {
        if (_gpuCounter == IntPtr.Zero) return (-1, -1);
        try
        {
            uint bytes = 0, count = 0;
            uint status = PdhGetFormattedCounterArrayW(_gpuCounter, PdhFormatDouble, ref bytes, ref count, IntPtr.Zero);
            if (bytes == 0 || (status != 0 && status != PdhMoreData)) return (-1, -1);

            IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
            try
            {
                status = PdhGetFormattedCounterArrayW(_gpuCounter, PdhFormatDouble, ref bytes, ref count, buffer);
                if (status != 0) return (-1, -1);

                int itemSize = Marshal.SizeOf<PdhFormattedCounterItem>();
                double processMaximum = -1;
                var systemEngineTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                string processPrefix = "pid_" + Environment.ProcessId + "_";
                for (int i = 0; i < count; i++)
                {
                    PdhFormattedCounterItem item = Marshal.PtrToStructure<PdhFormattedCounterItem>(IntPtr.Add(buffer, i * itemSize));
                    if (item.Value.CStatus != 0 || !double.IsFinite(item.Value.DoubleValue)) continue;
                    double utilization = Math.Clamp(item.Value.DoubleValue, 0, 100);
                    string instanceName = Marshal.PtrToStringUni(item.Name) ?? "";
                    if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;

                    // Instance names start with pid_<process-id>_. Remove that prefix so
                    // values from all processes can be summed for the same physical engine.
                    int processIdSeparator = instanceName.IndexOf('_', 4);
                    if (processIdSeparator >= 0)
                    {
                        string engineKey = instanceName[(processIdSeparator + 1)..];
                        systemEngineTotals[engineKey] = systemEngineTotals.GetValueOrDefault(engineKey) + utilization;
                    }

                    if (instanceName.StartsWith(processPrefix, StringComparison.OrdinalIgnoreCase))
                        processMaximum = Math.Max(processMaximum, utilization);
                }
                double systemMaximum = systemEngineTotals.Count == 0
                    ? -1
                    : Math.Clamp(systemEngineTotals.Values.Max(), 0, 100);
                // If the system counters are working but this process has no active
                // GPU engine instance, treat its current GPU use as zero rather than N/A.
                if (systemMaximum >= 0 && processMaximum < 0) processMaximum = 0;
                return (processMaximum, systemMaximum);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        catch { return (-1, -1); }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        _gpuCounter = IntPtr.Zero;
        _systemCpuCounter = IntPtr.Zero;
        _process.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint Low;
        public uint High;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedValue { public uint CStatus; public double DoubleValue; }
    [StructLayout(LayoutKind.Sequential)]
    private struct PdhFormattedCounterItem { public IntPtr Name; public PdhFormattedValue Value; }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out NativeFileTime idleTime, out NativeFileTime kernelTime, out NativeFileTime userTime);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(IntPtr dataSource, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out PdhFormattedValue value, IntPtr type);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr itemBuffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
