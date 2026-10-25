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
    // 窗口类按类名记录引用计数：RegisterClass 是进程全局的，插件 ALC 可被卸载（热重载），
    // 销毁时归零即 UnregisterClass，否则重载后注册同名类必得 1410、CreateWindowEx 落到旧类的失效 WndProc
    private static readonly Dictionary<string, int> _registeredClasses = new();
    private static readonly object _classLock = new();

    protected IntPtr Hwnd;
    protected int DpiScale100;   // 系统 DPI（96/120/144…），/96f 即缩放系数
    protected int _width, _height;         // 逻辑像素
    protected int _physW, _physH, _posX, _posY; // 物理像素
    private IntPtr _memDc, _dib, _dibBits, _oldObj;
    private SKSurface? _surface;
    private readonly string _className;
    private readonly string _title;
    private readonly Queue<Action> _uiQueue = new(); // UI 线程执行的投递队列
    private readonly object _queueLock = new();
    private int _threadId;                   // UI 线程的 Win32 线程 ID（ForceQuitThread 用）

    /// <summary>关闭中标志：置 true 后 UI 线程不再采样/重绘，加速 Dispose 退出。
    /// 插件层在 Dispose 一开始就设它，缩短宿主 Teardown→Unload 窗口期内旧代码的存活时间。</summary>
    public volatile bool Closing;

    protected SkiaWindow(string className, string title, int width, int height)
    {
        // 每实例唯一类名：宿主热重载（禁用→启用）时旧窗口类可能尚未注销（旧 UI 线程还没退完），
        // 若沿用同名旧类，新窗口会绑到「旧 ALC 的 WndProc」上——旧线程随后注销类、ALC 完成卸载，
        // 新窗口的下一条消息就跳进已释放代码，宿主直接崩溃。GUID 后缀让每次注册都是全新的类。
        _className = className + "_" + Guid.NewGuid().ToString("N");
        _title = title;
        _width = width; _height = height;
    }

    // ---- 子类契约 ----
    protected abstract void OnPaint(SKCanvas canvas, int width, int height);
    protected virtual void OnMouseLeftDown(float x, float y) { }
    protected virtual void OnMouseLeftUp(float x, float y) { }
    protected virtual void OnMouseMove(float x, float y) { }
    protected virtual void OnMouseRightDown(float x, float y) { }
    protected virtual void OnMouseLeave() { } // TrackMouseEvent(TME_LEAVE) 触发：光标离开窗口
    /// <summary>窗口创建、缓冲就绪、首帧绘制完成后回调一次；此时 DpiScale100 已初始化，可安全做首次布局。</summary>
    protected virtual void OnWindowCreated() { }
    /// <summary>窗口销毁时释放子类持有的原生资源（如 AudioAnalyzer）。</summary>
    protected virtual void OnCleanup() { }
    /// <summary>WM_DESTROY 时是否 PostQuitMessage 结束本线程消息循环。任务栏窗口为 true；
    /// 菜单窗口必须为 false —— 它与任务栏同线程，任务栏主循环 GetMessage(IntPtr.Zero) 会分发线程内所有窗口的消息，
    /// 菜单销毁绝不能把整条线程的消息循环关掉。</summary>
    protected virtual bool QuitLoopOnDestroy => true;

    /// <summary>在 UI 线程上安全创建窗口并进入消息循环；返回即窗口已销毁。
    /// try/finally 保证创建后的任何异常路径都会 Cleanup（幂等），不留窗口/缓冲/窗口类。</summary>
    public void RunMessageLoop(int x, int y)
    {
        try
        {
            if (!CreateNativeWindow(x, y)) return;

            // 消息循环（必须在创建线程上跑）
            int gm;
            while ((gm = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0)) > 0)
            {
                if (msg.message == NativeMethods.WM_QUIT) break;
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
            }
            if (gm == -1)
                NotchPeninsula.Logger.Warn($"[PrivateTaskbar] GetMessage 返回 -1（错误），消息循环异常退出: Win32 {Marshal.GetLastWin32Error()}");
        }
        finally { Cleanup(); }
    }

    /// <summary>创建窗口、初始化缓冲并画好首帧（不进消息循环）。菜单窗口的 Create 用；返回 false = 创建失败。</summary>
    protected bool CreateNativeWindow(int x, int y)
    {
        if (Closing) return false; // Dispose 已开始而窗口尚未创建：直接放弃，UI 线程随即退出（封死 Initialize/Dispose 竞态）

        _threadId = NativeMethods.GetCurrentThreadId();
        DpiScale100 = (int)NativeMethods.GetDpiForSystem(); // 96 => 1.0x，实际返回 96/120/144…
        float scale = DpiScale100 / 96f;
        _physW = (int)(_width * scale); _physH = (int)(_height * scale);

        var hInst = NativeMethods.GetModuleHandle(string.Empty);
        EnsureClassRegistered(hInst);

        Hwnd = NativeMethods.CreateWindowEx(
            NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_NOACTIVATE,
            _className, _title,
            NativeMethods.WS_POPUP, // 不加 VISIBLE：首帧画好后再显示，避免闪烁
            x, y, _physW, _physH, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (Hwnd == IntPtr.Zero)
        {
            int createErr = Marshal.GetLastWin32Error();
            ReleaseClass(); // 创建失败没有 WM_DESTROY，补偿引用计数，防类永不注销
            NotchPeninsula.Logger.Error($"[PrivateTaskbar] CreateWindowEx({_className}) 失败: Win32 错误 {createErr}", null);
            return false;
        }
        _posX = x; _posY = y;
        // CreateWindowEx 期间的 WM_NCCREATE/WM_CREATE 走 DefWindowProc，从这句起消息才进 HandleMessage
        SkiaHostBridge.Register(Hwnd, this);
        NotchPeninsula.Logger.Info($"[PrivateTaskbar] 窗口已创建 {_className} hwnd=0x{Hwnd.ToString("X")} {_physW}x{_physH} @({x},{y})");

        InitBuffer();
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE); // 置顶；必须带 NOMOVE，否则 x/y=(0,0) 会把窗口移到左上角
        PaintFrame();

        OnWindowCreated();
        return true;
    }

    private void EnsureClassRegistered(IntPtr hInst)
    {
        lock (_classLock)
        {
            if (_registeredClasses.TryGetValue(_className, out var refs)) { _registeredClasses[_className] = refs + 1; return; }
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = WndProcStub,
                hInstance = hInst,
                lpszClassName = _className,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_ARROW),
            };
            ushort atom = NativeMethods.RegisterClass(ref wc);
            if (atom != 0)
                _registeredClasses[_className] = 1;
            else
            {
                // 类名带 GUID，理论上不可能重名；注册失败只记日志，绝不沿用残留旧类
                // （旧类的 WndProc 属于可能已被卸载的旧 ALC，挂上去就是崩溃定时炸弹）
                NotchPeninsula.Logger.Warn($"[PrivateTaskbar] RegisterClass({_className}) 失败: {Marshal.GetLastWin32Error()}");
            }
        }
    }

    /// <summary>引用计数归零即注销窗口类。必须在 DestroyWindow 返回（窗口彻底消失）之后调用——
    /// WM_DESTROY 时机上窗口还在销毁序列内，UnregisterClass 必然失败（1412）。幂等：重复调用安全。</summary>
    private void ReleaseClass()
    {
        bool last;
        lock (_classLock)
        {
            if (!_registeredClasses.TryGetValue(_className, out var refs)) return;
            last = refs <= 1;
            if (last) _registeredClasses.Remove(_className);
            else _registeredClasses[_className] = refs - 1;
        }
        if (last)
        {
            // 归零时本类已无存活窗口（菜单先于任务栏销毁，Dispose 顺序保证），锁外注销避免持锁调 P/Invoke
            try { NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(string.Empty)); }
            catch { }
        }
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
                if (Closing) return IntPtr.Zero; // 关闭中不再采样/重绘
                OnTimerTick();
                PaintFrame();
                return IntPtr.Zero;
            case NativeMethods.WM_DISPLAYCHANGE:
                OnDpiOrDisplayChanged();
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSEMOVE:
                OnMouseMove(Lo(lParam) * 96f / DpiScale100, Hi(lParam) * 96f / DpiScale100);
                return IntPtr.Zero;
            case NativeMethods.WM_MOUSELEAVE:
                OnMouseLeave();
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
            case NativeMethods.WM_CAPTURECHANGED:
                OnCaptureLost(lParam);
                return IntPtr.Zero;
            case NativeMethods.WM_APP_FRAME:
                PaintFrame();
                return IntPtr.Zero;
            case (uint)(NativeMethods.WM_APP + 0x40): // RunOnUi 投递唤醒
                DrainQueue();
                return IntPtr.Zero;
            case NativeMethods.WM_CLOSE:
                NativeMethods.DestroyWindow(Hwnd);
                return IntPtr.Zero;
            case NativeMethods.WM_DESTROY:
                SkiaHostBridge.Unregister(Hwnd);
                Hwnd = IntPtr.Zero;
                // 此处不能 UnregisterClass：窗口仍在销毁序列内，UnregisterClass 必得 1412（ERROR_CLASS_HAS_WINDOWS）。
                // 注销统一挪到 Cleanup（DestroyWindow 返回、窗口彻底消失之后）才真正生效。
                if (QuitLoopOnDestroy) NativeMethods.PostQuitMessage(0); // 让 GetMessage 返回 0，RunMessageLoop 才能返回
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(Hwnd, msg, wParam, lParam);
    }

    private void DrainQueue()
    {
        while (true)
        {
            Action action;
            lock (_queueLock)
            {
                if (_uiQueue.Count == 0) return;
                action = _uiQueue.Dequeue();
            }
            try { action(); }
            catch (Exception ex) { NotchPeninsula.Logger.Error("[PrivateTaskbar] UI 投递执行异常", ex); }
        }
    }

    protected virtual void OnDpiOrDisplayChanged() { }
    protected virtual void OnTimerTick() { } // WM_TIMER：子类先处理，再统一重绘
    /// <summary>鼠标捕获被夺走（lParam=新捕获者 HWND，可能为 0）。菜单窗口用来在 capture 被外部程序抢走时关闭自己。</summary>
    protected virtual void OnCaptureLost(IntPtr newCaptureHwnd) { }

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
        if (Closing || Hwnd == IntPtr.Zero || _surface is null) return;
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

    /// <summary>改窗口尺寸（逻辑像素）：重建缓冲 + SetWindowPos。必须带 NOMOVE——
    /// 缺了它每次改宽度窗口都会被甩回 (0,0)，等下一句 RepositionTo 才拉回来，左上角闪一帧。</summary>
    protected void ResizeTo(int width, int height)
    {
        _width = Math.Max(1, width); _height = Math.Max(1, height);
        float scale = DpiScale100 / 96f;
        _physW = (int)(_width * scale); _physH = (int)(_height * scale);
        InitBuffer();
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, 0, 0, _physW, _physH,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>移动窗口（屏幕物理坐标）。</summary>
    protected void RepositionTo(int x, int y)
    {
        _posX = x; _posY = y;
        NativeMethods.SetWindowPos(Hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        PaintFrame(); // ULW 需要重述位置
    }

    /// <summary>任意线程投递到 UI 线程执行（经消息队列唤醒）。窗口已销毁时直接丢弃，
    /// 否则 PostMessage(NULL) 会把消息投到调用线程（宿主线程）自己的队列里。</summary>
    public void RunOnUi(Action action)
    {
        if (Hwnd == IntPtr.Zero) return;
        lock (_queueLock) _uiQueue.Enqueue(action);
        NativeMethods.PostMessage(Hwnd, (uint)(NativeMethods.WM_APP + 0x40), IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>兜底强退：直接向 UI 线程投 WM_QUIT 掐掉消息循环（Dispose 等 Join 超时后调用）。
    /// 循环退出路径里 Cleanup→DestroyWindow 会正常注销窗口类。</summary>
    public void ForceQuitThread()
    {
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    private static short Lo(IntPtr lParam) => (short)(lParam.ToInt64() & 0xFFFF);
    private static short Hi(IntPtr lParam) => (short)((lParam.ToInt64() >> 16) & 0xFFFF);

    protected void Cleanup()
    {
        CleanupBuffer();
        OnCleanup();
        if (Hwnd != IntPtr.Zero) { NativeMethods.DestroyWindow(Hwnd); Hwnd = IntPtr.Zero; }
        // DestroyWindow 已返回、窗口彻底消失，此刻 UnregisterClass 才能成功；
        // 覆盖全部退出路径：消息循环正常退出 / ForceQuitThread 后 / 菜单 CloseSelf
        ReleaseClass();
    }

    /// <summary>只能在窗口所属的 UI 线程调用（跨线程 DestroyWindow 会失败）。宿主 Teardown 走
    /// Closing→RequestQuit→Join 的退出链，不会走到这里；预留幂等收尾语义。</summary>
    public virtual void Dispose() => Cleanup();
}
