using NotchPeninsula;
using SkiaSharp;
using MC = NotchPeninsula.MediaController;

namespace PrivateTaskbar;

/// <summary>
/// 任务栏纯绘制（无窗口依赖）。布局（左→右）：媒体控件区（封面 36 → 两行文字 → 三按钮 → 5 柱频谱）
/// → 1px 分隔线 → 监控组（时间日期/CPU/RAM/GPU/网速）。
/// 测量与绘制共用 BuildLayout，保证量画一致；平滑值与封面缓存是仅 UI 线程触碰的静态状态。
/// </summary>
internal static class TaskbarRenderer
{
    public const float PadX = 16f, Gap = 18f, DividerGap = 14f;
    private const float CoverSize = 36f;
    private const float TitleMaxW = 160f;      // 上行歌名/下行歌词的截断上限
    private const float BtnSize = 22f, BtnGap = 6f;
    private const float SpectrumW = 21.2f;     // 5 柱 × 2px + 4 间隔 × 2.8px（与灵动岛同规格）
    private const float ShellW = 24f;          // 空壳内容宽（总宽 56 = PadX*2 + 24）

    private enum ModKind { Cover, MediaText, Buttons, Spectrum, Divider, Clock, Cpu, Ram, Gpu, Net }

    // 平滑状态（_s += (target-_s)*0.2f；仅 UI/渲染线程触碰，不跨线程）。
    // 网速不平滑：直接显示 1 秒快照值，每秒更新一次（用户要求），避免数字逐帧跳动。
    private static float _sCpu, _sRam, _sGpu;

    // 封面缓存：_thumbSrcKey 只做引用比较的失效键（宿主位图绝不缓存绝不 Dispose），_thumbSmall 是自建副本
    private static SKBitmap? _thumbSmall;
    private static SKBitmap? _thumbSrcKey;

    // ---- 布局（测量与绘制共用） ----

    private static bool HasMedia(TaskbarSettings s, MediaBridge.MediaInfo? m) => m is not null && s.ShowMedia;

    /// <summary>各模块（含分隔线）的宽与右侧间距；最后一个条目的间距无效。</summary>
    private static List<(ModKind Kind, float W, float GapAfter)> BuildLayout(
        TaskbarSettings s, MediaBridge.MediaInfo? media, bool spectrum, float netUp, float netDown)
    {
        var list = new List<(ModKind Kind, float W, float GapAfter)>();
        if (HasMedia(s, media))
        {
            list.Add((ModKind.Cover, CoverSize, 10f));
            list.Add((ModKind.MediaText, MeasureMediaText(s, media!), 12f));
            list.Add((ModKind.Buttons, BtnSize * 3 + BtnGap * 2, spectrum ? 10f : 0f));
            if (spectrum) list.Add((ModKind.Spectrum, SpectrumW, 0f));
        }
        if (s.ShowClock || s.ShowResources || s.ShowNet)
        {
            if (HasMedia(s, media)) list.Add((ModKind.Divider, 1f, DividerGap * 2 - 1f));
            if (s.ShowClock) list.Add((ModKind.Clock, MeasureClockW(), Gap));
            if (s.ShowResources)
            {
                // 「资源监控」一个控件 = CPU + RAM + GPU 三段一起显示/隐藏
                list.Add((ModKind.Cpu, MeasureMonitorW("CPU"), Gap));
                list.Add((ModKind.Ram, MeasureMonitorW("RAM"), Gap));
                list.Add((ModKind.Gpu, MeasureMonitorW("GPU"), Gap));
            }
            if (s.ShowNet) list.Add((ModKind.Net, MeasureNetW(netUp, netDown), 0f));
        }
        if (list.Count > 0)
        {
            var last = list[^1];
            list[^1] = (last.Kind, last.W, 0f);
        }
        return list;
    }

    private static float SumLayout(List<(ModKind Kind, float W, float GapAfter)> layout)
    {
        if (layout.Count == 0) return PadX * 2 + ShellW; // 空壳模式：固定小宽度
        float w = PadX * 2;
        foreach (var (_, mw, gap) in layout) w += mw + gap;
        return w;
    }

