using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SerialPortSimulator.Services;

internal sealed class SelfResourceMonitor
{
    private const int ErrorBadLength = 24;
    private readonly Process _process = Process.GetCurrentProcess();
    private byte[]? _workingSetBuffer;
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;

    public SelfResourceMonitor()
    {
        long privateBytes = GetPrivateWorkingSetBytes();
        MemoryMB = (privateBytes > 0 ? privateBytes : _process.WorkingSet64) / 1024 / 1024;
    }

    public double CpuPercent { get; private set; }

    /// <summary>Private working set, as shown in Task Manager's default memory column.</summary>
    public long MemoryMB { get; private set; }

    public void Sample()
    {
        _process.Refresh();
        DateTime now = DateTime.UtcNow;
        TimeSpan cpu = _process.TotalProcessorTime;
        double wallMs = (now - _lastSample).TotalMilliseconds;
        if (wallMs > 0)
            CpuPercent = (cpu - _lastCpu).TotalMilliseconds / (wallMs * Environment.ProcessorCount) * 100;
        _lastCpu = cpu;
        _lastSample = now;

        long privateBytes = GetPrivateWorkingSetBytes();
        MemoryMB = (privateBytes > 0 ? privateBytes : _process.WorkingSet64) / 1024 / 1024;
    }

    private long GetPrivateWorkingSetBytes()
    {
        int pageSize = Environment.SystemPageSize;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            long pageCount = _process.WorkingSet64 / pageSize + 4096;
            int required = checked((int)((pageCount + 1) * IntPtr.Size));
            if (_workingSetBuffer is null || _workingSetBuffer.Length < required)
                _workingSetBuffer = new byte[required];

            if (!QueryWorkingSet(_process.Handle, _workingSetBuffer, (uint)_workingSetBuffer.Length))
            {
                if (Marshal.GetLastWin32Error() != ErrorBadLength) return 0;
                _workingSetBuffer = null;
                _process.Refresh();
                continue;
            }

            long entries = ReadPointer(_workingSetBuffer, 0);
            long capacity = _workingSetBuffer.Length / IntPtr.Size - 1;
            if (entries > capacity)
            {
                _workingSetBuffer = null;
                _process.Refresh();
                continue;
            }

            long privatePages = 0;
            for (long index = 0; index < entries; index++)
            {
                ulong block = ReadBlock(_workingSetBuffer, (int)((index + 1) * IntPtr.Size));
                if (((block >> 8) & 1) == 0) privatePages++;
            }
            return privatePages * pageSize;
        }
        return 0;
    }

    private static long ReadPointer(byte[] buffer, int offset) =>
        IntPtr.Size == 8
            ? (long)BitConverter.ToUInt64(buffer, offset)
            : BitConverter.ToUInt32(buffer, offset);

    private static ulong ReadBlock(byte[] buffer, int offset) =>
        IntPtr.Size == 8
            ? BitConverter.ToUInt64(buffer, offset)
            : BitConverter.ToUInt32(buffer, offset);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryWorkingSet(IntPtr process, byte[] info, uint length);
}
