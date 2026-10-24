using NotchPeninsula;
using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public sealed class PrivateTaskbarPlugin : INotchPlugin, IDisposable
{
    public string Id => "com.dincccc.privatetaskbar.taskbarwidget"; // 必须含 taskbarwidget 标记，宿主据此豁免灵动岛"显示内容"列表
    public string DisplayName => "定制任务栏";
    public string Version => "1.0.0";
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
        _uiThread = new Thread(() =>
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
        })
        { Name = "PrivateTaskbar.UI", IsBackground = true };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
    }

    // ---- 右键菜单 ----

    private void Window_MenuRequested(object? sender, EventArgs e)
    {
        NativeMethods.GetCursorPos(out var pt);
        _menu?.CloseTree();
        var menu = new ContextMenuWindow(BuildMenu(_settings));
        menu.Closed += () => { if (ReferenceEquals(_menu, menu)) _menu = null; };
        _menu = menu;
        menu.PopupAt(pt.x, pt.y);
    }

    private IReadOnlyList<MenuEntry> BuildMenu(TaskbarSettings st)
    {
        var media = MediaBridge.Current;
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
            new MenuCheck("时间日期", st.ShowClock, () => Apply(s => s with { ShowClock = !s.ShowClock })),
            new MenuCheck("资源监控", st.ShowResources, () => Apply(s => s with { ShowResources = !s.ShowResources })),
            new MenuCheck("网速", st.ShowNet, () => Apply(s => s with { ShowNet = !s.ShowNet })),
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
        try { FontConfig.Changed -= OnFontChanged; } catch { }
        var w = _window;
        if (w is not null)
        {
            // 先断工作再请求退出：Closing 让 UI 线程跳过后续 WM_TIMER 采样/重绘（AudioAnalyzer 创建、
            // GPU 计数器读数都可能卡 1s+），把「宿主 Teardown→Unload ALC」窗口期内旧代码的存活压到最短
            w.Closing = true;
            // 菜单窗口必须在其创建线程（UI 线程）销毁：经 RunOnUi 排进队列，串在 WM_CLOSE 之前执行
            w.RunOnUi(() => { try { w.StopAnimation(); } catch { } try { _menu?.CloseTree(); } catch { } });
            w.RequestQuit(); // PostMessage(WM_CLOSE)，RunMessageLoop 返回后线程自然结束
            if (!(_uiThread?.Join(3000) ?? true))
            {
                // 兜底：直接向 UI 线程投 WM_QUIT 掐掉消息循环（Cleanup→DestroyWindow 会注销窗口类）。
                // 唯一类名保证：即使线程彻底卡死、旧类残留，也只是泄漏一个内核类结构与一条线程，
                // 绝不会让「重新启用的新实例」挂到旧 ALC 的失效 WndProc 上（那才是宿主崩溃的根因）
                w.ForceQuitThread();
                if (!(_uiThread?.Join(1000) ?? true))
                    Logger.Error("[PrivateTaskbar] UI 线程未能退出（已投 WM_QUIT 兜底），窗口类可能残留", null);
            }
        }
        _monitor?.Dispose();
        // AudioAnalyzer 由 TaskbarWindow.OnCleanup 在窗口销毁时释放
    }
}
