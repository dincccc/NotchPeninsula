using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PrivateTaskbar;

/// <summary>采样快照（后台线程整包替换，渲染线程 volatile 读）。</summary>
public sealed record MonitorSnapshot(
    float Cpu, float Ram, float? Gpu, float NetDown, float NetUp)
{
    public static readonly MonitorSnapshot Initial = new(0, 0, null, 0, 0);
}

/// <summary>每秒采样：CPU=GetSystemTimes 差值；RAM=GlobalMemoryStatusEx；GPU=性能计数器 GPU Engine 求和；网速=物理网卡收发字节差值。</summary>
public sealed class SystemMonitor : IDisposable
{
    private volatile MonitorSnapshot _snapshot = MonitorSnapshot.Initial;
    public MonitorSnapshot Snapshot => _snapshot;

    private System.Threading.Timer? _timer;
    private ulong _lastIdle, _lastSystem;
    private long _lastBytesIn, _lastBytesOut;
    private long _lastTick;
    private float _lastGpu;
    private DateTime _gpuCountersBuilt = DateTime.MinValue;
    private readonly List<PerformanceCounter> _gpuCounters = new();
    private bool _gpuBroken; // 计数器类别不存在/抛异常后永久停用，显示 "--"

    public void Start()
    {
        _lastTick = Environment.TickCount64;
        Sample(buildGpu: false); // 只建 CPU/网速基线：GPU Engine 枚举数百实例可达秒级，不能卡宿主 UI 线程
        _timer = new System.Threading.Timer(_ => { try { Sample(); } catch { } }, null, 1000, 1000);
    }

    /// <summary>采样一帧。buildGpu=false 时跳过 GPU 性能计数器（含 GetInstanceNames 枚举），只建其余基线。</summary>
    private void Sample(bool buildGpu = true)
    {
        // ---- CPU：GetSystemTimes 差值（与宿主 Renderer.Media.cs 同算法）----
        int cpu = 0;
        if (NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong curIdle = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
            ulong curSys = (((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime)
                         + (((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime);
            if (_lastSystem > 0 && curSys > _lastSystem)
            {
                ulong idleDiff = curIdle - _lastIdle, sysDiff = curSys - _lastSystem;
                cpu = (int)Math.Min(100, (sysDiff - idleDiff) * 100 / sysDiff);
            }
            _lastIdle = curIdle; _lastSystem = curSys;
        }

        // ---- RAM ----
        int ram = 0;
        var mi = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (NativeMethods.GlobalMemoryStatusEx(ref mi)) ram = (int)Math.Min(100, mi.dwMemoryLoad);

        // ---- GPU：GPU Engine 引擎利用率求和（每 60s 重建实例列表，进程进出会增删实例）----
        // buildGpu=false 的首采还没建计数器，给 null 让 UI 显示 "--" 而不是误导性的 0%
        float? gpu = buildGpu ? _lastGpu : null;
        if (buildGpu)
        {
            if (!_gpuBroken)
            {
                try
                {
                    if ((DateTime.UtcNow - _gpuCountersBuilt).TotalSeconds > 60)
                    {
                        foreach (var c in _gpuCounters) c.Dispose();
                        _gpuCounters.Clear();
                        var cat = new PerformanceCounterCategory("GPU Engine");
                        foreach (var inst in cat.GetInstanceNames())
                        {
                            if (!inst.Contains("engtype_", StringComparison.OrdinalIgnoreCase)) continue;
                            try
                            {
                                _gpuCounters.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", inst, readOnly: true));
                            }
                            catch (InvalidOperationException) { /* 实例刚消失，跳过 */ }
                        }
                        _gpuCountersBuilt = DateTime.UtcNow;
                    }
                    float sum = 0;
                    foreach (var c in _gpuCounters)
                    {
                        try { sum += c.NextValue(); }
                        catch (InvalidOperationException) { } // 60s 重建窗口期内个别实例消失：只跳过该实例，不整体降级
                    }
                    _lastGpu = Math.Clamp(sum, 0f, 100f);
                    gpu = _lastGpu;
                }
                catch (Exception)
                {
                    // 类别不存在（无驱动/无权限）或持续异常：永久降级为 "--"
                    foreach (var c in _gpuCounters) c.Dispose();
                    _gpuCounters.Clear();
                    _gpuBroken = true;
                    gpu = null;
                }
            }
            else gpu = null;
        }

        // ---- 网速：非环回、非隧道的 Up 接口收发字节累加差值 ----
        long bytesIn = 0, bytesOut = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var stats = nic.GetIPv4Statistics();
            if (stats is null) continue;
            bytesIn += stats.BytesReceived;
            bytesOut += stats.BytesSent;
        }

        long now = Environment.TickCount64;
        float down = 0, up = 0;
        if (_lastTick > 0 && now > _lastTick && bytesIn >= _lastBytesIn && bytesOut >= _lastBytesOut)
        {
            float dt = (now - _lastTick) / 1000f;
            down = (bytesIn - _lastBytesIn) / dt;
            up = (bytesOut - _lastBytesOut) / dt;
        }
        _lastBytesIn = bytesIn; _lastBytesOut = bytesOut; _lastTick = now;

        _snapshot = new MonitorSnapshot(cpu, ram, gpu, down, up);
    }

    public void Dispose()
    {
        // 给在途回调最多 2s 排空（DisposeAsync 等待回调完成），避免与下面清 _gpuCounters 并发
        try { _timer?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        _timer = null;
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuCounters.Clear();
    }
}
