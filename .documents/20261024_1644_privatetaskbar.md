# PrivateTaskbar 定制任务栏插件 — 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `PrivateTaskbar/` 目录开发一个 NPS 插件：贴屏幕底部的自绘任务栏胶囊条，包含主体功能（媒体控制、时间日期、CPU/RAM）并扩展 GPU 占用与上传/下载网速，右键菜单可逐项开关与调整位置。

**Architecture:** 普通 NPS 插件（`INotchPlugin`），但**不注册灵动岛 `IWidget`**（不占岛内位置），而是在专用 UI 线程上自建 Win32 分层窗口（`WS_EX_LAYERED` + `UpdateLayeredWindow`）绘制任务栏本体；后台线程每秒采样 CPU/RAM/GPU/网速；右键弹出自绘上下文菜单窗口（结构与参考图一致：媒体操作/位置单选组+微调子菜单/显示开关勾选/打开应用/关闭组件）。插件 Id 含 `taskbarwidget` 标记，宿主 `PluginManager.IgnoresDisplayOrder` 会自动把它排除出"显示内容"排序列表。

**Tech Stack:** C# / net10.0-windows10.0.19041.0 / SkiaSharp（宿主传递）/ 自带 P/Invoke（不依赖宿主 Win32 内部结构）/ System.Diagnostics.PerformanceCounter（GPU）。

**Spec:** **需求规格（终版，冲突时以此为准）**：`.documents/20261024_1749_privatetaskbar_requirements.md`；插件 API 依据仓库根 `NPS_PluginsAPI.md`（权威文档）；参考图为右键菜单设计稿（已解析，结构见 Task 5）。

> ⚠️ **本文档写于需求澄清之前**，以下点已被需求规格修正，实施时一律按需求规格：① 插件 Id=`com.dincccc.privatetaskbar.taskbarwidget`、Author=`dincccc`；② 默认位置=**贴托盘(End)**、微调步长 **±10px**；③ 监控默认只勾「时间日期」（ShowClock=true，ShowCpu/Ram/Gpu/Net=false）；④ 歌词**内嵌媒体区第二行**（上行歌名-歌手/下行歌词），不再是独立横排模块；⑤ 媒体区顺序=封面→两行文字→三按钮→5柱频谱→监控组；⑥ 网速箭头文字**统一白色**；⑦ 右键菜单文案精简（开头/居中/贴托盘、开关项无括号注释、关闭组件无长文案）；⑧ 无媒体且全关时保留**透明空壳**（可右键）；⑨ 频谱源=`new AudioAnalyzer()` 复用本体 public 类（媒体激活时启动，降级安全）；⑩ 全部内容关闭时空壳固定小宽度。

## Global Constraints

- 目标框架必须与主程序一致：`net10.0-windows10.0.19041.0`（`NPS_PluginsAPI.md` 第九节第 3 条）。
- 插件工程为类库（`OutputType=Library`），通过 `ProjectReference` 引用 `..\NotchPeninsula.csproj`，代码中 `using NotchPeninsula.Plugins;`。
- 插件 Id 必须包含子串 `taskbarwidget`（大小写不敏感），否则会错误地出现在灵动岛"显示内容"列表里（`Plugin/PluginManager.cs:267-274`）。
- **不修改主程序任何文件**；主工程 csproj/slnx 一律不动。插件自带的 `NativeMethods.cs` 与宿主 `NotchPeninsula.Win32` 重复的声明无害。
- SkiaSharp 画笔（`SKPaint`）一律在 `Draw` 内临时创建、`using` 释放，绝不缓存为字段跨线程复用（文档第九节第 1 条，违者热重载崩溃）。
- 宿主字体（`NotchPeninsula.FontConfig.Normal`）每次绘制现取、绝不缓存也绝不 Dispose（文档第六节两条铁律）。
- 后台线程写、渲染线程读的数据必须用 `volatile`/`Interlocked`/`lock` 保护（文档第九节第 2 条）。
- 设置持久化只用 `host.GetSetting/SetSetting`（自动加 `Plugin.<Id>.` 前缀）。
- 不使用 `host.CreateWindow` 做任务栏本体：该窗口强制屏幕居中、固定尺寸、右上角关闭按钮、Esc 关闭、无右键事件（`Plugin/PluginWindow.cs:319-462`），全部不适合任务栏。
- 构建命令：`dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`（本机 SDK 10.0.402 已装）。
- 不 commit / 不 push（用户规则：提交前需用户确认）。

## 文件结构（全部新建，均在 `PrivateTaskbar/` 下）

| 文件 | 职责 |
|---|---|
| `PrivateTaskbar.csproj` | 工程配置 + 编译后拷贝 dll 到主程序 plugins 目录 |
| `PrivateTaskbarPlugin.cs` | 入口类：生命周期、装配（monitor→window→menu） |
| `NativeMethods.cs` | 插件自用的全部 P/Invoke 与结构体 |
| `TaskbarSettings.cs` | 设置模型：默认值、读写、不可变快照替换 |
| `SystemMonitor.cs` | CPU/RAM/GPU/网速采样（后台 1s）+ 线程安全快照 |
| `SkiaWindow.cs` | 分层窗口基类：UI 线程、消息循环、DIB+SKSurface+ULW 渲染 |
| `TaskbarWindow.cs` | 任务栏窗口：布局定位、模块编排、hit-test、右键弹菜单 |
| `TaskbarRenderer.cs` | 纯绘制：各模块的 Skia 实现（无窗口依赖） |
| `ContextMenu.cs` | 自绘上下文菜单窗口：命令/勾选/单选/子菜单/点外关闭 |

任务顺序即依赖顺序：1 脚手架 → 2 设置+监控 → 3 窗口基础设施 → 4 完整绘制与交互 → 5 右键菜单 → 6 收尾。

---

### Task 1: 工程脚手架与入口类

**Files:**
- Create: `PrivateTaskbar/PrivateTaskbar.csproj`
- Create: `PrivateTaskbar/PrivateTaskbarPlugin.cs`（本任务先给空壳实现，后续任务替换内容）
- Create: `PrivateTaskbar/NativeMethods.cs`（完整版一次写好）

