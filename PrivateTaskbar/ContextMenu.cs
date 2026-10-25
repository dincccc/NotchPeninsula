using NotchPeninsula;
using SkiaSharp;

namespace PrivateTaskbar;

// ---- 菜单项模型 ----

public abstract record MenuEntry;
public sealed record MenuCommand(string Text, Action Run) : MenuEntry;
public sealed record MenuCheck(string Text, bool Checked, Action Toggle) : MenuEntry;
public sealed record MenuRadio(string Text, bool Checked, Action Select) : MenuEntry;
public sealed record MenuSeparator() : MenuEntry;
public sealed record MenuLabel(string Text) : MenuEntry; // 灰色小说明行（不可点、无 hover）
public sealed record MenuSubmenu(string Text, IReadOnlyList<MenuEntry> Children) : MenuEntry;

/// <summary>
/// 自绘上下文菜单窗口。与任务栏共用同一条 UI 线程但绝不跑独立消息循环（QuitLoopOnDestroy=false）：
/// 线程上 GetMessage(IntPtr.Zero) 的任务栏主循环会分发线程内所有窗口的消息。
/// 交互：SetCapture 后点击窗口外关闭整链；hover 到子菜单项在其右侧弹出子菜单窗口（capture 接力）。
/// </summary>
public sealed class ContextMenuWindow : SkiaWindow
{
    private const float RowH = 34f, SepH = 13f, PadV = 6f, MenuW = 256f, TextSize = 12.5f, LabelH = 24f;

    private readonly List<(float Y, float H, MenuEntry Entry)> _rows = new();
    private readonly ContextMenuWindow? _parent; // 子菜单指向上级；根为 null
    private ContextMenuWindow? _child;           // 当前展开的子菜单窗口
    private int _hoverRow = -1;
    private bool _closing;                       // ReleaseCapture 会同步触发 WM_CAPTURECHANGED，防 CloseChain 重入

    /// <summary>窗口销毁后回调（UI 线程）。</summary>
    public event Action? Closed;

    public ContextMenuWindow(IReadOnlyList<MenuEntry> entries, ContextMenuWindow? parent = null)
        : base("NPSPrivateTaskbarMenu", "NPS-PrivateTaskbar-Menu", (int)MenuW, ComputeHeight(entries))
    {
        _parent = parent;
        float y = PadV;
        foreach (var e in entries)
        {
            float rh = e is MenuSeparator ? SepH : e is MenuLabel ? LabelH : RowH;
            _rows.Add((y, rh, e));
            y += rh;
        }
    }

    protected override bool QuitLoopOnDestroy => false; // 绝不干扰任务栏主循环

    private static int ComputeHeight(IReadOnlyList<MenuEntry> entries)
    {
        float h = PadV * 2;
        foreach (var e in entries) h += e is MenuSeparator ? SepH : e is MenuLabel ? LabelH : RowH;
        return (int)MathF.Ceiling(h);
    }

    // ---- 弹出 / 关闭 ----

    /// <summary>在光标处弹出（光标右下方，放不下则翻转；上下以整屏为界，允许弹到任务栏上方）。
    /// 返回 false = 窗口创建失败（此路径不走 Closed，调用方须自行复位状态）。</summary>
    public bool PopupAt(int screenX, int screenY)
    {
        float scale = NativeMethods.GetDpiForSystem() / 96f;
        int wPhys = (int)(MenuW * scale), hPhys = (int)(_height * scale);
        // 用整屏边界而非工作区：条贴着屏幕底，按工作区夹会把高菜单推到屏幕中间（远离光标数百像素）
        var sb = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int x = screenX + (int)(4 * scale), y = screenY + (int)(4 * scale);
        if (x + wPhys > sb.Right) x = screenX - wPhys - (int)(4 * scale);
        if (y + hPhys > sb.Bottom) y = screenY - hPhys - (int)(4 * scale); // 光标上方翻转
        x = Math.Clamp(x, sb.Left, Math.Max(sb.Left, sb.Right - wPhys));
        y = Math.Clamp(y, sb.Top, Math.Max(sb.Top, sb.Bottom - hPhys));
        return CreateAt(x, y);
    }

