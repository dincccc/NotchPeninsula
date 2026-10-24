using System.Runtime.InteropServices;
using NotchPeninsula;
using SkiaSharp;

namespace PrivateTaskbar;

/// <summary>任务栏主窗口：贴工作区底部的胶囊条。专用 UI 线程上 Show()。</summary>
public sealed class TaskbarWindow : SkiaWindow
{
    public const float BarHeight = 52f;      // 逻辑像素
    private const float MarginBottom = 6f, MarginSide = 8f;
    private const float RelayoutThresholdPx = 8f; // 内容宽变化超过该值才触发重排（防网速文本微动频繁重建 DIB）

    private readonly SystemMonitor _monitor;
    private readonly Func<TaskbarSettings> _settings;
    public volatile bool Hidden;             // 菜单"关闭组件"置 true（会话级）
    private IntPtr _animTimer;               // 32FPS 动画帧驱动
    private List<(SKRect Rect, string Action)> _hits = new();

    // hover 状态（仅 UI 线程触碰）
    private bool _hovered;
    private bool _trackLeave;
    private string? _hoverAction;
    private float _bgAlpha;                  // 背景 0..1，每帧向目标 Lerp（约 150ms 淡入淡出）

    // 频谱源：媒体激活时创建、失活时释放（仅 UI 线程触碰）
    private AudioAnalyzer? _analyzer;
    private bool _analyzerBroken;            // 构造失败后本会话不再尝试，频谱整块隐藏
    private bool _relayoutPending;

    public TaskbarWindow(SystemMonitor monitor, Func<TaskbarSettings> settings)
        : base("NPSPrivateTaskbar", "NPS-PrivateTaskbar", 400, (int)BarHeight)
    {
        _monitor = monitor;
        _settings = settings;
    }

    /// <summary>专用线程入口：创建窗口并常驻消息循环。首次布局与显示在 OnWindowCreated 里做
    /// （DpiScale100 在 RunMessageLoop 内才初始化，Relayout 必须等它就绪）。
    /// 初始坐标放屏幕外：CreateNativeWindow 的首帧 ULW 会隐式显示窗口，
    /// 在 (0,0) 创建就会在左上角闪一帧，布局完成后再移入目标位置。</summary>
    public void Show()
    {
        RunMessageLoop(-32000, -32000);
    }