**Interfaces:**
- Consumes: `INotchPlugin`、`IPluginHost`（`Plugin/PluginApi.cs`）
- Produces: `namespace PrivateTaskbar`；入口类 `PrivateTaskbarPlugin`；`NativeMethods`（静态类，后续任务直接用其 P/Invoke）

- [ ] **Step 1.1 写 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- 必须与主程序一致，否则无法被加载 -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <AssemblyName>PrivateTaskbar</AssemblyName>
    <Version>1.0.0</Version>
    <RootNamespace>PrivateTaskbar</RootNamespace>
    <!-- 宿主用了 WinForms 不会传递给插件；本插件要用 Screen.WorkingArea 拿工作区 -->
    <UseWindowsForms>true</UseWindowsForms>
    <Authors>dincccc</Authors>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\NotchPeninsula.csproj" />
  </ItemGroup>

  <!-- 编译后自动把 dll 复制到主程序输出目录的 plugins 文件夹 -->
  <Target Name="InstallToHostPlugins" AfterTargets="Build">
    <PropertyGroup>
      <HostPluginDir>$(MSBuildThisFileDirectory)..\bin\$(Configuration)\$(TargetFramework)\plugins</HostPluginDir>
    </PropertyGroup>
    <MakeDir Directories="$(HostPluginDir)" />
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="$(HostPluginDir)" SkipUnchangedFiles="true" />
  </Target>

</Project>
```

- [ ] **Step 1.2 写入口类空壳**

```csharp
using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public sealed class PrivateTaskbarPlugin : INotchPlugin, IDisposable
{
    public string Id => "com.dincccc.privatetaskbar.taskbarwidget"; // 必须含 taskbarwidget 标记
    public string DisplayName => "定制任务栏";
    public string Version => "1.0.0";
    public string Author => "dincccc";

    public void Initialize(IPluginHost host) { }

    public void Dispose() { }
}
```

- [ ] **Step 1.3 写 NativeMethods.cs（完整）**

```csharp
using System.Runtime.InteropServices;

namespace PrivateTaskbar;

/// <summary>插件自用的 Win32 声明（与宿主 NotchPeninsula.Win32 的重复声明无害，换取不依赖宿主内部结构）。</summary>
internal static class NativeMethods
{
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const int WS_VISIBLE = 0x10000000;
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const uint WM_TIMER = 0x0113;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONDOWN = 0x0201;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONDOWN = 0x0204;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_APP = 0x8000;
    public const uint WM_APP_FRAME = WM_APP + 1;   // 动画帧（timer 兜底之外的主驱动）
    public const uint WM_APP_CLOSEMENU = WM_APP + 2; // 菜单：点外部关闭
    public const uint WM_DISPLAYCHANGE = 0x007E;

    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;
    public static readonly IntPtr HWND_TOPMOST = new(-1);

