using System.Runtime.InteropServices;
using NotchPeninsula;
using SkiaSharp;

namespace PrivateTaskbar;

/// <summary>任务栏主窗口：SetParent 挂进系统任务栏的胶囊条。专用 UI 线程上 Show()。</summary>
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
    // 动画帧驱动 timer 的显式非零 id：SetTimer 传 nIDEvent=0 时系统分配 id，其 Kill/替换行为文档未定义
    // （实测 KillTimer 对分配 id 杀不掉，StopAnimation 会失效）。显式 id 的 Kill/替换语义跨版本确定，
    // 与 ContextMenuWindow.PollTimerId 同款做法。
    private static readonly IntPtr AnimTimerId = (IntPtr)0x1990;
    private List<(SKRect Rect, string Action)> _hits = new();

    // hover 状态（仅 UI 线程触碰）
    private bool _hovered;
    private bool _trackLeave;
    private bool _menuOpen;                  // 右键菜单打开中：capture 转移会触发一次 WM_MOUSELEAVE，hover 视觉冻结到菜单关闭
    private string? _hoverAction;
    private float _bgAlpha;                  // 背景 0..1，每帧向目标 Lerp（约 150ms 淡入淡出）

    // 频谱源：媒体激活时创建、失活时释放（仅 UI 线程触碰）
    private AudioAnalyzer? _analyzer;
    private bool _analyzerBroken;            // 构造失败后本会话不再尝试，频谱整块隐藏

    // ---- 任务栏挂载 ----
    // 学市场插件 NpsTaskbarWidget（反编译实证）：SetParent 挂进系统任务栏（Shell_TrayWnd）成子窗口，
    // 条的可见性与 z 序完全跟随系统任务栏——全屏/自动隐藏等行为由 Windows 全权处理，本插件不再有
    // 任何主动 z 序动作。此前的"全屏沉底让位"方案（FullscreenMonitor 前台覆盖判据 + HWND_BOTTOM/
    // TOPMOST 切换 + EnsureTopmost 每帧保活）在窗口切换时反复重排 z 序造成闪烁，用户实测推翻。
    // 子窗口 + WS_EX_LAYERED 的 UpdateLayeredWindow 在 Win10/11 可用（NpsTaskbarWidget 同款技术栈实证）。
    private IntPtr _taskbarHwnd;             // 挂载父窗口（仅 UI 线程触碰）
    private const int AnimIntervalMs = 15;   // 64FPS（见 StartAnimation 处注释）

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

    /// <summary>窗口创建 + 缓冲就绪 + 首帧绘制完成后回调：先挂进系统任务栏（子窗口坐标换算依赖
    /// 挂载父窗口），再布局、启动动画帧，画好首帧再显示。</summary>
    protected override void OnWindowCreated()
    {
        AttachToTaskbar();
        Relayout();
        StartAnimation();
        // 屏幕外创建→挂载+Relayout 已就位后才显示；必须带 NOMOVE，否则这一句会把刚就位的窗口甩回 (0,0)
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
            | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    /// <summary>SetParent 挂进系统任务栏（WS_POPUP→WS_CHILD 前置样式，NpsTaskbarWidget 同款）。
    /// 任务栏未起（宿主早于 explorer）或挂载失败（管理员运行被 UIPI 拦截）时保持独立顶层窗口，
    /// HealthCheck 会周期重试挂载。</summary>
    private void AttachToTaskbar()
    {
        if (_taskbarHwnd != IntPtr.Zero && NativeMethods.IsWindow(_taskbarHwnd)) return;
        var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return;
        int style = NativeMethods.GetWindowLong(Hwnd, NativeMethods.GWL_STYLE);
        NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_STYLE, (style & 0x7FFFFFFF) | NativeMethods.WS_CHILD);
        if (NativeMethods.SetParent(Hwnd, tray) == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            NativeMethods.SetWindowLong(Hwnd, NativeMethods.GWL_STYLE, style); // 还原样式，避免悬在半改状态
            Logger.Warn($"[PrivateTaskbar] SetParent(Shell_TrayWnd) 失败: Win32 {err}（管理员运行会被 UIPI 拦截），保持独立窗口，巡检会重试");
            return;
        }
        _taskbarHwnd = tray;
        Logger.Info($"[PrivateTaskbar] 已挂载到系统任务栏 tray=0x{tray.ToInt64():X}");
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
    // 挂载方案下的自愈只剩两件事：任务栏没挂上/挂载父窗口失效时补挂重试；条被意外隐藏/位置漂移
    // （explorer 重启、分辨率变更、外部软件乱动窗口等）时拉回。z 序保活（旧版 EnsureTopmost）随
    // 沉底让位方案一并删除——子窗口的 z 序归父窗口管，没有任何主动竞争，也就不存在"被压"。

    private DateTime _healthAt = DateTime.MinValue; // 巡检节流

    protected override void OnTimerTick()
    {
        HealthCheck(DateTime.UtcNow);    // 2s 节流巡检
    }

    /// <summary>自愈巡检（2s 一次）：补挂 + 条被意外隐藏/位置漂移时拉回，兜住一切"条莫名消失"。
    /// 窗口本体死亡（explorer 重启 → 挂载父窗口死 → 子窗口连带销毁）不在这里检测——WM_DESTROY
    /// 会在本线程触发 PostQuitMessage，消息循环返回后由入口类按 Closing 标志重建。</summary>
    private void HealthCheck(DateTime now)
    {
        if (Hidden) return;
        if ((now - _healthAt).TotalMilliseconds < 2000) return;
        _healthAt = now;

        // 挂载成子窗口后收不到 WM_DISPLAYCHANGE（该广播只发顶层窗口），DPI/缩放变更后由巡检代偿：
        // 比对系统 DPI，不一致就走既有回调重取重排——否则旧 DPI 算出的错值自洽，巡检永不发现
        int dpi = (int)NativeMethods.GetDpiForSystem();
        if (dpi != DpiScale100) { OnDpiOrDisplayChanged(); return; }

        // 补挂：首次挂载时任务栏未起，或挂载父窗口被重建（explorer 重启后子窗口必死，此处主要兜
        // "SetParent 曾失败"的场景）
        if ((_taskbarHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_taskbarHwnd))
            && Hwnd != IntPtr.Zero && NativeMethods.IsWindow(Hwnd))
        {
            _taskbarHwnd = IntPtr.Zero;
            AttachToTaskbar();
            if (_taskbarHwnd != IntPtr.Zero) { Relayout(); return; }
        }

        bool visible = Hwnd != IntPtr.Zero && NativeMethods.IsWindowVisible(Hwnd);
        bool misplaced = false;
        int l = 0, t = 0, rr = 0, b = 0, orgX = 0, orgY = 0;
        if (visible && NativeMethods.GetWindowRect(Hwnd, out var r))
        {
            l = r.Left; t = r.Top; rr = r.Right; b = r.Bottom;
            // GetWindowRect 恒返回屏幕坐标；挂载态 _posX/_posY 存父客户区坐标，比较前先平移回来
            if (_taskbarHwnd != IntPtr.Zero)
            {
                var org = new NativeMethods.POINT(0, 0);
                if (NativeMethods.ClientToScreen(_taskbarHwnd, ref org)) { orgX = org.x; orgY = org.y; }
            }
            misplaced = Math.Abs(l - orgX - _posX) > 8 || Math.Abs(t - orgY - _posY) > 8;
        }
        if (!visible || misplaced)
        {
            Logger.Info($"[PrivateTaskbar] 自愈巡检触发: visible={visible} misplaced={misplaced} rect=({l},{t})-({rr},{b}) 期望=({_posX},{_posY}) 客户区原点=({orgX},{orgY})");
            Relayout();
            NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
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
        if (_menuOpen) return; // 菜单打开期间保持 hover（用户要求）：条已收不到鼠标消息，状态冻结即可
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
        _menuOpen = true; // hover 状态（含按钮高亮）原样冻结，菜单关闭后由 ResetHover 按光标位置重算
        MenuRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? MenuRequested;   // 入口类订阅：弹右键菜单

    // ---- 布局与定位 ----
    public void Relayout()
    {
        if (Hidden) return; // 用户已关组件：窗口已藏且 timer 已停，设置/字体变更等重排无意义
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

        // 挂载态：子窗口的 SetWindowPos/ULW 位置都是父客户区坐标，把上面算好的屏幕坐标平移换算。
        // 未挂载（Attach 失败重试中）保持屏幕坐标语义，两套坐标在 RepositionTo/ULW 里同源一致。
        if (_taskbarHwnd != IntPtr.Zero && NativeMethods.IsWindow(_taskbarHwnd))
        {
            var org = new NativeMethods.POINT(0, 0);
            if (NativeMethods.ClientToScreen(_taskbarHwnd, ref org)) { x -= org.x; y -= org.y; }
        }

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
    public void StartAnimation()
    {
        if (Hwnd == IntPtr.Zero) return;
        // 动画帧驱动 timer 的显式非零 id：SetTimer 传 nIDEvent=0 时系统分配 id，其 Kill 行为文档未定义
        // （实测 KillTimer 对分配 id 杀不掉）。显式 id 的 Kill/替换语义跨版本确定，与 ContextMenuWindow.PollTimerId 同款。
        NativeMethods.KillTimer(Hwnd, AnimTimerId);
        NativeMethods.SetTimer(Hwnd, AnimTimerId, (uint)AnimIntervalMs, IntPtr.Zero);
    }

    public void StopAnimation()
    {
        if (Hwnd != IntPtr.Zero) NativeMethods.KillTimer(Hwnd, AnimTimerId);
    }

    /// <summary>整组复位 hover 跟踪：右键菜单的 capture 期间本窗口收不到鼠标消息，若光标关闭菜单后
    /// 仍悬在条上，静止的鼠标不会再产生 WM_MOUSEMOVE，不复位就永远没有高亮（用户实测复现）。
    /// 光标仍在胶囊区内时直接恢复 hover 并重新注册 LEAVE 跟踪（窗口已恒定加宽，必须按胶囊区判断，
    /// 透明穿透区不算）。</summary>
    public void ResetHover()
    {
        _menuOpen = false;
        _hovered = false;
        _hoverAction = null;
        _trackLeave = false;
        // 不硬置 _bgAlpha=0：菜单打开期间背景已冻结在全亮，硬置会让关菜单瞬间背景跳变。
        // 交给 OnPaint 的 lerp 自然过渡——光标在条上保持全亮，不在则 150ms 淡出。
        try
        {
            float scale = DpiScale100 / 96f;
            float capX = _settings().Position switch
            {
                TaskbarPosition.Start => 0f,
                TaskbarPosition.Center => (FixedWinW - _curW) / 2f,
                _ => FixedWinW - _curW,
            };
            // 条左缘以 GetWindowRect 为准（屏幕坐标）：挂载态 _posX 存父客户区坐标，不能与屏幕光标混算
            if (Hwnd != IntPtr.Zero && NativeMethods.GetCursorPos(out var pt)
                && NativeMethods.GetWindowRect(Hwnd, out var r))
            {
                int capL = r.Left + (int)(capX * scale);
                int capR = capL + (int)(_curW * scale);
                if (pt.x >= capL && pt.x < capR && pt.y >= r.Top && pt.y < r.Bottom)
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
        }
        catch { }
        Invalidate();
    }

    public void HideBar()
    {
        Hidden = true;   // 用户级关闭（会话级，不写设置）
        ResetHover();
        StopAnimation();
        if (Hwnd != IntPtr.Zero) NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_HIDEWINDOW);
    }
}
