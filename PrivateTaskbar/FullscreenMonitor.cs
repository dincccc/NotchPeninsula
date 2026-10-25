namespace PrivateTaskbar;

/// <summary>
/// 全屏前台窗口检测：单判据「前台窗口矩形覆盖主屏」（排除 shell 窗口与自身进程），带迟滞去抖。
/// 旧版 QUNS 方案在真实环境的两个缺陷——专注助手滞留 BUSY 导致条永不恢复、SMTC 会话重建
/// 抖动导致闪烁——根因是 QUNS 是「意图」信号，会被系统通知状态污染；本类只用「事实」信号：
/// 前台窗口是否真的盖住了主屏（独占 D3D 全屏的前台窗口矩形同样是整屏，事实判据全场景覆盖）。
/// 迟滞：进入全屏一次确认即生效（让位要快），退出需连续两次确认（吸收瞬时抖动，提回 ≈0.5~1s）。
/// 仅在任务栏 UI 线程被 Tick 驱动（OnTimerTick / OnWindowCreated），无需加锁。
/// </summary>
internal sealed class FullscreenMonitor
{
    // 250ms：进入全屏最坏 0.25s 隐藏（接近立即；0.8s 曾被用户实测抱怨"停一下才藏"）。
    // 与隐藏期 timer（TaskbarWindow.FsHiddenIntervalMs=250）同周期，恢复迟滞 2 连拍 ≈0.5~1s。
    // 探测本身只有 4 个 Win32 调用（微秒级），正常态 15ms 帧驱动下每 16 拍执行一次，成本可忽略。
    private const double ProbeIntervalMs = 250;
    private const int RestoreConfirmTicks = 2;   // 退出迟滞：连续 2 次非全屏才恢复
    private const int CoverSlackPx = 2;          // 覆盖判定容差（无边框窗口常有 1px 报数偏差）

    private DateTime _probeAt = DateTime.MinValue;
    private int _restoreStreak;

    /// <summary>迟滞后的判定。初始 false；只被 Tick 写（仅 UI 线程）。</summary>
    public bool IsFullscreen { get; private set; }

    /// <summary>最近一次探测采样的前台窗口类名（含被排除的 shell/自身窗口），供隐藏日志留证：
    /// 用户报「条不见了」时可立即区分「判定正确但不合期望」与「判定错误」，定位到具体窗口。</summary>
    public string? LastForegroundClass { get; private set; }

    /// <summary>按节流周期探测一次。返回 true = 本次实际执行了探测（_probeAt 初值 MinValue 与
    /// now 的差恒大于周期，首拍自然成立）。</summary>
    public bool Tick(DateTime now)
    {
        if ((now - _probeAt).TotalMilliseconds < ProbeIntervalMs)
            return false;
        _probeAt = now;

        if (ForegroundCoversScreen())
        {
            _restoreStreak = 0;
            IsFullscreen = true;
        }
        else if (IsFullscreen && ++_restoreStreak >= RestoreConfirmTicks)
        {
            _restoreStreak = 0;
            IsFullscreen = false;
        }
        return true;
    }

    /// <summary>前台窗口是否盖住主屏（条的所在屏；条恒在主屏任务栏带，Relayout 同源取 PrimaryScreen）。
    /// Win+D/桌面（Progman/WorkerW）、系统任务栏、宿主/插件自身窗口不算；带边框最大化只盖工作区、
    /// 盖不满整屏，天然不误报。任何失败都按非全屏处理（保守侧：宁可不藏，绝不闪烁）。</summary>
    private bool ForegroundCoversScreen()
    {
        try
        {
            IntPtr fg = NativeMethods.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            string cls = NativeMethods.GetClassNameSafe(fg);
            LastForegroundClass = cls;
            if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            _ = NativeMethods.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == (uint)Environment.ProcessId) return false; // 宿主岛体/托盘菜单等自身窗口
            if (!NativeMethods.GetWindowRect(fg, out var r)) return false;
            // 进程是 PMv2，Screen.Bounds 与 GetWindowRect 都返回物理像素，可直接比较
            var screen = System.Windows.Forms.Screen.PrimaryScreen;
            if (screen is null) return false;
            var sb = screen.Bounds;
            return r.Left <= sb.Left + CoverSlackPx && r.Top <= sb.Top + CoverSlackPx
                && r.Right >= sb.Right - CoverSlackPx && r.Bottom >= sb.Bottom - CoverSlackPx;
        }
        catch { return false; }
    }
}