    /// <summary>重排时量内容宽。spectrum = 频谱区是否计入（AudioAnalyzer 已就绪）。</summary>
    public static float MeasureContent(TaskbarSettings s, MediaBridge.MediaInfo? media, bool spectrum, float netUp, float netDown)
        => SumLayout(BuildLayout(s, media, spectrum, netUp, netDown));

    /// <summary>
    /// 画一帧并返回 hit 区。bars 为 AudioAnalyzer.GetBars() 的当帧引用（仅本帧消费，绝不存字段）；
    /// hoverAction 是 TaskbarWindow 跟踪的悬停按钮，用于画高亮。
    /// </summary>
    public static List<(SKRect Rect, string Action)> Paint(
        SKCanvas canvas, int width, int height,
        TaskbarSettings settings, MonitorSnapshot snapshot,
        MediaBridge.MediaInfo? media, bool spectrum, string? hoverAction, float[]? bars, out float contentW)
    {
        SmoothTo(snapshot);
        var hits = new List<(SKRect, string)>();
        var layout = BuildLayout(settings, media, spectrum, MathF.Max(0f, snapshot.NetUp), MathF.Max(0f, snapshot.NetDown));
        contentW = SumLayout(layout);
        if (layout.Count == 0) return hits; // 空壳：不画内容，hover 背景由窗口层画

        float cy = height / 2f;
        float x = PadX;
        foreach (var (kind, w, gapAfter) in layout)
        {
            switch (kind)
            {
                case ModKind.Cover:
                {
                    float top = cy - CoverSize / 2f;
                    DrawCover(canvas, x, top, media!);
                    hits.Add((new SKRect(x, top, x + CoverSize, top + CoverSize), "open-app"));
                    break;
                }
                case ModKind.MediaText:
                {
                    DrawMediaText(canvas, x, cy, settings, media!);
                    hits.Add((new SKRect(x, cy - 15f, x + w, cy + 15f), "open-app"));
                    break;
                }
                case ModKind.Buttons:
                    DrawButtons(canvas, x, cy, media!.IsPlaying, hoverAction, hits);
                    break;
                case ModKind.Spectrum:
                    DrawSpectrum(canvas, x, cy, bars);
                    break;
                case ModKind.Divider:
                {
                    using var lp = new SKPaint { IsAntialias = true, StrokeWidth = 1f, Color = new SKColor(255, 255, 255, 36) };
                    canvas.DrawLine(x + 0.5f, cy - 12f, x + 0.5f, cy + 12f, lp);
                    break;
                }
                case ModKind.Clock:
                    DrawClock(canvas, x, cy);
                    break;
                case ModKind.Cpu:
                    DrawMonitor(canvas, x, cy, "CPU", PctText(_sCpu));
                    break;
                case ModKind.Ram:
                    DrawMonitor(canvas, x, cy, "RAM", PctText(_sRam));
                    break;
                case ModKind.Gpu:
                    DrawMonitor(canvas, x, cy, "GPU", snapshot.Gpu is null ? "--" : PctText(_sGpu));
                    break;
                case ModKind.Net:
                    DrawNet(canvas, x, cy, MathF.Max(0f, snapshot.NetUp), MathF.Max(0f, snapshot.NetDown));
                    break;
            }
            x += w + gapAfter;
        }
        return hits;
    }

    private static void SmoothTo(MonitorSnapshot s)
    {
        _sCpu += (s.Cpu - _sCpu) * 0.2f;
        _sRam += (s.Ram - _sRam) * 0.2f;
        if (s.Gpu is { } g) _sGpu += (g - _sGpu) * 0.2f;
    }

    private static string PctText(float v) => $"{Math.Clamp((int)MathF.Round(v), 0, 100)}%";

    private static string FormatSpeed(float bytesPerSec) =>
        bytesPerSec >= (1 << 30) ? $"{bytesPerSec / (1 << 30):0.0}GB/s"
        : bytesPerSec >= (1 << 20) ? $"{bytesPerSec / (1 << 20):0.0}MB/s"
        : bytesPerSec >= (1 << 10) ? $"{bytesPerSec / (1 << 10):0.0}KB/s"
        : $"{(int)MathF.Max(0f, bytesPerSec)}B/s";

