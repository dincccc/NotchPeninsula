using NotchPeninsula;
using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public sealed class PrivateTaskbarPlugin : INotchPlugin, IDisposable
{
    public string Id => "com.dincccc.privatetaskbar.taskbarwidget"; // 必须含 taskbarwidget 标记，宿主据此豁免灵动岛"显示内容"列表
    public string DisplayName => "定制任务栏";
    public string Version => "1.3.0";
    public string Author => "dincccc";

    private IPluginHost? _host;
    private SystemMonitor? _monitor;
    private Thread? _uiThread;
    private volatile TaskbarWindow? _window;
    private ContextMenuWindow? _menu;        // 仅 UI 线程触碰
    private volatile TaskbarSettings _settings = new();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = TaskbarSettings.Load(_host!);
        _monitor = new SystemMonitor();
        _monitor.Start();
        try { SubscribeFontChanged(); } catch { } // 旧宿主没有 FontConfig 时只让重排失效，其余功能照常

        var monitor = _monitor;
        // 窗口构造/订阅/赋值必须在宿主线程完成：Dispose 可能在线程刚启动、还没跑到 `_window = w` 时被调，
        // 那样 Dispose 看到的 _window 为 null 会整条清理链失效。先赋值再启动线程，UI 线程只跑 Show()；
        // CreateNativeWindow 开头的 Closing 检查再把竞态窗口收到零。
        var w = new TaskbarWindow(monitor, () => _settings);
        _window = w;
        w.MenuRequested += Window_MenuRequested;
        _uiThread = new Thread(() => UiThreadMain(w))
        { Name = "PrivateTaskbar.UI", IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    /// <summary>UI 线程主体：常驻消息循环。返回后若不是宿主 Dispose 发起的关闭（Closing 标志），
    /// 说明窗口被外部销毁——挂载方案的唯一来源是 explorer 重启（挂载父窗口死 → 子窗口连带销毁），
    /// 此时在本线程收尾，重建整条窗口链（窗口/线程全部换新，事件订阅在创建时接好）。</summary>
    private void UiThreadMain(TaskbarWindow w)
    {
        try
        {
            w.Show();
        }
        catch (Exception ex)
        {
            // IsBackground 线程未处理异常会终止整个宿主进程，必须兜住
            Logger.Error("[PrivateTaskbar] UI 线程异常", ex);
        }
        if (!w.Closing)
        {
            // 与上方 catch 对称：重建链任何一环抛异常都不能炸宿主（IsBackground 线程未处理异常终止进程）
            try { RebuildAfterWindowLost(w); }
            catch (Exception ex) { Logger.Error("[PrivateTaskbar] 窗口重建失败", ex); }
        }
    }

    // ---- 挂载窗口重建（explorer 重启自愈） ----

    private readonly object _lifecycleLock = new();
    private volatile bool _disposed;
    private int _rebuildCount;                                   // 10 分钟滑动窗内的重建次数
    private DateTime _rebuildWindowStart = DateTime.MinValue;

    private void RebuildAfterWindowLost(TaskbarWindow lost)
    {
        lock (_lifecycleLock)
        {
            // 旧窗口的消息循环已退出，其右键菜单若还开着会变成无人泵消息的僵尸窗口。必须放在
            // _disposed 检查之前：Dispose 竞态路径下 RunOnUi 的关菜单投递会因旧窗口已死被丢弃，
            // 这里的同步调用是唯一机会（CloseTree 幂等，两分支都安全）
            try { _menu?.CloseTree(); } catch { }
            _menu = null;
            if (_disposed) return; // 宿主正在 Teardown：绝不再起窗口（新实例由热重载链负责）
            var now = DateTime.UtcNow;
            if (_rebuildCount == 0 || (now - _rebuildWindowStart).TotalMinutes >= 10)
            {
                _rebuildWindowStart = now;
                _rebuildCount = 0;
            }
            if (++_rebuildCount > 5)
            {
                Logger.Error("[PrivateTaskbar] 10 分钟内窗口丢失超过 5 次，停止自动重建（查看上方日志定位根因）", null);
                return;
            }
            var w = new TaskbarWindow(_monitor!, () => _settings);
            w.Hidden = lost.Hidden; // 会话级关闭状态随重建延续，防"关闭组件"后 explorer 重启让条复活
            w.MenuRequested += Window_MenuRequested;
            _window = w;
            _uiThread = new Thread(() => UiThreadMain(w))
            { Name = "PrivateTaskbar.UI", IsBackground = true };
            _uiThread.SetApartmentState(ApartmentState.STA);
            _uiThread.Start();
            Logger.Info($"[PrivateTaskbar] 任务栏窗口已重建（第 {_rebuildCount} 次）");
        }
    }

    // ---- 右键菜单 ----

    private void Window_MenuRequested(object? sender, EventArgs e)
    {
        NativeMethods.GetCursorPos(out var pt);
        _menu?.CloseTree();
        var menu = new ContextMenuWindow(BuildMenu(_settings));
        menu.Closed += () =>
        {
            if (ReferenceEquals(_menu, menu)) _menu = null;
            // 菜单窗口 capture 期间任务栏收不到鼠标消息，关闭后其 hover 跟踪状态可能卡死
            //（用户复现：关菜单后 hover/右键失灵）；Closed 在 UI 线程触发，直接复位。
            try { _window?.ResetHover(); } catch { }
        };
        _menu = menu;
        if (!menu.PopupAt(pt.x, pt.y))
        {
            _menu = null; // 创建失败不会触发 Closed，就地复位防止 _menuOpen 永久冻结条的 hover
            try { _window?.ResetHover(); } catch { }
        }
    }

    private IReadOnlyList<MenuEntry> BuildMenu(TaskbarSettings st)
    {
        var media = MediaBridge.Current;
        bool hasMedia = media is not null; // 菜单打开瞬间的模式快照：时钟/资源/网速按此模式读写
        return new List<MenuEntry>
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
            // 时钟/资源/网速两模式各自记忆：勾选状态与切换目标都按当前模式路由（TaskbarSettings 的 *Media 键）
            new MenuLabel(hasMedia ? "有媒体模式 · 以下三项独立记忆" : "无媒体模式 · 以下三项独立记忆"),
            new MenuCheck("时间日期", hasMedia ? st.ShowClockMedia : st.ShowClock,
                () => Apply(s => hasMedia ? s with { ShowClockMedia = !s.ShowClockMedia } : s with { ShowClock = !s.ShowClock })),
            new MenuCheck("资源监控", hasMedia ? st.ShowResourcesMedia : st.ShowResources,
                () => Apply(s => hasMedia ? s with { ShowResourcesMedia = !s.ShowResourcesMedia } : s with { ShowResources = !s.ShowResources })),
            new MenuCheck("网速", hasMedia ? st.ShowNetMedia : st.ShowNet,
                () => Apply(s => hasMedia ? s with { ShowNetMedia = !s.ShowNetMedia } : s with { ShowNet = !s.ShowNet })),
            new MenuSeparator(),
            new MenuCommand("打开正在播放的应用", () => MediaBridge.Invoke("open-app")),
            new MenuCommand("关闭组件", HideBar),
        };
    }

    /// <summary>回写设置：持久化 + volatile 替换 + UI 线程重排（同线程时经消息队列下一轮执行）。</summary>
    private void Apply(Func<TaskbarSettings, TaskbarSettings> mutate)
    {
        var next = mutate(_settings);
        try { next.Save(_host!); }
        catch (Exception ex) { Logger.Warn("[PrivateTaskbar] 设置保存失败: " + ex.Message); }
        _settings = next;
        _window?.RunOnUi(() => _window?.Relayout());
    }

    /// <summary>"关闭组件"：会话级隐藏（不写设置）+ 灵动岛提醒；插件中心禁用→启用即恢复。</summary>
    private void HideBar()
    {
        _window?.RunOnUi(() => _window?.HideBar());
        try
        {
            _host?.PostReminder(new ReminderData
            {
                Title = "定制任务栏",
                Body = "任务栏已收起，插件中心禁用后启用可恢复",
            });
        }
        catch (Exception ex) { Logger.Warn("[PrivateTaskbar] 提醒投递失败: " + ex.Message); }
    }

    // ---- 字体变更 ----

    private void SubscribeFontChanged() => FontConfig.Changed += OnFontChanged;
    private void OnFontChanged() => _window?.RunOnUi(() => _window?.Relayout());

    public void Dispose()
    {
        // 锁内只做标志置位与引用快照：重建回调（旧 UI 线程）与 Dispose（宿主线程）互斥，
        // 保证"看到 _disposed=false 就重建"的判断与窗口/线程替换是原子的
        Thread? t;
        TaskbarWindow? w;
        lock (_lifecycleLock)
        {
            _disposed = true;
            w = _window;
            t = _uiThread;
        }
        try { FontConfig.Changed -= OnFontChanged; } catch { }
        if (w is not null)
        {
            // 先断工作再请求退出：Closing 让 UI 线程跳过后续 WM_TIMER 采样/重绘（AudioAnalyzer 创建、
            // GPU 计数器读数都可能卡 1s+），把「宿主 Teardown→Unload ALC」窗口期内旧代码的存活压到最短
            w.Closing = true;
            // 菜单窗口必须在其创建线程（UI 线程）销毁：经 RunOnUi 排进队列，串在 WM_CLOSE 之前执行
            w.RunOnUi(() => { try { w.StopAnimation(); } catch { } try { _menu?.CloseTree(); } catch { } });
            w.RequestQuit(); // PostMessage(WM_CLOSE)，RunMessageLoop 返回后线程自然结束
            if (!(t?.Join(3000) ?? true))
            {
                // 兜底：直接向 UI 线程投 WM_QUIT 掐掉消息循环（Cleanup→DestroyWindow 会注销窗口类）。
                // 唯一类名保证：即使线程彻底卡死、旧类残留，也只是泄漏一个内核类结构与一条线程，
                // 绝不会让「重新启用的新实例」挂到旧 ALC 的失效 WndProc 上（那才是宿主崩溃的根因）
                w.ForceQuitThread();
                if (!(t?.Join(1000) ?? true))
                    Logger.Error("[PrivateTaskbar] UI 线程未能退出（已投 WM_QUIT 兜底），窗口类可能残留", null);
            }
        }
        _monitor?.Dispose();
        // AudioAnalyzer 由 TaskbarWindow.OnCleanup 在窗口销毁时释放
    }
}