    public const int IDC_ARROW = 32512;
    public const uint ULW_ALPHA = 0x02;
    public const byte AC_SRC_OVER = 0x00;
    public const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    public struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }
    public delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors; // 32bpp 无调色板，占位即可
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile;
        public ulong ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME { public uint dwLowDateTime, dwHighDateTime; }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClass(ref WNDCLASS wc);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpszClassName, string lpszWindowName, int style,
        int x, int y, int width, int height, IntPtr hwndParent, IntPtr hMenu, IntPtr hInst, IntPtr pvParam);

    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] public static extern bool PostThreadMessage(int idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetTimer(IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(
        IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pprSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern int GetMessageExtraInfo(); // 占位备用

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll")] public static extern int GetCurrentThreadId();
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);
}
```

- [ ] **Step 1.4 构建验证**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded（0 Error；dll 出现在 `bin\Debug\net10.0-windows10.0.19041.0\` 且被拷到 `..\bin\Debug\net10.0-windows10.0.19041.0\plugins\PrivateTaskbar.dll`）

---

### Task 2: 设置模型与系统监控

**Files:**
- Create: `PrivateTaskbar/TaskbarSettings.cs`
- Create: `PrivateTaskbar/SystemMonitor.cs`

**Interfaces:**
- Consumes: `IPluginHost.GetSetting/SetSetting`
- Produces:
  - `TaskbarSettings`：`static TaskbarSettings Load(IPluginHost host)`；实例属性 `Position`（`TaskbarPosition` 枚举：`Start/Center/End`）、`int OffsetX`、`bool ShowMedia/ShowLyric/ShowClock/ShowCpu/ShowRam/ShowGpu/ShowNet`；实例方法 `Save(IPluginHost host)`
  - `SystemMonitor`：ctor `SystemMonitor()`；`void Start()` / `void Dispose()`；`MonitorSnapshot Snapshot { get; }`（volatile 快照）
  - `MonitorSnapshot`：`float Cpu, Ram; float? Gpu; float NetDown, NetUp;`（网速单位 字节/秒）

- [ ] **Step 2.1 写 TaskbarSettings.cs**

```csharp
using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public enum TaskbarPosition { Start, Center, End }

/// <summary>任务栏设置。整个对象不可变，修改 = 新实例替换（volatile 引用，渲染线程安全读）。</summary>
public sealed class TaskbarSettings
{
    public TaskbarPosition Position { get; init; } = TaskbarPosition.End; // 终版默认：贴托盘
    public int OffsetX { get; init; }                       // 位置微调像素（-500~500，步长 10）
    public bool ShowMedia { get; init; } = true;
    public bool ShowLyric { get; init; } = true;
    public bool ShowClock { get; init; } = true;
    public bool ShowCpu { get; init; } = false;   // 终版默认：只勾时间日期
    public bool ShowRam { get; init; } = false;
    public bool ShowGpu { get; init; } = false;
    public bool ShowNet { get; init; } = false;

    public static TaskbarSettings Load(IPluginHost host)
    {
        static string Get(IPluginHost h, string k, string fb) { try { return h.GetSetting(k, fb); } catch { return fb; } }
        return new TaskbarSettings
        {
            Position = Get(host, "position", "center").ToLowerInvariant() switch
            {
                "start" => TaskbarPosition.Start,
                "end" => TaskbarPosition.End,
                _ => TaskbarPosition.Center,
            },
            OffsetX = Math.Clamp(int.TryParse(Get(host, "offset_x", "0"), out var ox) ? ox : 0, -500, 500),
            ShowMedia = Get(host, "show_media", "1") != "0",
            ShowLyric = Get(host, "show_lyric", "1") != "0",
            ShowClock = Get(host, "show_clock", "1") != "0",
            ShowCpu = Get(host, "show_cpu", "0") != "0",
            ShowRam = Get(host, "show_ram", "0") != "0",
            ShowGpu = Get(host, "show_gpu", "0") != "0",
            ShowNet = Get(host, "show_net", "0") != "0",
        };
    }

    public void Save(IPluginHost host)
    {
        host.SetSetting("position", Position switch { TaskbarPosition.Start => "start", TaskbarPosition.End => "end", _ => "center" });
        host.SetSetting("offset_x", OffsetX.ToString());
        host.SetSetting("show_media", ShowMedia ? "1" : "0");
        host.SetSetting("show_lyric", ShowLyric ? "1" : "0");
        host.SetSetting("show_clock", ShowClock ? "1" : "0");
        host.SetSetting("show_cpu", ShowCpu ? "1" : "0");
        host.SetSetting("show_ram", ShowRam ? "1" : "0");
        host.SetSetting("show_gpu", ShowGpu ? "1" : "0");
        host.SetSetting("show_net", ShowNet ? "1" : "0");
    }
}
```

- [ ] **Step 2.2 写 SystemMonitor.cs**

```csharp
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
        Sample(); // 建立第一个采样点
        _timer = new System.Threading.Timer(_ => { try { Sample(); } catch { } }, null, 1000, 1000);
    }

    private void Sample()
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
        float? gpu = _lastGpu;
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
                foreach (var c in _gpuCounters) sum += c.NextValue();
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

        // ---- 网速：非环回、非隧道的 Up 接口收发字节累加差值 ----
        long bytesIn = 0, bytesOut = 0;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var stats = nic.GetIPv4InterfaceStatistics();
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
        _timer?.Dispose();
        _timer = null;
        foreach (var c in _gpuCounters) c.Dispose();
        _gpuCounters.Clear();
    }
}
```

- [ ] **Step 2.3 构建验证**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded

---

### Task 3: 分层窗口基础设施与任务栏骨架

**Files:**
- Create: `PrivateTaskbar/SkiaWindow.cs`
- Create: `PrivateTaskbar/TaskbarWindow.cs`（本任务先画时间日期一个模块，端到端可见）

**Interfaces:**
- Consumes: `NativeMethods`
- Produces:
  - `SkiaWindow`（abstract）：`void RunOnUi(Action action)`（任意线程安全投递）；`protected abstract void OnPaint(SKCanvas canvas, int width, int height)`；`protected abstract void OnMouseLeftDown(float x, float y)` / `OnMouseRightDown(float x, float y)` / `OnMouseMove(float x, float y)` / `OnMouseLeftUp(float x, float y)`；`protected void ResizeTo(int width, int height)`（逻辑像素）；`protected void RepositionTo(int x, int y)`；`protected void Invalidate()`（请求一帧）；`protected int ScreenX/ScreenY`（当前物理位置）
  - `TaskbarWindow`：ctor `TaskbarWindow(SystemMonitor monitor, Func<TaskbarSettings> settings)`；`void Show()`（阻塞式消息循环，在专用线程上调用）；`volatile bool Hidden`（"关闭组件"用）

- [ ] **Step 3.1 写 SkiaWindow.cs（分层窗口渲染核心）**

```csharp
using System.Runtime.InteropServices;
using SkiaSharp;

namespace PrivateTaskbar;

/// <summary>
/// 自建 Win32 分层窗口：UI 线程消息循环 + DIB 上直接建 SKSurface + UpdateLayeredWindow 合成。
/// 不用宿主 CreateWindow（那个强制居中、固定尺寸、右上角关闭按钮、Esc 关闭、无右键）。
/// </summary>
public abstract class SkiaWindow : IDisposable
{
    private static readonly NativeMethods.WndProc WndProcStub = StaticWndProc; // 防 GC
    private static int _classRegistered;

    protected IntPtr Hwnd;
    protected int DpiScale100;   // 100 = 1.0x
    protected int _width, _height;         // 逻辑像素
    protected int _physW, _physH, _posX, _posY; // 物理像素
    private IntPtr _memDc, _dib, _dibBits, _oldObj;
    private SKSurface? _surface;
    private readonly string _className;
    private readonly string _title;
    private readonly Queue<Action> _uiQueue = new(); // UI 线程执行的投递队列
    private readonly object _queueLock = new();

    protected SkiaWindow(string className, string title, int width, int height)
    {
        _className = className; _title = title;
        _width = width; _height = height;
    }

    // ---- 子类契约 ----
    protected abstract void OnPaint(SKCanvas canvas, int width, int height);
    protected virtual void OnMouseLeftDown(float x, float y) { }
    protected virtual void OnMouseLeftUp(float x, float y) { }
    protected virtual void OnMouseMove(float x, float y) { }
    protected virtual void OnMouseRightDown(float x, float y) { }
    protected virtual void OnKeyDownEscape() { } // 分层窗口默认不激活，Esc 仅菜单窗口用

    /// <summary>在 UI 线程上安全创建窗口并进入消息循环；返回即窗口已销毁。</summary>
    public void RunMessageLoop(int x, int y)
    {
        DpiScale100 = (int)NativeMethods.GetDpiForSystem(); // 96=>100? 实际返回 96/120/144…
        float scale = DpiScale100 / 96f;
        _physW = (int)(_width * scale); _physH = (int)(_height * scale);

        var hInst = NativeMethods.GetModuleHandle(string.Empty);
        if (Interlocked.CompareExchange(ref _classRegistered, 1, 0) == 0 || _classRegistered == 1)
        {
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = WndProcStub,
                hInstance = hInst,
                lpszClassName = _className,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
            };
            NativeMethods.RegisterClass(ref wc); // 已注册(1410)忽略
            _classRegistered = 2;
        }

        Hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE,
            _className, _title,
            NativeMethods.WS_POPUP, // 不加 VISIBLE：由首次 ULW 呈现后再显示
            x, y, _physW, _physH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero) return;
        _posX = x; _posY = y;

        InitBuffer();
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE); // 置顶但不改位置
        PaintFrame();

        // 消息循环（UI 线程）
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == NativeMethods.WM_QUIT) break;
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
        Cleanup();
    }

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => SkiaHostBridge.Dispatch(hwnd, msg, wParam, lParam);

    // 跨实例的 hwnd→window 映射（同一 UI 线程上的多个窗口共用一个 wndProc）
    private static class SkiaHostBridge
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, SkiaWindow> Map = new();
        internal static IntPtr Dispatch(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            var w = Map.TryGetValue(hwnd, out var win) ? win : null;
            if (w is null) return NativeMethods.DefWindowProc(hwnd, msg, wParam, lParam);
            return w.HandleMessage(msg, wParam, lParam);
        }
        internal static void Register(IntPtr hwnd, SkiaWindow w) => Map[hwnd] = w;
        internal static void Unregister(IntPtr hwnd) => Map.TryRemove(hwnd, out _);
    }

    private IntPtr HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case NativeMethods.WM_PAINT:
                PaintFrame();
                return IntPtr.Zero;
            case NativeMethods.WM_TIMER:
                PaintFrame();
                return IntPtr.Zero;
            case NativeMethods.WM_DISPLAYCHANGE:
                OnDpiOrDisplayChanged();
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(Lo(lParam) * 96f / DpiScale100, Hi(lParam) * 96f / DpiScale100);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONDOWN:
                OnMouseLeftDown(Lo(lParam) * 96f / DpiScale100, Hi(lParam) * 96f / DpiScale100);
                return IntPtr.Zero;
            case NativeMethods.WM_LBUTTONUP:
                OnMouseLeftUp(Lo(lParam) * 96f / DpiScale100, Hi(lParam) * 96f / DpiScale100);
                return IntPtr.Zero;
            case NativeMethods.WM_RBUTTONDOWN:
                OnMouseRightDown(Lo(lParam) * 96f / DpiScale100, Hi(lParam) * 96f / DpiScale100);
                return IntPtr.Zero;
            case NativeMethods.WM_APP_FRAME:
                PaintFrame();
                return IntPtr.Zero;
            case NativeMethods.WM_APP_CLOSEMENU:
                CloseFromExternal();
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(Hwnd);
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                SkiaHostBridge.Unregister(Hwnd);
                Hwnd = IntPtr.Zero;
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(Hwnd, msg, wParam, lParam);
    }

    protected virtual void OnDpiOrDisplayChanged() { }
    protected virtual void CloseFromExternal() { }

    // ---- 渲染 ----
    private void InitBuffer()
    {
        CleanupBuffer();
        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        _memDc = NativeMethods.CreateCompatibleDC(screenDc);
        var bmi = new NativeMethods.BITMAPINFO();
        bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>();
        bmi.bmiHeader.biWidth = _physW;
        bmi.bmiHeader.biHeight = -_physH; // 自上而下
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = 0; // BI_RGB
        _dib = NativeMethods.CreateDIBSection(screenDc, ref bmi, 0, out _dibBits, IntPtr.Zero, 0);
        _oldObj = NativeMethods.SelectObject(_memDc, _dib);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);

        var info = new SKImageInfo(_physW, _physH, SKColorType.Bgra8888, SKAlphaType.Premul);
        _surface = SKSurface.Create(info, _dibBits, _physW * 4);
    }

    private void CleanupBuffer()
    {
        _surface?.Dispose(); _surface = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldObj != IntPtr.Zero) NativeMethods.SelectObject(_memDc, _oldObj);
            NativeMethods.DeleteDC(_memDc); _memDc = IntPtr.Zero;
        }
        if (_dib != IntPtr.Zero) { NativeMethods.DeleteObject(_dib); _dib = IntPtr.Zero; }
    }

    /// <summary>画一帧并合成到屏幕。子类 Invalidate() 会驱动它。</summary>
    protected void PaintFrame()
    {
        if (Hwnd == IntPtr.Zero || _surface is null) return;
        var canvas = _surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        float scale = DpiScale100 / 96f;
        canvas.Save();
        canvas.Scale(scale); // 子类按逻辑像素绘制
        try { OnPaint(canvas, _width, _height); }
        catch (Exception ex) { NotchPeninsula.Logger.Error("[PrivateTaskbar] 绘制异常", ex); }
        canvas.Restore();

        var screenDc = NativeMethods.GetDC(IntPtr.Zero);
        var ptSrc = new NativeMethods.POINT(0, 0);
        var ptDst = new NativeMethods.POINT { x = _posX, y = _posY };
        var size = new NativeMethods.SIZE(_physW, _physH);
        var blend = new NativeMethods.BLENDFUNCTION
        {
            BlendOp = NativeMethods.AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = NativeMethods.AC_SRC_ALPHA,
        };
        NativeMethods.UpdateLayeredWindow(Hwnd, screenDc, ref ptDst, ref size, _memDc, ref ptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
    }

    /// <summary>请求重绘一帧（任意线程安全，异步）。</summary>
    protected void Invalidate() => NativeMethods.PostMessage(Hwnd, NativeMethods.WM_APP_FRAME, IntPtr.Zero, IntPtr.Zero);

    /// <summary>改窗口尺寸（逻辑像素）：重建缓冲 + SetWindowPos。</summary>
    protected void ResizeTo(int width, int height)
    {
        _width = Math.Max(1, width); _height = Math.Max(1, height);
        float scale = DpiScale100 / 96f;
        _physW = (int)(_width * scale); _physH = (int)(_height * scale);
        InitBuffer();
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, _physW, _physH,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>移动窗口（屏幕物理坐标）。</summary>
    protected void RepositionTo(int x, int y)
    {
        _posX = x; _posY = y;
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        PaintFrame(); // ULW 需要重述位置
    }

    /// <summary>任意线程投递到 UI 线程执行（经消息队列唤醒）。</summary>
    public void RunOnUi(Action action)
    {
        lock (_queueLock) _uiQueue.Enqueue(action);
        NativeMethods.PostMessage(Hwnd, (uint)(NativeMethods.WM_APP + 0x40), IntPtr.Zero, IntPtr.Zero);
    }

    private static short Lo(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static short Hi(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    private void Cleanup()
    {
        CleanupBuffer();
        if (Hwnd != IntPtr.Zero) { NativeMethods.DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
    }

    public virtual void Dispose() => Cleanup();
}
```

**实现说明（给实施者）：** 上面 `RunMessageLoop` 中投递队列的唤醒消息在 `HandleMessage` 里漏了处理分支，实施时在 switch 中补一条 `case (uint)(NativeMethods.WM_APP + 0x40): DrainQueue(); return IntPtr.Zero;` 并实现 `DrainQueue()`（lock 下 Dequeue 逐个执行，异常 catch 记 Logger）。这是参考实现，允许实施者做等价修正，但**消息循环必须在创建线程上跑、wndProc 必须静态防 GC** 这两点不可改。

- [ ] **Step 3.2 写 TaskbarWindow.cs 骨架（时间日期端到端）**

```csharp
using SkiaSharp;

namespace PrivateTaskbar;

/// <summary>任务栏主窗口：贴工作区底部的胶囊条。专用 UI 线程上 Show()。</summary>
public sealed class TaskbarWindow : SkiaWindow
{
    public const float BarHeight = 52f;      // 逻辑像素
    private const float MarginBottom = 6f, MarginSide = 8f;

    private readonly SystemMonitor _monitor;
    private readonly Func<TaskbarSettings> _settings;
    public volatile bool Hidden;             // 菜单"关闭组件"置 true
    private IntPtr _animTimer;               // 32FPS 动画帧驱动
    private List<HitRegion> _hits = new();

    public TaskbarWindow(SystemMonitor monitor, Func<TaskbarSettings> settings)
        : base("NPSPrivateTaskbar", "NPS-PrivateTaskbar", 400, (int)BarHeight)
    {
        _monitor = monitor;
        _settings = settings;
    }

    private sealed record HitRegion(SKRect Rect, string Action);

    /// <summary>专用线程入口：创建窗口并常驻消息循环。</summary>
    public void Show()
    {
        Relayout(); // 先算宽度和位置
        // 初始位置随便给，RunMessageLoop 前重定位
        RunMessageLoop(0, 0);
    }

    protected override void OnPaint(SKCanvas canvas, int width, int height)
    {
        // 胶囊背景（固定深色，与灵动岛观感一致；不依赖宿主主题）
        using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(22, 22, 26, 232) };
        canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, height / 2f, height / 2f, bg);
        using var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 28) };
        canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, height / 2f, height / 2f, border);

        _hits = TaskbarRenderer.Paint(canvas, width, height, _settings(), _monitor.Snapshot, MediaBridge.Current);
    }

    protected override void OnMouseLeftDown(float x, float y)
    {
        foreach (var h in _hits)
            if (h.Rect.Contains(x, y)) { HitAction(h.Action); return; }
    }

    private void HitAction(string action) => MediaBridge.Invoke(action);

    protected override void OnMouseRightDown(float x, float y) => MenuRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? MenuRequested;   // 入口类订阅：弹右键菜单

    // ---- 布局与定位 ----
    public void Relayout()
    {
        // 1) 量内容宽
        float contentW = TaskbarRenderer.MeasureContent(_settings(), MediaBridge.Current);
        float w = contentW;
        // 2) 计算工作区（WinForms 只在 UI 线程摸）
        var wa = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        float scale = DpiScale100 / 96f;
        int physW = (int)(w * scale), physH = (int)(BarHeight * scale);
        int y = (int)((wa.Bottom - BarHeight - MarginBottom) * scale);
        int x = _settings().Position switch
        {
            TaskbarPosition.Start => (int)((wa.Left + MarginSide) * scale),
            TaskbarPosition.End => (int)((wa.Right - w - MarginSide) * scale),
            _ => (int)((wa.Left + (wa.Width - w) / 2f) * scale),
        };
        x += (int)(_settings().OffsetX * scale);

        if (physW != _physW || physH != _physH) ResizeTo((int)w, (int)BarHeight);
        RepositionTo(x, y);
    }

    public void StartAnimation() => _animTimer = NativeMethods.SetTimer(Hwnd, IntPtr.Zero, 31, IntPtr.Zero);
    // 31ms ≈ 32FPS（系统定时器粒度 15.6ms，请求 16 也会取整到 31）
    public void StopAnimation() { if (_animTimer != IntPtr.Zero) NativeMethods.KillTimer(Hwnd, _animTimer); }

    public void HideBar()
    {
        Hidden = true;
        StopAnimation();
        if (Hwnd != IntPtr.Zero) NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | 0x0080 /*SWP_HIDEWINDOW*/);
    }

    public void ShowBar()
    {
        Hidden = false;
        Relayout();
        StartAnimation();
        if (Hwnd != IntPtr.Zero) NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }
}
```

**实现说明：** `MediaBridge` 是本任务先落的临时静态类（放在 TaskbarRenderer.cs 里），封装 `MediaController.Instance` 的读取与命令，签名：

```csharp
using MC = NotchPeninsula.MediaController;
namespace PrivateTaskbar;

