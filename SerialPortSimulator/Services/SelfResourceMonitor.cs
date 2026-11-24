using System.Diagnostics;

namespace SerialPortSimulator.Services;

internal sealed class SelfResourceMonitor
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;

    public double CpuPercent { get; private set; }

    public long WorkingSetMB => _process.WorkingSet64 / 1024 / 1024;

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
    }
}