    /// <summary>作为子菜单弹出：贴 parent 右侧、与 hover 行顶对齐，越界翻到左侧。返回 false = 创建失败。</summary>
    private bool PopupAsChild(ContextMenuWindow parent, float rowTopLogic)
    {
        float scale = NativeMethods.GetDpiForSystem() / 96f;
        int wPhys = (int)(MenuW * scale), hPhys = (int)(_height * scale);
        var sb = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        int x = parent._posX + parent._physW + (int)(2 * scale);
        if (x + wPhys > sb.Right) x = parent._posX - wPhys - (int)(2 * scale);
        int y = parent._posY + (int)(rowTopLogic * scale) - (int)(PadV * scale);
        if (y + hPhys > sb.Bottom) y = sb.Bottom - hPhys;
        x = Math.Clamp(x, sb.Left, Math.Max(sb.Left, sb.Right - wPhys));
        y = Math.Clamp(y, sb.Top, Math.Max(sb.Top, sb.Bottom - hPhys));
        return CreateAt(x, y);
    }

    /// <summary>创建窗口并显示（不进消息循环），随即抓取鼠标并启动点外轮询兜底。</summary>
    private bool CreateAt(int x, int y)
    {
        if (!CreateNativeWindow(x, y)) return false; // 失败路径不会触发 Closed，调用方负责复位关联状态
        NativeMethods.SetWindowPos(Hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOMOVE); // NOMOVE 必带：否则 (0,0) 把菜单甩到左上角
        NativeMethods.SetCapture(Hwnd);
        // 线程级 capture 对跨进程点击的 CAPTURECHANGED 时序不可靠：轮询兜底，左键按下且不在菜单链内即关
        NativeMethods.SetTimer(Hwnd, PollTimerId, 80, IntPtr.Zero);
        return true;
    }

    private static readonly IntPtr PollTimerId = (IntPtr)0x5151;

    protected override void OnTimerTick()
    {
        if (_closing) return;
        if ((NativeMethods.GetAsyncKeyState(0x01 /*VK_LBUTTON*/) & 0x8000) == 0) return;
        NativeMethods.GetCursorPos(out var pt);
        for (var w = this; w is not null; w = w._child)
            if (w.ContainsPt(pt.x, pt.y)) return;
        for (var p = _parent; p is not null; p = p._parent)
            if (p.ContainsPt(pt.x, pt.y)) return;
        CloseChain();
    }

    /// <summary>关闭自己及全部后代（不动父级）。</summary>
    public void CloseTree()
    {
        _child?.CloseTree();
        _child = null;
        CloseSelf();
    }

    /// <summary>沿父链找到根后整链关闭。</summary>
    public void CloseChain()
    {
        ContextMenuWindow root = this;
        while (root._parent is { } p) root = p;
        root.CloseTree();
    }

    private void CloseSelf()
    {
        if (_closing) return; // 关闭中重入（多半来自 WM_CAPTURECHANGED），直接跳过
        _closing = true;
        if (Hwnd != IntPtr.Zero) NativeMethods.KillTimer(Hwnd, PollTimerId);
        var parent = _parent;
        if (parent is not null && ReferenceEquals(parent._child, this))
            parent._child = null; // 清父的死引用：残留会让 OpenSubmenu 的防重入检查把之后的唤出全部拦死
        if (NativeMethods.GetCapture() == Hwnd)
        {
            NativeMethods.ReleaseCapture();
            parent?.Recapture(); // capture 接力回父级
        }
        if (Hwnd != IntPtr.Zero) NativeMethods.DestroyWindow(Hwnd); // WM_DESTROY 里 Unregister（不 PostQuit）
        Cleanup();
        Closed?.Invoke();
    }

    private void Recapture() => NativeMethods.SetCapture(Hwnd);

    // ---- 交互 ----