/// <summary>宿主媒体状态的安全读取层：实例可能为 null，属性可能被并发替换，读短、不缓存位图。</summary>
internal static class MediaBridge
{
    public sealed record MediaInfo(bool IsActive, bool IsPlaying, string Title, string Artist, string Lyric, SKBitmap? Thumb);

    public static MediaInfo? Current
    {
        get
        {
            var c = MC.Instance; if (c is null || !c.IsActive) return null;
            return new MediaInfo(c.IsActive, c.IsPlaying, c.Title ?? "", c.Artist ?? "", c.CurrentLyric ?? "", c.Thumbnail);
        }
    }

    public static void Invoke(string action)
    {
        var c = MC.Instance; if (c is null) return;
        try
        {
            switch (action)
            {
                case "media-prev": c.Previous(); break;
                case "media-play": c.TogglePlayPause(); break;
                case "media-next": c.Next(); break;
                case "open-app": c.OpenCurrentApp(); break;
            }
        }
        catch (Exception ex) { NotchPeninsula.Logger.Warn("[PrivateTaskbar] 媒体命令失败: " + ex.Message); }
    }
}
```

本任务 `TaskbarRenderer.Paint/MeasureContent` 先只实现时间日期模块（Task 4 补全），先返回空 hit 列表：

```csharp
using SkiaSharp;
namespace PrivateTaskbar;