    /// <summary>超宽截断加 …（与调用方共用同一 paint 实例，保证测量与绘制一致）。</summary>
    private static string ClampText(SKPaint p, string text, float maxW)
    {
        if (p.MeasureText(text) <= maxW) return text;
        while (text.Length > 1 && p.MeasureText(text + "…") > maxW) text = text[..^1];
        return text + "…";
    }

    // ---- 媒体区 ----

    /// <summary>上行 = 歌名（12px）+ " - " + 歌手（10px）；歌名超宽 160 截断，剩余宽度给歌手。
    /// titleBold = 无歌词的单行模式：歌名用粗体（测量与绘制必须用同一字体，否则 Bold 更宽会溢出截断宽度）。</summary>
    private static (float UpW, string Title, float TitleW, string Artist, float ArtistW) LayoutTitleLine(MediaBridge.MediaInfo media, bool titleBold)
    {
        var tf = titleBold ? PluginFont.ResolveBold() : PluginFont.Resolve();
        using var pT = new SKPaint { IsAntialias = true, TextSize = 12f, Typeface = tf };
        using var pA = new SKPaint { IsAntialias = true, TextSize = 10f, Typeface = tf };
        const string sep = " - ";
        float sepW = pA.MeasureText(sep);

        string title = media.Title;
        float titleW = pT.MeasureText(title);
        if (titleW > TitleMaxW)
        {
            title = ClampText(pT, title, TitleMaxW);
            titleW = pT.MeasureText(title);
        }

        string artist = media.Artist;
        float remain = TitleMaxW - titleW - sepW;
        float artistW = 0f;
        if (remain >= 24f) // 剩余太窄就不画歌手
        {
            artistW = pA.MeasureText(artist);
            if (artistW > remain)
            {
                artist = ClampText(pA, artist, remain);
                artistW = pA.MeasureText(artist);
            }
        }
        return (titleW + (artistW > 0f ? sepW + artistW : 0f), title, titleW, artist, artistW);
    }

    /// <summary>下行 = 歌词当前句（「歌词」开关关闭时留空，布局不变）。</summary>
    private static (float W, string Text) LayoutLyric(TaskbarSettings s, MediaBridge.MediaInfo media)
    {
        if (!s.ShowLyric || string.IsNullOrEmpty(media.Lyric)) return (0f, "");
        using var p = new SKPaint { IsAntialias = true, TextSize = 11f, Typeface = PluginFont.Resolve() };
        string t = media.Lyric;
        float w = p.MeasureText(t);
        if (w > TitleMaxW)
        {
            t = ClampText(p, t, TitleMaxW);
            w = p.MeasureText(t);
        }
        return (w, t);
    }

    private static float MeasureMediaText(TaskbarSettings s, MediaBridge.MediaInfo media)
    {
        bool single = string.IsNullOrEmpty(media.Lyric); // 只有真无歌词才单行；「歌词」开关关闭=两行布局、下行留空（需求 2.1.2 布局不变）
        var (upW, _, _, _, _) = LayoutTitleLine(media, single);
        var (lyW, _) = LayoutLyric(s, media);
        return MathF.Max(upW, lyW);
    }

    private static void DrawMediaText(SKCanvas canvas, float x, float cy, TaskbarSettings s, MediaBridge.MediaInfo media)
    {
        var (lyW, lyT) = LayoutLyric(s, media);
        bool single = string.IsNullOrEmpty(media.Lyric); // 与 MeasureMediaText 同判据；开关关闭时 lyW=0 自然不画歌词行
        var up = LayoutTitleLine(media, single);
        if (single)
        {
            using (var p = new SKPaint { IsAntialias = true, TextSize = 12f, Typeface = PluginFont.ResolveBold(), Color = new SKColor(255, 255, 255, 240) })
                canvas.DrawText(up.Title, x, cy + 4.5f, p);
            if (up.ArtistW > 0f)
            {
                using var pA = new SKPaint { IsAntialias = true, TextSize = 10f, Typeface = PluginFont.Resolve(), Color = new SKColor(255, 255, 255, 150) };
                canvas.DrawText(" - " + up.Artist, x + up.TitleW, cy + 4.5f, pA);
            }
            return;
        }
        var tf = PluginFont.Resolve();
        using (var p = new SKPaint { IsAntialias = true, TextSize = 12f, Typeface = tf, Color = new SKColor(255, 255, 255, 235) })
            canvas.DrawText(up.Title, x, cy - 4f, p);
        if (up.ArtistW > 0f)
        {
            using var pA = new SKPaint { IsAntialias = true, TextSize = 10f, Typeface = tf, Color = new SKColor(255, 255, 255, 140) };
            canvas.DrawText(" - " + up.Artist, x + up.TitleW, cy - 4f, pA);
        }
        if (lyW > 0f)
        {
            using var pL = new SKPaint { IsAntialias = true, TextSize = 11f, Typeface = tf, Color = new SKColor(255, 255, 255, 199) };
            canvas.DrawText(lyT, x, cy + 10.5f, pL);
        }
    }