    protected override void OnMouseMove(float x, float y)
    {
        NativeMethods.GetCursorPos(out var pt);
        if (!ContainsPt(pt.x, pt.y))
        {
            // capture 在本窗口：光标回到父链菜单上时转交父级处理 hover（父更新高亮，移到
            // 非唤出行时关掉本子树）。绝不能直接 CloseSelf——子菜单刚弹出时光标多半还停在
            // 父级的唤出行上，旧逻辑一动就自杀，且父 _child 残留死引用会把之后的唤出全部拦死
            //（用户实测：二级菜单无法唤出）。
            for (var p = _parent; p is not null; p = p._parent)
            {
                if (!p.ContainsPt(pt.x, pt.y)) continue;
                if (_hoverRow != -1) { _hoverRow = -1; Invalidate(); } // 清自身高亮
                p.HandleHoverFromChild(pt);
                return;
            }
            return; // 纯出界不关（可能正在移向屏幕他处）；点外由 PollTimer 兜底
        }
        int row = HitRow(y);
        if (row != _hoverRow) { _hoverRow = row; Invalidate(); }
        if (row >= 0)
        {
            if (_rows[row].Entry is MenuSubmenu sub) OpenSubmenu(row, sub);
            else if (_child is not null) { var c = _child; _child = null; c.CloseTree(); }
        }
    }

    /// <summary>子菜单转交来的 hover（capture 在子菜单、光标回到本菜单）：更新本菜单高亮；
    /// 光标移到非子菜单唤出行时关掉子树，把 capture 收回来。</summary>
    private void HandleHoverFromChild(NativeMethods.POINT pt)
    {
        float y = (pt.y - _posY) * 96f / DpiScale100;
        int row = HitRow(y);
        if (row != _hoverRow) { _hoverRow = row; Invalidate(); }
        if (row >= 0 && _rows[row].Entry is MenuSubmenu) return; // 仍停在唤出行：子菜单保持
        if (_child is not null) { var c = _child; _child = null; c.CloseTree(); }
    }

    protected override void OnMouseLeftDown(float x, float y)
    {
        NativeMethods.GetCursorPos(out var pt);
        if (!ContainsPt(pt.x, pt.y))
        {
            if (IsCursorInsideAncestor()) { CloseSelf(); return; } // 点回父级：关闭自己，父级接手后续点击
            CloseChain();
            return;
        }
        int row = HitRow(y);
        if (row < 0) return;
        switch (_rows[row].Entry)
        {
            case MenuCommand c:
            {
                SafeRun(c.Run);
                CloseChain(); // 点命令项执行 → 整条父子链关闭
                break;
            }
            case MenuCheck c:
            {
                SafeRun(c.Toggle);
                CloseChain();
                break;
            }
            case MenuRadio r:
            {
                SafeRun(r.Select);
                CloseChain();
                break;
            }
            case MenuSubmenu sub:
                OpenSubmenu(row, sub);
                break;
        }
    }

    protected override void OnMouseRightDown(float x, float y)
    {
        NativeMethods.GetCursorPos(out var pt);
        if (!ContainsPt(pt.x, pt.y) && !IsCursorInsideAncestor()) CloseChain();
    }

    /// <summary>capture 被夺走：新捕获者是本链子菜单（SetCapture 接力）则放行；被外部程序抢走（Alt+Tab 等）则整链关闭，防菜单僵死屏幕。</summary>
    protected override void OnCaptureLost(IntPtr newCaptureHwnd)
    {
        if (_closing) return;
        for (var c = _child; c is not null; c = c._child)
            if (c.Hwnd == newCaptureHwnd) return;
        CloseChain();
    }

    private void OpenSubmenu(int row, MenuSubmenu sub)
    {
        if (_child is not null) return;
        var child = new ContextMenuWindow(sub.Children, this);
        if (!child.PopupAsChild(this, _rows[row].Y)) return; // 创建失败不登记：死引用会把后续唤出全部拦死
        _child = child;
    }