internal static class TaskbarRenderer
{
    public const float PadX = 16f, Gap = 18f, DividerGap = 14f;

    public static float MeasureContent(TaskbarSettings s, MediaBridge.MediaInfo? media)
        => PadX * 2 + (s.ShowClock ? 120f : 0f); // Task 4 换成真实测量

    public static List<(SKRect Rect, string Action)> Paint(
        SKCanvas canvas, int width, int height,
        TaskbarSettings settings, MonitorSnapshot snapshot, MediaBridge.MediaInfo? media)
    {
        var hits = new List<(SKRect, string)>();
        float x = PadX;
        if (settings.ShowClock)
        {
            // 时间（粗体大字）+ 右侧两行（日期 / 星期）
            var now = DateTime.Now;
            float cy = height / 2f;
            var tf = PluginFont.Resolve();
            using (var timePaint = new SKPaint { IsAntialias = true, TextSize = 18f, Typeface = tf, Color = new SKColor(255, 255, 255, 245), IsAutohinted = false })
            {
                string t = now.ToString("HH:mm");
                canvas.DrawText(t, x, cy + 6.5f, timePaint);
                x += timePaint.MeasureText(t) + 10f;
            }
            using (var subPaint = new SKPaint { IsAntialias = true, TextSize = 9.5f, Typeface = tf, Color = new SKColor(255, 255, 255, 165) })
            {
                canvas.DrawText(now.ToString("M月d日"), x, cy - 3f, subPaint);
                canvas.DrawText("星期" + "日一二三四五六"[(int)now.DayOfWeek], x, cy + 9f, subPaint);
                x += subPaint.MeasureText(now.ToString("M月d日")) ;
            }
        }
        return hits;
    }
}
```

`PluginFont`（借宿主字体，每次现取、绝不缓存与释放）：

```csharp
using NotchPeninsula;
using SkiaSharp;
namespace PrivateTaskbar;

internal static class PluginFont
{
    public static SKTypeface? Resolve()
    {
        try { return FontConfig.Normal; } catch { }
        try { return FontConfig.Fallback; } catch { }
        return null;
    }
}
```

- [ ] **Step 3.3 接线入口类（临时，Task 6 完善）**

`PrivateTaskbarPlugin` 替换为：

```csharp
using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public sealed class PrivateTaskbarPlugin : INotchPlugin, IDisposable
{
    public string Id => "com.dincccc.privatetaskbar.taskbarwidget";
    public string DisplayName => "定制任务栏";
    public string Version => "1.0.0";
    public string Author => "dincccc";

    private IPluginHost? _host;
    private SystemMonitor? _monitor;
    private Thread? _uiThread;
    private TaskbarWindow? _window;
    private volatile TaskbarSettings _settings = new();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = TaskbarSettings.Load(host);
        _monitor = new SystemMonitor();
        _monitor.Start();

        var monitor = _monitor;
        _uiThread = new Thread(() =>
        {
            _window = new TaskbarWindow(monitor, () => _settings);
            _window.MenuRequested += (s, e) { }; // Task 5 换成弹菜单
            _window.Show();
        })
        { Name = "PrivateTaskbar.UI", IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        // UI 线程随进程退出（IsBackground）；Task 6 换成优雅退出
    }
}
```

**实现说明：** 上面 `MenuRequested += (s, e) { }` 是非法语法占位——实施时写作 `_window.MenuRequested += (s, e) => { };`。

- [ ] **Step 3.4 构建验证**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded。若宿主程序正在运行，重启宿主后屏幕底部中央应出现一条含时间的深色胶囊（手动验证项，可留到 Task 6 一并验）。

---

### Task 4: 完整绘制与交互（媒体/歌词/时间/监控四组）

**Files:**
- Modify: `PrivateTaskbar/TaskbarRenderer.cs`（完整版替换）
- Modify: `PrivateTaskbar/TaskbarWindow.cs`（补 hover 与点击反馈）

**Interfaces:**
- Consumes: `MediaBridge`、`PluginFont`、`MonitorSnapshot`、`TaskbarSettings`
- Produces: `TaskbarRenderer.MeasureContent(TaskbarSettings, MediaInfo?) -> float`（完整版）；`TaskbarRenderer.Paint(...) -> List<(SKRect, string)>`，action 取值约定：`media-prev` / `media-play` / `media-next` / `open-app`

- [ ] **Step 4.1 完整版 TaskbarRenderer**

模块顺序（左→右，终版）：**媒体控件区**（封面 36×36 → 两行文字[上行=歌名-歌手，下行=歌词当前句，「歌词」关闭时下行留空] → 上一曲/播放暂停/下一曲三按钮 → 5 柱迷你频谱）→ **监控组**（时间日期星期 / CPU / RAM / GPU / 网速，各自可关）。相邻大组之间画 1px 半透明分隔线。要点：

- **频谱源**：媒体激活时 `new NotchPeninsula.AudioAnalyzer()`（public，与灵动岛同源），`GetBars()` 每帧现读（返回数组当帧消费、绝不存字段）；无媒体或 `AudioAnalyzer` 不可用时 Dispose/隐藏频谱区（try/catch 降级，不影响其他模块）。
- **无媒体**：媒体控件区整块消失；若监控也全关 → 保留固定小宽度（约 56px）透明空壳，右键菜单仍可用。
- 监控项绘制函数：标签（10px，白色 55% 透明）+ 数值（12.5px，白色 92%），如 `CPU 22%`；GPU 无数据时数值画 `--`；网速：`↑ 3.2MB/s` / `↓ 12.8MB/s` 左右并排，**箭头与文字统一白色**（自绘小三角箭头，不用彩色），单位自适应 `FormatSpeed(float)`（1024 进制，保留 1 位小数，<1024 取整）。
- 数值平滑：渲染侧每帧对 snapshot 做 Lerp（`_smooth += (target - _smooth) * 0.2f`，静态字段缓存平滑值，与宿主 `Renderer.Media.cs:187` 同法）。网速/百分比同理。
- 封面缓存：`private static SKBitmap? _thumbSmall; private static SKBitmap? _thumbSrcKey;` 以 `ReferenceEquals(media.Thumb, _thumbSrcKey)` 为失效键，变化时重画一份 36×36 的缩放副本（自己 new 的，可安全 Dispose 旧副本），宿主原位图绝不缓存不释放。
- 按钮：三个 22×22 圆形热区，图标全部矢量自绘（上一首=竖线+左三角；播放=右三角；暂停=双竖条；下一首=镜像），线宽 1.6f、白色，hover 态（`TaskbarWindow` 传入 hover action）画半透明白圆底。
- 歌词：`media.Lyric` 单行 11px 白 78%，超宽截断加 `…`（MeasureText 比较）。
- 标题超宽（>160px）截断加 `…`；歌手 10px 白 55%。
- 时间日期同 Task 3，宽度以实际 MeasureText 为准（`MeasureContent` 与 `Paint` 用同一套测量逻辑，避免量画不一致——建议私有方法 `MeasureModules(...) -> List<(模块名, 宽度)>` 供两处共用）。
- 常量：`PadX=16, Gap=18, DividerGap=14, BarInnerH=40`。

参考实现骨架（完整逻辑按上述要点补齐，测量与绘制必须共用 `MeasureModules`）：

```csharp
internal static class TaskbarRenderer
{
    // 平滑状态（渲染线程独占）
    private static float _sCpu, _sRam, _sGpu, _sDown, _sUp;
    private static SKBitmap? _thumbSmall, _thumbSrcKey;

