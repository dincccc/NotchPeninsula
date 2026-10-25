using System.Runtime.InteropServices;
using NotchPeninsula;
using SkiaSharp;

namespace PrivateTaskbar;

/// <summary>任务栏主窗口：贴工作区底部的胶囊条。专用 UI 线程上 Show()。</summary>
public sealed class TaskbarWindow : SkiaWindow
{
    private const float BarHeight = 52f;      // 逻辑像素
    private const float MarginBottom = 6f, MarginSide = 8f;
    // 窗口恒定逻辑宽（宿主同款架构，NotchWindow 用 WINDOW_WIDTH 恒定窗口 + 岛体在窗口内动画）：
    // 宽度变化全在绘制层做弹簧动画，窗口绝不因内容 resize（resize 会重建 DIB 且引起 DWM 重排，
    // 这就是旧版"宽度变化生硬"的原因）。900 覆盖全控件场景（媒体+时钟+资源+网速 ≈830）。
    private const float FixedWinW = 900f;

    // 宽度弹簧动画（参数照搬宿主 NotchWindow：2.65Hz 阻尼弹簧，0.45s，带一次轻回弹的"Q 弹"手感）
    private const double SpringFrequency = 2.65, SpringDecay = 10.8, SpringDurationSeconds = 0.450;
    private float _curW;                     // 当前显示宽（动画值）
    private float _startW, _targetW;
    private DateTime _animStart;
    private bool _animating;

    private readonly SystemMonitor _monitor;
    private readonly Func<TaskbarSettings> _settings;
    public volatile bool Hidden;             // 菜单"关闭组件"置 true（会话级）
    private IntPtr _animTimer;               // 15ms ≈ 64FPS 动画帧驱动
    private List<(SKRect Rect, string Action)> _hits = new();

    // hover 状态（仅 UI 线程触碰）
    private bool _hovered;
    private bool _trackLeave;
    private string? _hoverAction;
    private float _bgAlpha;                  // 背景 0..1，每帧向目标 Lerp（约 150ms 淡入淡出）

    // 频谱源：媒体激活时创建、失活时释放（仅 UI 线程触碰）
    private AudioAnalyzer? _analyzer;
    private bool _analyzerBroken;            // 构造失败后本会话不再尝试，频谱整块隐藏

    public TaskbarWindow(SystemMonitor monitor, Func<TaskbarSettings> settings)
        : base("NPSPrivateTaskbar", "NPS-PrivateTaskbar", (int)FixedWinW, (int)BarHeight)
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
        if (Hidden) return;
        var settings = _settings();
        var media = MediaBridge.Current;

        UpdateAnalyzer(settings, media);
        float[]? bars = null;
        if (_analyzer is not null && media is { IsPlaying: true })
        {
            try { bars = _analyzer.GetBars(); } // 当帧现读、当帧消费，绝不存字段
            catch { bars = null; }
        }

        // 目标宽 = 本帧布局宽（夹到窗口内）；网速数字 1~2px 的每秒微动不值得弹，≥2px 才起弹簧
        var snap = _monitor.Snapshot;
        float targetW = MathF.Min(TaskbarRenderer.MeasureContent(settings, media, _analyzer is not null,
            MathF.Max(0f, snap.NetUp), MathF.Max(0f, snap.NetDown)), width - 2f);
        AnimTickWidth(targetW);

        // 胶囊在固定窗口内的对齐：贴托盘=右对齐（右缘不动、左缘伸缩）、开头=左对齐、居中=居中
        float capX = settings.Position switch
        {
            TaskbarPosition.Start => 0f,
            TaskbarPosition.Center => (width - _curW) / 2f,
            _ => width - _curW,
        };