    /// <summary>优雅退出：投递 WM_CLOSE，DestroyWindow → WM_DESTROY → PostQuitMessage，消息循环自然返回。</summary>
    public void RequestQuit()
    {
        if (Hwnd != IntPtr.Zero)
            NativeMethods.PostMessage(Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>窗口创建 + 缓冲就绪 + 首帧绘制完成后回调：算好宽度和位置、启动动画帧，画好首帧再显示。</summary>
    protected override void OnWindowCreated()
    {
        Relayout();
        StartAnimation();
        // 屏幕外创建→Relayout 已就位后才显示；必须带 NOMOVE，否则这一句会把刚就位的窗口甩回 (0,0)
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    protected override void OnPaint(SKCanvas canvas, int width, int height)
    {
        if (Hidden || _fsHidden) return;
        var settings = _settings();
        var media = MediaBridge.Current;

        // 背景：默认完全透明，hover 时淡入半透明深色圆角 + 细边框
        float target = _hovered ? 1f : 0f;
        _bgAlpha += (target - _bgAlpha) * 0.28f;
        if (MathF.Abs(_bgAlpha - target) < 0.02f) _bgAlpha = target;
        // 分层窗口 alpha=0 的像素点击会穿透到系统任务栏——常驻一层 2/255 的隐形底，
        // 视觉不可见但保证整条（含模块间隙）都能接收鼠标（命中要求 alpha≥1）
        using (var hit = new SKPaint { IsAntialias = true, Color = new SKColor(22, 22, 26, 2) })
            canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, height / 2f, height / 2f, hit);
        if (_bgAlpha > 0.01f)
        {
            using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(22, 22, 26, (byte)(232 * _bgAlpha)) })
                canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, height / 2f, height / 2f, bg);
            using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, (byte)(28 * _bgAlpha)) })
                canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, height / 2f, height / 2f, border);
        }

        UpdateAnalyzer(settings, media);
        float[]? bars = null;
        if (_analyzer is not null && media is { IsPlaying: true })
        {
            try { bars = _analyzer.GetBars(); } // 当帧现读、当帧消费，绝不存字段
            catch { bars = null; }
        }

        _hits = TaskbarRenderer.Paint(canvas, width, height, settings, _monitor.Snapshot, media,
            _analyzer is not null, _hoverAction, bars, out float contentW);

        // 内容宽变化（媒体出现/消失、切歌、网速单位跳变）→ 下一轮消息循环里重排，避免在绘制中重建缓冲
        if (Hwnd != IntPtr.Zero && MathF.Abs(contentW - width) >= RelayoutThresholdPx && !_relayoutPending)
        {
            _relayoutPending = true;
            NativeMethods.PostMessage(Hwnd, NativeMethods.WM_APP_RELAYOUT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private void UpdateAnalyzer(TaskbarSettings settings, MediaBridge.MediaInfo? media)
    {
        bool want = media is not null && settings.ShowMedia && !_analyzerBroken;
        if (want && _analyzer is null)
        {
            try { _analyzer = new AudioAnalyzer(); } // 与宿主灵动岛同源的 public 类
            catch (Exception ex)
            {
                Logger.Warn("[PrivateTaskbar] AudioAnalyzer 创建失败，频谱降级隐藏: " + ex.Message);
                _analyzerBroken = true;
            }
        }
        else if (!want && _analyzer is not null)
        {
            try { _analyzer.Dispose(); } catch { }
            _analyzer = null;
        }
    }

    protected override void OnCleanup()
    {
        try { _analyzer?.Dispose(); } catch { }
        _analyzer = null;
    }

    /// <summary>改分辨率/系统缩放后复位：重取系统 DPI，Relayout 会因物理尺寸变化重建缓冲并重新定位。</summary>
    protected override void OnDpiOrDisplayChanged()
    {
        DpiScale100 = (int)NativeMethods.GetDpiForSystem();
        Relayout();
        Invalidate();
    }

    protected override void OnAppMessage(uint msg)
    {
        if (msg == NativeMethods.WM_APP_RELAYOUT)
        {
            _relayoutPending = false;
            Relayout();
        }
    }

    // ---- 全屏检测（与宿主灵动岛同款：SHQueryUserNotificationState，0.8s 节流探测）----
    // 全屏应用 z 序在本条之上，hover/右键全被盖住；此时随系统任务栏一起隐藏，退出全屏自动恢复。

    private DateTime _fsProbeAt = DateTime.MinValue;
    private bool _fsHidden;                    // 当前是否因全屏而隐藏（区别于 Hidden 的"用户关闭组件"）

    protected override void OnTimerTick()
    {
        var now = DateTime.UtcNow;
        if ((now - _fsProbeAt).TotalMilliseconds < 800) return;
        _fsProbeAt = now;

        bool fs;
        try
        {
            fs = NativeMethods.SHQueryUserNotificationState(out int st) == 0
                 && (st == NativeMethods.QUNS_BUSY
                  || st == NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
                  || st == NativeMethods.QUNS_PRESENTATION_MODE);
        }
        catch { fs = false; }

        if (fs == _fsHidden) return;
        _fsHidden = fs;
        if (Hidden) return; // 用户"关闭组件"优先：全屏状态只记账，窗口保持隐藏，插件重载（禁用→启用）后由新实例恢复
        if (fs)
        {
            _hovered = false; _hoverAction = null;
            // 降频到 800ms 而非停掉：这个 timer 是全屏探测唯一的驱动源，停了就永远检测不到退出全屏。
            // 降频后每跳恰好穿过 OnTimerTick 的 800ms 节流，也不再逐帧空转 Clear+ULW
            SetAnimInterval(800);
            NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
        }
        else
        {
            Relayout(); // 全屏期间分辨率可能变过
            SetAnimInterval(31); // 恢复动画帧率
            NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    // ---- 鼠标 ----

    protected override void OnMouseMove(float x, float y)
    {
        if (!_trackLeave && Hwnd != IntPtr.Zero)
        {
            var tme = new NativeMethods.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                dwFlags = NativeMethods.TME_LEAVE,
                hwndTrack = Hwnd,
            };
            if (NativeMethods.TrackMouseEvent(ref tme)) _trackLeave = true; // 离开窗口时系统投递 WM_MOUSELEAVE
        }
        if (!_hovered) { _hovered = true; Invalidate(); }
        string? act = null;
        foreach (var h in _hits)
            if (h.Rect.Contains(x, y)) { act = h.Action; break; }
        if (act != _hoverAction) { _hoverAction = act; Invalidate(); } // 命中变化才重绘
    }

    protected override void OnMouseLeave()
    {
        _trackLeave = false;
        if (_hovered || _hoverAction is not null)
        {
            _hovered = false;
            _hoverAction = null;
            Invalidate();
        }
    }

    protected override void OnMouseLeftDown(float x, float y)
    {
        foreach (var h in _hits)
            if (h.Rect.Contains(x, y)) { _hoverAction = h.Action; HitAction(h.Action); return; }
    }

    private void HitAction(string action) => MediaBridge.Invoke(action);

    protected override void OnMouseRightDown(float x, float y)
    {
        _hoverAction = null; // 菜单打开后按钮高亮不再由本窗口跟踪
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? MenuRequested;   // 入口类订阅：弹右键菜单

    // ---- 布局与定位 ----
    public void Relayout()
    {
        if (Hidden) return;
        // 1) 量内容宽（网速用当前 1s 快照，与绘制同源）
        var snap = _monitor.Snapshot;
        float contentW = TaskbarRenderer.MeasureContent(_settings(), MediaBridge.Current, _analyzer is not null,
            MathF.Max(0f, snap.NetUp), MathF.Max(0f, snap.NetDown));
        float w = contentW;
        // 2) 计算工作区（进程是 PMv2，Screen.WorkingArea 返回物理像素；只把逻辑量换算成物理，wa 不再乘 scale）
        var screen = System.Windows.Forms.Screen.PrimaryScreen;
        var wa = screen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var sb = screen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        float scale = DpiScale100 / 96f;
        // 系统任务栏带高度（底部）；条要嵌进任务栏带内而不是悬浮在它上方。
        // 任务栏自动隐藏/非底部时 taskbarH<=0，退回工作区底部上方悬浮。
        int taskbarH = sb.Bottom - wa.Bottom;
        float barH = taskbarH > (int)(10 * scale)
            ? Math.Min(BarHeight, taskbarH / scale - 2f)
            : BarHeight;
        int physW = (int)(w * scale), physH = (int)(barH * scale);
        int y = taskbarH > (int)(10 * scale)
            ? wa.Bottom + Math.Max(0, (taskbarH - physH) / 2)              // 嵌入任务栏带内垂直居中
            : wa.Bottom - physH - (int)(MarginBottom * scale);            // 无任务栏带：工作区底部上方
        // 「贴托盘」以系统托盘区（Shell_TrayWnd 的 TrayNotifyWnd：托盘图标+输入法+时钟+通知）实际左缘为基准，
        // 紧贴其左侧，浮在任务栏元素之上且不遮挡托盘；取不到时退回工作区右缘估算。
        int trayLeft = FindTrayNotifyLeft();
        int rightEdge = trayLeft > 0 ? trayLeft : wa.Right;
        int x = _settings().Position switch
        {
            TaskbarPosition.Start => wa.Left + (int)(MarginSide * scale),
            TaskbarPosition.End => rightEdge - physW - (int)(MarginSide * scale),
            _ => wa.Left + (wa.Width - physW) / 2,
        };
        x += (int)(_settings().OffsetX * scale);

        if (physW != _physW || physH != _physH) ResizeTo((int)w, (int)barH);
        RepositionTo(x, y);
    }

    /// <summary>系统托盘区（TrayNotifyWnd）的屏幕左缘物理坐标；找不到返回 -1。</summary>
    private static int FindTrayNotifyLeft()
    {
        try
        {
            var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero) return -1;
            var notify = NativeMethods.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify == IntPtr.Zero) return -1;
            return NativeMethods.GetWindowRect(notify, out var r) ? r.Left : -1;
        }
        catch { return -1; }
    }

    private void SetAnimInterval(int ms) => _animTimer = NativeMethods.SetTimer(Hwnd, IntPtr.Zero, (uint)ms, IntPtr.Zero);
    // 31ms ≈ 32FPS（系统定时器粒度 15.6ms，请求 16 也会取整到 31）；同 (hwnd,id) 的 SetTimer 是替换语义，重复调用不泄漏
    public void StartAnimation() => SetAnimInterval(31);
    public void StopAnimation()
    {
        if (_animTimer != IntPtr.Zero) { NativeMethods.KillTimer(Hwnd, _animTimer); _animTimer = IntPtr.Zero; }
    }

    public void HideBar()
    {
        Hidden = true;
        _hovered = false;
        _hoverAction = null;
        StopAnimation();
        if (Hwnd != IntPtr.Zero) NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
    }
}