    public static float MeasureContent(TaskbarSettings s, MediaBridge.MediaInfo? m) { /* 各模块宽求和 + 分隔线 */ }
    public static List<(SKRect, string)> Paint(...) { /* Lerp 平滑 → 逐模块绘制 → 收集 hit 区 */ }

    private static string FormatSpeed(float bytesPerSec) =>
        bytesPerSec >= 1 << 30 ? $"{bytesPerSec / (1 << 30):0.0}GB/s"
        : bytesPerSec >= 1 << 20 ? $"{bytesPerSec / (1f << 20):0.0}MB/s"
        : bytesPerSec >= 1 << 10 ? $"{bytesPerSec / (1f << 10):0.0}KB/s"
        : $"{(int)bytesPerSec}B/s";
}
```

- [ ] **Step 4.2 TaskbarWindow 补 hover 反馈**

`OnMouseMove` 记录 hover 命中 action（静态 volatile 字段），命中变化时 `Invalidate()`；`Paint` 时把 hover action 传给 Renderer 画按钮高亮。鼠标离开窗口（move 坐标超出）清 hover。

- [ ] **Step 4.3 构建验证**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded

---

### Task 5: 自绘右键上下文菜单

**Files:**
- Create: `PrivateTaskbar/ContextMenu.cs`
- Modify: `PrivateTaskbar/TaskbarWindow.cs`（右键坐标传递）
- Modify: `PrivateTaskbar/PrivateTaskbarPlugin.cs`（菜单构建与设置回写）

**Interfaces:**
- Consumes: `SkiaWindow`、`TaskbarSettings`、`MediaBridge`
- Produces: `ContextMenuWindow`：ctor `ContextMenuWindow(IEnumerable<MenuEntry> entries)`；`void PopupAt(int screenX, int screenY)`（UI 线程）；`event Action? Closed`。菜单项模型：

```csharp
public abstract record MenuEntry;
public sealed record MenuCommand(string Text, Action Run) : MenuEntry;
public sealed record MenuCheck(string Text, bool Checked, Action Toggle) : MenuEntry;
public sealed record MenuRadio(string Text, bool Checked, Action Select) : MenuEntry;
public sealed record MenuSeparator() : MenuEntry;
public sealed record MenuSubmenu(string Text, IReadOnlyList<MenuEntry> Children) : MenuEntry;
```

- [ ] **Step 5.1 实现菜单窗口**

布局规格（对齐参考图）：宽 250 逻辑 px、行高 32、分隔线上下留 5、文字 12px 白 92%、勾选 ✔ 与单选 ● 自绘矢量、子菜单指示 ▶ 自绘右三角、hover 行画圆角高亮块（白 12%）。弹出位置：光标右下方，越界翻转。交互：`SetCapture` 后鼠标点在窗口外 → 关闭并 `ReleaseCapture`；hover 到 `MenuSubmenu` 即在右侧弹出子菜单窗口（子项执行后父子全关）。执行命令后整链关闭。窗口 `WS_EX_NOACTIVATE`，不抢焦点。

- [ ] **Step 5.2 菜单构建（入口类）**

```csharp
private ContextMenuWindow? _menu;

private void Window_MenuRequested(object? s, EventArgs e)
{
    // 在 UI 线程弹菜单（TaskbarWindow 的右键回调已在 UI 线程）
    NativeMethods.GetCursorPos(out var pt);
    var st = _settings;
    _menu?.Close();
    _menu = new ContextMenuWindow(BuildMenu(st));
    _menu.Closed += () => _menu = null;
    _menu.PopupAt(pt.x, pt.y);
}