        // 背景：默认完全透明，hover 时淡入半透明深色圆角 + 细边框（只画胶囊区）
        float target = _hovered ? 1f : 0f;
        _bgAlpha += (target - _bgAlpha) * 0.28f;
        if (MathF.Abs(_bgAlpha - target) < 0.02f) _bgAlpha = target;
        // 分层窗口 alpha=0 的像素点击会穿透到系统任务栏——胶囊区内常驻一层 2/255 的隐形底，
        // 视觉不可见但保证整条（含模块间隙）都能接收鼠标（命中要求 alpha≥1）；胶囊外保持纯透明穿透
        using (var hit = new SKPaint { IsAntialias = true, Color = new SKColor(22, 22, 26, 2) })
            canvas.DrawRoundRect(capX + 0.5f, 0.5f, _curW - 1f, height - 1f, height / 2f, height / 2f, hit);
        if (_bgAlpha > 0.01f)
        {
            using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(22, 22, 26, (byte)(238 * _bgAlpha)) })
                canvas.DrawRoundRect(capX + 0.5f, 0.5f, _curW - 1f, height - 1f, height / 2f, height / 2f, bg);
            using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, (byte)(30 * _bgAlpha)) })
                canvas.DrawRoundRect(capX + 0.5f, 0.5f, _curW - 1f, height - 1f, height / 2f, height / 2f, border);
        }

        // 内容画进胶囊区：平移对齐 + 裁剪到当前动画宽度（弹簧回弹时过冲部分不越界）
        canvas.Save();
        canvas.ClipRect(new SKRect(capX, 0f, capX + _curW, height), SKClipOperation.Intersect, true);
        _hits = TaskbarRenderer.Paint(canvas, height, capX, settings, snap, media,
            _analyzer is not null, _hoverAction, bars, out _);
        canvas.Restore();
    }

    /// <summary>宽度弹簧动画（宿主 NotchWindow 同款：目标变化记起点，SpringEase 逐帧插值，到时长 snap）。</summary>
    private void AnimTickWidth(float targetW)
    {
        if (MathF.Abs(targetW - _targetW) > 0.1f)
        {
            _startW = _curW;
            _targetW = targetW;
            _animating = MathF.Abs(targetW - _startW) >= 2f; // 微变（网速数字抖动）直接贴合不弹
            _animStart = DateTime.UtcNow;
            if (!_animating) _curW = targetW;
        }
        if (!_animating) return;
        double t = (DateTime.UtcNow - _animStart).TotalSeconds;
        if (t >= SpringDurationSeconds)
        {
            _curW = _targetW;
            _animating = false;
        }
        else
        {
            _curW = _startW + (_targetW - _startW) * (float)SpringEase(t);
        }
    }

    private static double SpringEase(double t)
        => 1.0 - Math.Cos(SpringFrequency * t * 2.0 * Math.PI) * Math.Exp(-SpringDecay * t);

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

    // ---- 自愈巡检 ----
    // 第五轮设计定调（用户拍板）：条是纯显示层，恒 TOPMOST 恒显示，不做任何"自动隐藏"的聪明逻辑
    // ——QUNS 全屏判断在真实环境里只会带来消失/闪烁（专注助手滞留 BUSY、SMTC 会话抖动），
    // 全屏应用盖住条就是自然的遮挡，不需要我们配合。唯一保留的自愈：可见性/位置巡检。

    private DateTime _healthAt = DateTime.MinValue; // 巡检节流

    protected override void OnTimerTick()
    {
        EnsureTopmost();               // 每帧廉价检查（纳秒级，被压才 SetWindowPos）
        HealthCheck(DateTime.UtcNow);  // 2s 节流巡检
    }

    /// <summary>自愈巡检（2s 一次，仅正常显示态）：条被意外隐藏/位置漂移
    /// （explorer 重启、任务栏重组、外部软件乱动窗口等）时拉回，兜住一切"条莫名消失"。
    /// z 序被压不在这里处理（被压时窗口仍可见、位置正确，巡检查不出来）——由 EnsureTopmost 每帧负责。</summary>
    private void HealthCheck(DateTime now)
    {
        if (Hidden) return;
        if ((now - _healthAt).TotalMilliseconds < 2000) return;
        _healthAt = now;

        bool visible = Hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(Hwnd);
        bool misplaced = false;
        int l = 0, t = 0, rr = 0, b = 0;
        if (visible && NativeMethods.GetWindowRect(Hwnd, out var r))
        {
            l = r.Left; t = r.Top; rr = r.Right; b = r.Bottom;
            misplaced = Math.Abs(r.Left - _posX) > 8 || Math.Abs(r.Top - _posY) > 8;
        }
        if (!visible || misplaced)
        {
            Logger.Info($"[PrivateTaskbar] 自愈巡检触发: visible={visible} misplaced={misplaced} rect=({l},{t})-({rr},{b}) 期望=({_posX},{_posY})");
            Relayout();
            NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }
    }

    /// <summary>置顶保活（宿主 EnsureTopmostAlive 的检测升级版）：条嵌在任务栏带内、与任务栏同处
    /// TOPMOST 组，其他窗口开/关/激活、右键菜单窗口销毁等都会重排 TOPMOST 链把条压到任务栏之下
    /// ——条此刻仍"可见、位置正确"，巡检查不出来，旧版盲提 2s 一次导致"消失/闪烁一段时间"才恢复。
    /// 现在每帧做廉价检查（被压才 SetWindowPos+留证），恢复延迟压到一帧。</summary>
    private void EnsureTopmost()
    {
        if (Hwnd == IntPtr.Zero) return;
        try
        {
            IntPtr foreground = NativeMethods.GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != Hwnd)
            {
                _ = NativeMethods.GetWindowThreadProcessId(foreground, out uint pid);
                if (pid == (uint)Environment.ProcessId) return; // 前台是宿主自己的窗口时不抢 z 序
            }
            if (IsAboveTaskbar()) return;
            Logger.Info("[PrivateTaskbar] 条被压到系统任务栏之下，提回 TOPMOST 顶部");
            NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOACTIVATE);
        }
        catch { }
    }

    /// <summary>条是否有效压在系统任务栏之上。判据是「条 vs 任务栏」的相对 z 序，而不是
    /// 「条是否在 TOPMOST 组绝对顶部」——IME 候选窗、开始菜单等系统 UI 合法待在 TOPMOST 组顶
    /// （不盖条的位置），把它们当压制者会造成每帧提回的无限拉锯（压力测试实测：与 IME 每 16ms 互刷）。</summary>
    private bool IsAboveTaskbar()
    {
        if ((NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_EXSTYLE) & NativeMethods.WS_EX_TOPMOST) == 0)
            return false; // TOPMOST 样式位丢了（第三方工具清理窗口样式等），视为被压
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || taskbar == Hwnd) return true; // 拿不到任务栏就不折腾
        // 从条沿 z 序向上走：任务栏在条之下时永远遇不到它（健康）；先遇到任务栏即被压。
        // 32 步上限只是防御（正常 TOPMOST 组远没有这么深）。
        IntPtr w = NativeMethods.GetWindow(Hwnd, NativeMethods.GW_HWNDPREV);
        for (int i = 0; w != IntPtr.Zero && i < 32; i++)
        {
            if (w == taskbar) return false;
            w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDPREV);
        }
        return true;
    }

    /// <summary>绕过节流立即检查（右键菜单关闭等已知重排点主动补救）。仅 UI 线程调用。</summary>
    public void EnsureTopmostNow() => EnsureTopmost();

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
        // 1) 量内容宽（网速用当前 1s 快照，与绘制同源）：只用于首帧初始化动画值与定位，窗口宽度恒定
        var snap = _monitor.Snapshot;
        float contentW = TaskbarRenderer.MeasureContent(_settings(), MediaBridge.Current, _analyzer is not null,
            MathF.Max(0f, snap.NetUp), MathF.Max(0f, snap.NetDown));
        if (_targetW <= 0f)
            _curW = _targetW = MathF.Min(contentW, FixedWinW); // 首次定位（窗口创建）不动画
        float w = FixedWinW;
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
        // 窗口宽恒定，胶囊在窗口内对齐（OnPaint 的 capX）：三种位置模式胶囊的可见缘都与旧版一致。
        int trayLeft = FindTrayNotifyLeft();
        int rightEdge = trayLeft > 0 ? trayLeft : wa.Right;
        int x = _settings().Position switch
        {
            TaskbarPosition.Start => wa.Left + (int)(MarginSide * scale),
            TaskbarPosition.End => rightEdge - physW - (int)(MarginSide * scale),
            _ => wa.Left + (wa.Width - physW) / 2,
        };
        x += (int)(_settings().OffsetX * scale);

        if (physW != _physW || physH != _physH) ResizeTo((int)FixedWinW, (int)barH);
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

    // 15ms → 实际 15.6ms ≈ 64FPS：旧值 31ms（≈32FPS）是频谱/歌词扫光不如宿主流畅的主因
    // （宿主岛体默认 90fps；LiquidGlass 帧率档 internal 不可读，user timer 粒度 15.6ms 已是上限）。
    // 恒定帧率，不做状态调频
    public void StartAnimation() => _animTimer = NativeMethods.SetTimer(Hwnd, IntPtr.Zero, 15, IntPtr.Zero);
    public void StopAnimation()
    {
        if (_animTimer != IntPtr.Zero) { NativeMethods.KillTimer(Hwnd, _animTimer); _animTimer = IntPtr.Zero; }
    }

    /// <summary>整组复位 hover 跟踪：右键菜单的 capture 期间本窗口收不到鼠标消息，若光标关闭菜单后
    /// 仍悬在条上，静止的鼠标不会再产生 WM_MOUSEMOVE，不复位就永远没有高亮（用户实测复现）。
    /// 光标仍在胶囊区内时直接恢复 hover 并重新注册 LEAVE 跟踪（窗口已恒定加宽，必须按胶囊区判断，
    /// 透明穿透区不算）。</summary>
    public void ResetHover()
    {
        _hovered = false;
        _hoverAction = null;
        _trackLeave = false;
        _bgAlpha = 0f;
        try
        {
            float scale = DpiScale100 / 96f;
            float capX = _settings().Position switch
            {
                TaskbarPosition.Start => 0f,
                TaskbarPosition.Center => (FixedWinW - _curW) / 2f,
                _ => FixedWinW - _curW,
            };
            int capL = _posX + (int)(capX * scale);
            int capR = capL + (int)(_curW * scale);
            if (Hwnd != IntPtr.Zero && NativeMethods.GetCursorPos(out var pt)
                && NativeMethods.GetWindowRect(Hwnd, out var r)
                && pt.x >= capL && pt.x < capR && pt.y >= r.Top && pt.y < r.Bottom)
            {
                _hovered = true;
                var tme = new NativeMethods.TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                    dwFlags = NativeMethods.TME_LEAVE,
                    hwndTrack = Hwnd,
                };
                if (NativeMethods.TrackMouseEvent(ref tme)) _trackLeave = true;
            }
        }
        catch { }
        Invalidate();
    }

    public void HideBar()
    {
        Hidden = true;
        ResetHover();
        StopAnimation();
        if (Hwnd != IntPtr.Zero) NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
    }
}