    private int HitRow(float y)
    {
        for (int i = 0; i < _rows.Count; i++)
            if (y >= _rows[i].Y && y < _rows[i].Y + _rows[i].H) return i;
        return -1;
    }

    private bool ContainsPt(int px, int py)
        => px >= _posX && px < _posX + _physW && py >= _posY && py < _posY + _physH;

    private bool IsCursorInsideAncestor()
    {
        NativeMethods.GetCursorPos(out var pt);
        for (var p = _parent; p is not null; p = p._parent)
            if (p.ContainsPt(pt.x, pt.y)) return true;
        return false;
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { Logger.Warn("[PrivateTaskbar] 菜单命令执行失败: " + ex.Message); }
    }

    // ---- 绘制 ----

    protected override void OnPaint(SKCanvas canvas, int width, int height)
    {
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(19, 19, 23, 250) })
            canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, 10f, 10f, bg);
        using (var border = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 24) })
            canvas.DrawRoundRect(0.5f, 0.5f, width - 1f, height - 1f, 10f, 10f, border);

        var tf = PluginFont.Resolve();
        for (int i = 0; i < _rows.Count; i++)
        {
            var (y, h, entry) = _rows[i];
            if (entry is MenuSeparator)
            {
                using var lp = new SKPaint { IsAntialias = true, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 22) };
                canvas.DrawLine(12f, y + h / 2f, width - 12f, y + h / 2f, lp);
                continue;
            }
            if (entry is MenuLabel lab)
            {
                using var lp = new SKPaint { IsAntialias = true, TextSize = 10.5f, Typeface = tf, Color = new SKColor(255, 255, 255, 145) };
                canvas.DrawText(lab.Text, 16f, y + h / 2f + 3.5f, lp);
                continue;
            }
            if (i == _hoverRow)
            {
                using var hp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 22) };
                canvas.DrawRoundRect(5f, y + 3f, width - 10f, h - 6f, 7f, 7f, hp);
            }

            bool hasMark = entry is MenuCheck or MenuRadio;
            string text = entry switch
            {
                MenuCommand c => c.Text,
                MenuCheck c => c.Text,
                MenuRadio r => r.Text,
                MenuSubmenu s => s.Text,
                _ => "",
            };
            using (var p = new SKPaint { IsAntialias = true, TextSize = TextSize, Typeface = tf, Color = new SKColor(255, 255, 255, 243) })
                canvas.DrawText(text, hasMark ? 36f : 16f, y + h / 2f + 4.5f, p);

            float markCy = y + h / 2f;
            if (entry is MenuCheck { Checked: true }) DrawCheck(canvas, 23f, markCy);
            if (entry is MenuRadio { Checked: true }) DrawRadioDot(canvas, 23f, markCy);
            if (entry is MenuSubmenu) DrawSubmenuArrow(canvas, width - 16f, markCy);
        }
    }

    /// <summary>✔ 矢量自绘（两段折线）。</summary>
    private static void DrawCheck(SKCanvas canvas, float cx, float cy)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(255, 255, 255, 243),
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
        };
        canvas.DrawLine(cx - 4f, cy, cx - 1.2f, cy + 3.2f, p);
        canvas.DrawLine(cx - 1.2f, cy + 3.2f, cx + 4.2f, cy - 3.4f, p);
    }

    /// <summary>● 单选标记（实心圆点）。</summary>
    private static void DrawRadioDot(SKCanvas canvas, float cx, float cy)
    {
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 243) };
        canvas.DrawCircle(cx, cy, 3.4f, p);
    }

    /// <summary>▶ 子菜单指示（实心右三角）。</summary>
    private static void DrawSubmenuArrow(SKCanvas canvas, float x, float cy)
    {
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 180) };
        using var tri = new SKPath();
        tri.MoveTo(x, cy - 4.5f);
        tri.LineTo(x, cy + 4.5f);
        tri.LineTo(x + 5.5f, cy);
        tri.Close();
        canvas.DrawPath(tri, p);
    }
}