private IReadOnlyList<MenuEntry> BuildMenu(TaskbarSettings st)
{
    var media = MediaBridge.Current;
    List<MenuEntry> top = new()
    {
        new MenuCommand(media is { IsPlaying: true } ? "暂停" : "播放", () => MediaBridge.Invoke("media-play")),
        new MenuCommand("上一曲", () => MediaBridge.Invoke("media-prev")),
        new MenuCommand("下一曲", () => MediaBridge.Invoke("media-next")),
        new MenuSeparator(),
        new MenuRadio("开头", st.Position == TaskbarPosition.Start, () => Apply(s => s with { Position = TaskbarPosition.Start })),
        new MenuRadio("居中", st.Position == TaskbarPosition.Center, () => Apply(s => s with { Position = TaskbarPosition.Center })),
        new MenuRadio("贴托盘", st.Position == TaskbarPosition.End, () => Apply(s => s with { Position = TaskbarPosition.End })),
        new MenuSubmenu("位置微调", new MenuEntry[]
        {
            new MenuCommand("左移 10px", () => Apply(s => s with { OffsetX = Math.Clamp(s.OffsetX - 10, -500, 500) })),
            new MenuCommand("右移 10px", () => Apply(s => s with { OffsetX = Math.Clamp(s.OffsetX + 10, -500, 500) })),
            new MenuCommand("重置", () => Apply(s => s with { OffsetX = 0 })),
        }),
        new MenuSeparator(),
        new MenuCheck("媒体控件", st.ShowMedia, () => Apply(s => s with { ShowMedia = !s.ShowMedia })),
        new MenuCheck("歌词", st.ShowLyric, () => Apply(s => s with { ShowLyric = !s.ShowLyric })),
        new MenuCheck("时间日期", st.ShowClock, () => Apply(s => s with { ShowClock = !s.ShowClock })),
        new MenuCheck("CPU", st.ShowCpu, () => Apply(s => s with { ShowCpu = !s.ShowCpu })),
        new MenuCheck("RAM", st.ShowRam, () => Apply(s => s with { ShowRam = !s.ShowRam })),
        new MenuCheck("GPU", st.ShowGpu, () => Apply(s => s with { ShowGpu = !s.ShowGpu })),
        new MenuCheck("网速", st.ShowNet, () => Apply(s => s with { ShowNet = !s.ShowNet })),
        new MenuSeparator(),
        new MenuCommand("打开正在播放的应用", () => MediaBridge.Invoke("open-app")),
        new MenuCommand("关闭组件", HideBar),
    };
    return top;
}

private void Apply(Func<TaskbarSettings, TaskbarSettings> mutate)
{
    var next = mutate(_settings);
    next.Save(_host!);          // 持久化
    _settings = next;           // volatile 替换，渲染线程下一帧生效
    _window?.RunOnUi(() => _window?.Relayout()); // 宽度/位置重算
}

private void HideBar()
{
    _window?.RunOnUi(() => _window?.HideBar());
    _host?.PostReminder(new NotchPeninsula.Plugins.ReminderData
    {
        Title = "定制任务栏",
        Body = "任务栏已收起。在「我的插件」中禁用再启用本插件即可恢复显示。",
    });
}
```

**实现说明：** 菜单命令在菜单 UI 线程执行时，`Apply` 内的 `Relayout` 必须经 `RunOnUi` 回到任务栏窗口线程——菜单与任务栏共用同一条 UI 线程时 `RunOnUi` 退化为直接执行，实现时保证两条路径都正确。

- [ ] **Step 5.3 构建验证**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded

---

### Task 6: 生命周期收尾与验证

**Files:**
- Modify: `PrivateTaskbar/PrivateTaskbarPlugin.cs`（优雅停机、字体变更重排、Hidden 会话语义）
- Modify: `PrivateTaskbar/TaskbarWindow.cs`（暴露请求退出的方式）

**Interfaces:** 无新增；完善热重载安全性。

- [ ] **Step 6.1 优雅停机**

`TaskbarWindow` 增加 `public void RequestQuit()`：`PostMessage(Hwnd, WM_CLOSE)`；`RunMessageLoop` 返回后置 `TaskCompletionSource`。`PrivateTaskbarPlugin.Dispose()`：`_window?.RequestQuit()` → `_uiThread.Join(1000)` → `_monitor.Dispose()`。订阅 `FontConfig.Changed`（try/catch 包裹，旧宿主降级）→ `RunOnUi(Relayout)`，`Dispose` 时退订。

- [ ] **Step 6.2 构建复验 + 手动验证清单**

Run: `dotnet build H:\Projects\NotchPeninsula\PrivateTaskbar\PrivateTaskbar.csproj -c Debug`
Expected: Build succeeded, 0 Warning（新增代码部分）

手动验证清单（需真机运行宿主）：
1. 宿主启动（或插件热重载）后，屏幕底部出现胶囊任务栏：媒体控件（有媒体播放时）、时间日期、`CPU/RAM/GPU` 百分比、`↑/↓` 网速。
2. 播放音乐：标题/歌手/封面跟随切歌更新；三个按钮可用（上一曲/播放暂停/下一曲）。
3. 右键任务栏：弹出与参考图同结构的菜单；切换任一"显示 X"后任务栏宽度即时变化且重启宿主后记住。
4. 三个位置单选与"位置微调"子菜单生效且持久化。
5. "关闭组件"：任务栏消失 + 灵动岛弹出提醒；在"我的插件"禁用再启用插件后恢复。
6. 插件中心对本插件热重载不崩溃（重点验证 SKPaint/字体未缓存）。
7. "显示内容"列表中**不出现**本插件（taskbarwidget 标记生效）。

---

## Self-Review 记录

- 规格覆盖：主体功能（媒体控件=Task 4、时间日期=Task 3/4）+ 扩展监控（CPU/RAM=Task 2、GPU=Task 2、网速=Task 2、绘制=Task 4）+ 右键配置开关与位置（Task 5，对齐参考图菜单结构）+ 插件规范（csproj/Id 标记/铁律，Global Constraints）——全覆盖。
- 类型一致性：`MediaBridge.MediaInfo`、`MonitorSnapshot`、`TaskbarSettings`（`with` 表达式要求所有属性 init——已用 record 风格 init）、action 字符串常量（`media-prev/media-play/media-next/open-app`）在 Task 3/4/5 间一致。
- 已知留给实施者的等价修正：SkiaWindow 投递队列唤醒分支（Task 3 已注明）、菜单/任务栏同线程时 RunOnUi 的直通优化。