    private static void EnsureThumb(MediaBridge.MediaInfo media)
    {
        var src = media.Thumb;
        if (src is null || (ReferenceEquals(src, _thumbSrcKey) && _thumbSmall is not null)) return;
        _thumbSmall?.Dispose();
        _thumbSmall = null;
        _thumbSrcKey = null;
        SKBitmap? dst = null;
        try
        {
            dst = new SKBitmap((int)CoverSize, (int)CoverSize, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var c = new SKCanvas(dst);
            c.Clear(SKColors.Transparent);
            float scale = MathF.Max(CoverSize / src.Width, CoverSize / src.Height); // cover 裁剪填满
            float w = src.Width * scale, h = src.Height * scale;
            using var p = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
            c.DrawBitmap(src, new SKRect((CoverSize - w) / 2f, (CoverSize - h) / 2f, (CoverSize + w) / 2f, (CoverSize + h) / 2f), p);
            _thumbSmall = dst;
            _thumbSrcKey = src; // 换歌（引用变化）时才重建副本
            dst = null;         // 归属已移交，异常路径不再释放
        }
        catch (Exception ex)
        {
            Logger.Warn("[PrivateTaskbar] 封面缩放失败: " + ex.Message);
            dst?.Dispose();
        }
    }

    private static void DrawCover(SKCanvas canvas, float x, float y, MediaBridge.MediaInfo media)
    {
        var rect = new SKRect(x, y, x + CoverSize, y + CoverSize);
        using var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 26) };
        canvas.DrawRoundRect(rect, 7f, 7f, bg); // 无封面时的占位底
        EnsureThumb(media);
        if (_thumbSmall is null) return;
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(rect, 7f), SKClipOperation.Intersect, true);
        using var bp = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
        canvas.DrawBitmap(_thumbSmall, rect, bp);
        canvas.Restore();
    }

    private static void DrawButtons(SKCanvas canvas, float x, float cy, bool playing, string? hoverAction,
        List<(SKRect Rect, string Action)> hits)
    {
        string[] actions = { "media-prev", "media-play", "media-next" };
        for (int i = 0; i < actions.Length; i++)
        {
            float bx = x + i * (BtnSize + BtnGap);
            float cx = bx + BtnSize / 2f;
            var rect = new SKRect(bx, cy - BtnSize / 2f, bx + BtnSize, cy + BtnSize / 2f);
            hits.Add((rect, actions[i]));
            if (hoverAction == actions[i])
            {
                using var hp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 30) };
                canvas.DrawCircle(cx, cy, BtnSize / 2f + 1f, hp);
            }
            DrawButtonIcon(canvas, actions[i], cx, cy, playing);
        }
    }

    /// <summary>矢量自绘：上一曲=竖线+左三角；播放暂停=右三角/双竖条；下一曲=镜像。</summary>
    private static void DrawButtonIcon(SKCanvas canvas, string action, float cx, float cy, bool playing)
    {
        var color = new SKColor(255, 255, 255, 216);
        switch (action)
        {
            case "media-prev":
            {
                using (var lp = new SKPaint { IsAntialias = true, Color = color, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round })
                    canvas.DrawLine(cx - 5.5f, cy - 5.5f, cx - 5.5f, cy + 5.5f, lp);
                using (var fp = new SKPaint { IsAntialias = true, Color = color })
                using (var tri = new SKPath())
                {
                    tri.MoveTo(cx + 5f, cy - 5.5f); tri.LineTo(cx + 5f, cy + 5.5f); tri.LineTo(cx - 5f, cy);
                    tri.Close();
                    canvas.DrawPath(tri, fp);
                }
                break;
            }
            case "media-play":
            {
                using var fp = new SKPaint { IsAntialias = true, Color = color };
                if (playing) // 播放中显示暂停（双竖条）
                {
                    canvas.DrawRoundRect(cx - 5.2f, cy - 5.5f, 3.6f, 11f, 1.2f, 1.2f, fp);
                    canvas.DrawRoundRect(cx + 1.6f, cy - 5.5f, 3.6f, 11f, 1.2f, 1.2f, fp);
                }
                else
                using (var tri = new SKPath())
                {
                    tri.MoveTo(cx - 4f, cy - 6f); tri.LineTo(cx - 4f, cy + 6f); tri.LineTo(cx + 6.5f, cy);
                    tri.Close();
                    canvas.DrawPath(tri, fp);
                }
                break;
            }
            case "media-next":
            {
                using (var lp = new SKPaint { IsAntialias = true, Color = color, StrokeWidth = 1.6f, StrokeCap = SKStrokeCap.Round })
                    canvas.DrawLine(cx + 5.5f, cy - 5.5f, cx + 5.5f, cy + 5.5f, lp);
                using (var fp = new SKPaint { IsAntialias = true, Color = color })
                using (var tri = new SKPath())
                {
                    tri.MoveTo(cx - 5f, cy - 5.5f); tri.LineTo(cx - 5f, cy + 5.5f); tri.LineTo(cx + 5f, cy);
                    tri.Close();
                    canvas.DrawPath(tri, fp);
                }
                break;
            }
        }
    }

    private static void DrawSpectrum(SKCanvas canvas, float x, float cy, float[]? bars)
    {
        if (bars is null || bars.Length < 5) return; // 暂停/无实例时不画，区域由布局保留
        // 与灵动岛（Renderer.MediaWidget.DrawMediaInline）完全同款：细柱 2px、间隔 2.8px、
        // 圆角 1.5、最低 2px，高度变化时上下对称伸缩（垂直居中），而非底部对齐向上长
        const float barW = 2f, spacing = 2.8f;
        const float maxH = 21f; // 灵动岛 16/条高≈40，本条 52 等比
        using var p = new SKPaint { IsAntialias = true, Color = SKColors.White };
        for (int i = 0; i < 5; i++)
        {
            float h = MathF.Max(2f, MathF.Max(0f, bars[i]) * maxH);
            float y = cy - h / 2f;
            canvas.DrawRoundRect(new SKRect(x + i * (barW + spacing), y, x + i * (barW + spacing) + barW, y + h), 1.5f, 1.5f, p);
        }
    }

    // ---- 监控组 ----

    private static float MeasureClockW()
    {
        var now = DateTime.Now;
        using var pT = new SKPaint { IsAntialias = true, TextSize = 18f, Typeface = PluginFont.ResolveBold() };
        using var pS = new SKPaint { IsAntialias = true, TextSize = 9.5f, Typeface = PluginFont.Resolve() };
        return pT.MeasureText(now.ToString("HH:mm")) + 8f + pS.MeasureText(now.ToString("M月d日"));
    }

    private static float MeasureMonitorW(string label)
    {
        using var pL = new SKPaint { IsAntialias = true, TextSize = 10f, Typeface = PluginFont.Resolve() };
        using var pV = new SKPaint { IsAntialias = true, TextSize = 12.5f, Typeface = PluginFont.Resolve() };
        return MathF.Max(pL.MeasureText(label), pV.MeasureText("100%")); // 数值宽按上限预留，GPU "--" 也不跳宽
    }

    private static float MeasureNetW(float netUp, float netDown)
    {
        using var p = new SKPaint { IsAntialias = true, TextSize = 11f, Typeface = PluginFont.Resolve() };
        return 7f + 4f + p.MeasureText(FormatSpeed(netUp))
             + 12f + 7f + 4f + p.MeasureText(FormatSpeed(netDown));
    }

    private static void DrawClock(SKCanvas canvas, float x, float cy)
    {
        var now = DateTime.Now;
        using (var p = new SKPaint { IsAntialias = true, TextSize = 18f, Typeface = PluginFont.ResolveBold(), Color = new SKColor(255, 255, 255, 245) })
        {
            string t = now.ToString("HH:mm");
            canvas.DrawText(t, x, cy + 6.5f, p);
            float tw = p.MeasureText(t);
            using (var ps = new SKPaint { IsAntialias = true, TextSize = 9.5f, Typeface = PluginFont.Resolve(), Color = new SKColor(255, 255, 255, 165) })
            {
                canvas.DrawText(now.ToString("M月d日"), x + tw + 8f, cy - 3f, ps);
                canvas.DrawText("星期" + "日一二三四五六"[(int)now.DayOfWeek], x + tw + 8f, cy + 9.5f, ps);
            }
        }
    }

    /// <summary>标签 10px 白 55% 上行 + 数值 12.5px 白 92% 下行。</summary>
    private static void DrawMonitor(SKCanvas canvas, float x, float cy, string label, string value)
    {
        var tf = PluginFont.Resolve();
        using (var p = new SKPaint { IsAntialias = true, TextSize = 10f, Typeface = tf, Color = new SKColor(255, 255, 255, 140) })
            canvas.DrawText(label, x, cy - 3.5f, p);
        using (var p = new SKPaint { IsAntialias = true, TextSize = 12.5f, Typeface = tf, Color = new SKColor(255, 255, 255, 235) })
            canvas.DrawText(value, x, cy + 12f, p);
    }

    /// <summary>↑ / ↓ 并排，箭头为自绘白色小三角（与文字统一白色）。数值为 1 秒快照直显，不平滑。</summary>
    private static void DrawNet(SKCanvas canvas, float x, float cy, float netUp, float netDown)
    {
        var tf = PluginFont.Resolve();
        float cx = x;
        DrawSpeedArrow(canvas, cx, cy, up: true);
        cx += 11f;
        using (var p = new SKPaint { IsAntialias = true, TextSize = 11f, Typeface = tf, Color = new SKColor(255, 255, 255, 235) })
        {
            string up = FormatSpeed(netUp);
            canvas.DrawText(up, cx, cy + 4f, p);
            cx += p.MeasureText(up) + 12f;
        }
        DrawSpeedArrow(canvas, cx, cy, up: false);
        cx += 11f;
        using (var p = new SKPaint { IsAntialias = true, TextSize = 11f, Typeface = tf, Color = new SKColor(255, 255, 255, 235) })
            canvas.DrawText(FormatSpeed(netDown), cx, cy + 4f, p);
    }

    private static void DrawSpeedArrow(SKCanvas canvas, float x, float cy, bool up)
    {
        using var p = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, 235) };
        using var tri = new SKPath();
        if (up)
        {
            tri.MoveTo(x + 3.5f, cy - 9f); tri.LineTo(x, cy - 2f); tri.LineTo(x + 7f, cy - 2f);
        }
        else
        {
            tri.MoveTo(x + 3.5f, cy - 2f); tri.LineTo(x, cy - 9f); tri.LineTo(x + 7f, cy - 9f);
        }
        tri.Close();
        canvas.DrawPath(tri, p);
    }
}

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
        catch (Exception ex) { Logger.Warn("[PrivateTaskbar] 媒体命令失败: " + ex.Message); }
    }
}

/// <summary>借宿主字体：每次绘制现取、绝不缓存也绝不 Dispose（宿主换字体时会释放旧字体面）。</summary>
internal static class PluginFont
{
    public static SKTypeface? Resolve()
    {
        try { return FontConfig.Normal; } catch { }
        try { return FontConfig.Fallback; } catch { }
        return null;
    }

    /// <summary>粗体（时间 HH:mm 用）；旧宿主无 Bold 档时降级回 Normal。</summary>
    public static SKTypeface? ResolveBold()
    {
        try { return FontConfig.Bold; } catch { }
        return Resolve();
    }
}
