using NotchPeninsula.Plugins;

namespace PrivateTaskbar;

public enum TaskbarPosition { Start, Center, End }

/// <summary>任务栏设置。整个对象不可变，修改 = 新实例替换（volatile 引用，渲染线程安全读）。
/// record：右键菜单用 `with` 表达式生成修改后的新实例。</summary>
public sealed record TaskbarSettings
{
    public TaskbarPosition Position { get; init; } = TaskbarPosition.End; // 终版默认：贴托盘
    public int OffsetX { get; init; }                       // 位置微调像素（-500~500，步长 10）
    public bool ShowMedia { get; init; } = true;
    public bool ShowLyric { get; init; } = true;
    public bool ShowClock { get; init; } = true;
    public bool ShowResources { get; init; } = false;   // 「资源监控」=CPU+RAM+GPU 合并控件；终版默认只勾时间日期
    public bool ShowNet { get; init; } = false;

    public static TaskbarSettings Load(IPluginHost host)
    {
        static string Get(IPluginHost h, string k, string fb) { try { return h.GetSetting(k, fb); } catch { return fb; } }
        return new TaskbarSettings
        {
            Position = Get(host, "position", "end").ToLowerInvariant() switch
            {
                "start" => TaskbarPosition.Start,
                "center" => TaskbarPosition.Center,
                _ => TaskbarPosition.End,
            },
            OffsetX = Math.Clamp(int.TryParse(Get(host, "offset_x", "0"), out var ox) ? ox : 0, -500, 500),
            ShowMedia = Get(host, "show_media", "1") != "0",
            ShowLyric = Get(host, "show_lyric", "1") != "0",
            ShowClock = Get(host, "show_clock", "1") != "0",
            ShowResources = Get(host, "show_resources", "0") != "0",
            ShowNet = Get(host, "show_net", "0") != "0",
        };
    }

    public void Save(IPluginHost host)
    {
        host.SetSetting("position", Position switch { TaskbarPosition.Start => "start", TaskbarPosition.Center => "center", _ => "end" });
        host.SetSetting("offset_x", OffsetX.ToString());
        host.SetSetting("show_media", ShowMedia ? "1" : "0");
        host.SetSetting("show_lyric", ShowLyric ? "1" : "0");
        host.SetSetting("show_clock", ShowClock ? "1" : "0");
        host.SetSetting("show_resources", ShowResources ? "1" : "0");
        host.SetSetting("show_net", ShowNet ? "1" : "0");
    }
}
