# PrivateTaskbar 开发进度与待办（新会话依据）

> 更新时间：2026-10-25（第十四轮）。承接 `.documents/20261024_1749_privatetaskbar_requirements.md`（需求规格，权威）与 `.documents/20261024_1644_privatetaskbar.md`（实施计划）。本文档是当前最新状态，优先级高于前两份的历史描述。

## 一、项目定位

- 定制任务栏插件 `PrivateTaskbar/`（9 文件，net10.0-windows10.0.19041.0），嵌入 Windows 任务栏带的胶囊条：媒体控制（封面/歌名-歌手/歌词/三按钮/5 柱频谱）+ 时间日期 + 资源监控（CPU/RAM/GPU）+ 网速，右键菜单可配置。
- 插件 Id `com.dincccc.privatetaskbar.taskbarwidget`（含 `taskbarwidget` 子串被宿主豁免岛内显示列表），作者 dincccc。
- 自建 Win32 分层窗口（专用 UI 线程 + DIB/SKSurface + UpdateLayeredWindow），不用宿主 CreateWindow（其强制居中/固定尺寸）。
- **双产物线**：开发版（H 盘仓库 HEAD 2.0.3，`bin\PrivateTaskbar\Debug\...`）+ v2.0.2 兼容版（worktree `H:\Projects\nps-v202-wt` Release 编译 → 拷 `E:\NotchPeninsula\plugins\PrivateTaskbar.dll`，E 盘宿主为 `NotchPeninsula.v2.0.2.exe`）。**给哪个版本宿主分发就必须用对应 tag 编译**（引用版本 ≤ 宿主 AssemblyVersion）。

## 二、已完成

### v1.0 全流程（10-24 完成）
grilling 三轮定需求 → implementer 两批实施 → tester/reviewer → 11 项审查问题全修（热重载窗口类残留、DPI 双重缩放等）→ E 盘 v2.0.2 真机加载成功。

### 前两轮 bug 修复（10-24 完成，已实测回归通过）
1. WNDCLASS 缺 `CharSet=CharSet.Unicode` → 注册乱码类名，CreateWindowEx 报 1407（已修）。
2. 条悬浮任务栏上方而非嵌入 → Relayout 改为任务栏带内垂直居中、高度自适应（已修）。
3. 右键菜单弹在左上角/点击失效 → SetWindowPos 缺 SWP_NOMOVE（已修）。
4. CPU/RAM/GPU 合并为一个"资源监控"控件（Settings.ShowResources，已修）。
5. 贴托盘压住托盘图标 → 以 TrayNotifyWnd 实际左缘为基准贴靠（已修）。
6. 菜单点外不关 → 80ms 轮询 GetAsyncKeyState+GetCursorPos 兜底（已修，线程级 capture 跨进程不可靠）。
7. 菜单被 WorkingArea 夹到屏幕中间 → 改 Screen.Bounds + 向上翻转（已修）。
8. 透明像素点击穿透 → 常驻 alpha=2/255 隐形命中层（已修）。

### 第三轮修复（10-24 深夜～10-25 早晨完成，**已部署 E 盘但验证中断**）
用户报 6 个问题，根因全部定位并修复，Debug/Release 均编译通过（0 错误）：

| # | 问题 | 根因 | 修复 | 涉及文件 |
|---|------|------|------|----------|
| 1 | **P0：关闭后重新打开插件宿主崩溃** | 热重载（禁用→启用）时旧窗口类未注销，新 ALC 里 `RegisterClass` 返回 1410，原代码"沿用旧类"兜底把新窗口挂到**旧 ALC 的 WndProc**；旧线程随后退出注销类、ALC 完成卸载，新窗口下一条消息（31ms 定时器）跳进已释放代码 → Access Violation | ① 每实例 GUID 唯一类名（构造函数 `className + "_" + Guid.N`），永不撞 1410，并删除 1410 沿用兜底；② `SkiaWindow.Closing` volatile 标志（WM_TIMER/PaintFrame 开头检查）；③ Dispose 收紧：置 Closing → RunOnUi(停动画+关菜单) → RequestQuit → Join(3000) → 超时 `ForceQuitThread()`（PostThreadMessage WM_QUIT，Win32 线程 ID 在 CreateNativeWindow 里记录）→ 再 Join(1000) → 仍失败只 Error 日志（泄漏线程/内核类但**绝不崩宿主**）；④ RunOnUi 在 Hwnd==Zero 时丢弃（防 PostMessage(NULL) 投到宿主线程） | SkiaWindow.cs、PrivateTaskbarPlugin.cs |
| 2 | 开插件时屏幕左上角闪烁 | 三个叠加：窗口在 (0,0) 创建且首帧 ULW 隐式显示；`ResizeTo` 的 SetWindowPos 缺 SWP_NOMOVE（每次改宽甩回 (0,0)）；`OnWindowCreated` 的 SHOWWINDOW 也缺 SWP_NOMOVE | ① Show() 传屏幕外 `RunMessageLoop(-32000,-32000)`，OnWindowCreated 里 Relayout 就位后才 SHOWWINDOW；② ResizeTo 补 SWP_NOMOVE；③ OnWindowCreated SHOWWINDOW 补 SWP_NOMOVE | SkiaWindow.cs、TaskbarWindow.cs |
| 3 | 全屏应用时无法 hover/右键 | 全屏窗口 z 序盖住 TOPMOST 条，物理上被遮挡 | 复用宿主灵动岛方案：`SHQueryUserNotificationState`（QUNS_BUSY=2/D3D=3/PRESENTATION=4），WM_TIMER 内 800ms 节流探测，全屏→SWP_HIDEWINDOW 隐藏+复位 hover，退出→Relayout+TOPMOST+SHOWWINDOW；`_fsHidden` 与 `Hidden`（用户关闭组件）独立记账，ShowBar 时若仍全屏则只恢复定时器不亮窗 | TaskbarWindow.cs、NativeMethods.cs |
| 4 | 无歌词时标题应居中加粗 | 原固定两行布局 | 无歌词（`LayoutLyric` 空）时单行：歌名 12px **ResolveBold** 白 240、歌手 10px 灰 150 同行跟随，整行垂直居中基线 cy+4.5f；`LayoutTitleLine(media, titleBold)` 测量与绘制用同一字体（Bold 更宽，分开会溢出截断宽度） | TaskbarRenderer.cs |
| 5 | 频谱和灵动岛不一样 | 形态差异：灵动岛柱子**垂直居中、上下对称伸缩**（`y=(H-h)/2`，柱 2px/间隔 2.8px/圆角 1.5/最低 2px/纯白），我们是底部对齐向上长 3px 柱 | DrawSpectrum 完全复刻灵动岛画法（参照 `Render/Renderer.MediaWidget.cs:97-106`），maxH=21（灵动岛 16/条高≈40，本条 52 等比）；SpectrumW 27→21.2 | TaskbarRenderer.cs |
| 6 | 网速更新频率改每秒 | SystemMonitor 本就是 1s 采样，但渲染端 `_sDown/_sUp` 每帧 Lerp（31ms 一变）数字狂跳 | 删网速平滑字段；`BuildLayout/MeasureContent` 增加 `(netUp, netDown)` 参数，`DrawNet/MeasureNetW` 直接显示快照值（每秒一跳）；CPU/RAM/GPU 保留 Lerp | TaskbarRenderer.cs、TaskbarWindow.cs |

**部署状态**：5 个改动文件（NativeMethods/SkiaWindow/TaskbarWindow/TaskbarRenderer/PrivateTaskbarPlugin）已同步 `H:/Projects/nps-v202-wt/PrivateTaskbar/` 并 Release 编译（0 警告 0 错误），dll 已拷 `E:\NotchPeninsula\plugins\PrivateTaskbar.dll`（65024 字节，10-24 21:09），宿主已启动（PID 见 tasklist，进程存活）。

### 2.1.0 适配确认（10-25 完成，无需改代码）

- 主仓库 HEAD（225888d）`<Version>2.1.0</Version>`，插件经 ProjectReference 直接绑定 2.1.0 API，Debug 编译 0 警告 0 错误。
- 逐项核验：`MediaController.Instance`/`AudioAnalyzer`（Interlocked 双缓冲 GetBars）/`FontConfig`（Changed 事件）/`IPluginHost.GetSetting`/`PostReminder`/Teardown 顺序（同步 Dispose → ALC Unload 非阻塞）契约全部吻合；`taskbarwidget` 豁免（`PluginManager.cs:268` IgnoresDisplayMarker 对 Key/Id 大小写不敏感）2.1.0 保留。
- E 盘已出现 `NotchPeninsula.v2.1.0.exe`（与 v2.0.2 同目录共存，共享 plugins/）。**给 2.1.0 宿主分发用 H 盘 HEAD 编译即可**。

### 第四轮：requesting-code-review 审查与修复（10-25 完成）

reviewer 子代理审查全部 9 文件（无 Critical）+ 复核修复（8 通过、1 回归当场修正）。修复清单：

| # | 级别 | 问题 | 修复 | 文件 |
|---|------|------|------|------|
| 1 | Important | **窗口类"正常注销"从未生效**：WM_DESTROY 内 UnregisterClass 必得 1412（窗口仍在销毁序列内，实测证实），右键一次泄漏一个 GUID 窗口类（win32k 全局原子表） | 注销统一挪到 `Cleanup()` 尾部（DestroyWindow 返回后）；WM_DESTROY 分支留注释说明；CreateWindowEx 失败补偿保留；Cleanup 全路径幂等 | SkiaWindow.cs |
| 2 | Important | Initialize/Dispose 竞态：`_window` 未赋值时 Dispose 整条清理链失效，极端情况线程+窗口永久残留 | 窗口构造/订阅/`_window = w` 赋值移到宿主线程（Initialize 内），UI 线程只跑 Show()；`CreateNativeWindow` 开头 `if (Closing) return false;` 封死竞态 | PrivateTaskbarPlugin.cs、SkiaWindow.cs |
| 3 | Important | 「歌词」开关关闭时布局变单行，违反需求 2.1.2"下行留空、布局不变" | MeasureMediaText/DrawMediaText 的 single 判据统一为 `string.IsNullOrEmpty(media.Lyric)`（真无歌词才单行加粗），量画一致 | TaskbarRenderer.cs |
| 4 | Minor | RunMessageLoop 无异常兜底，InitBuffer/OnWindowCreated 抛异常会泄漏窗口+缓冲+窗口类 | 整体 try/finally { Cleanup(); } | SkiaWindow.cs |
| 5 | Minor | 全屏隐藏期间 31ms 动画 timer 空转（Clear+ULW 全透明帧） | **SetTimer 降频 800ms 而非 Stop**——timer 是全屏探测唯一驱动源，停掉会导致"退出全屏条永久消失"（复核发现的确定性回归，第一版用 StopAnimation 已回炉）；恢复时切回 31ms | TaskbarWindow.cs |
| 6 | Minor | GetMessage 未标 SetLastError，err=-1 时日志输出陈旧错误码 | 声明补 SetLastError=true | NativeMethods.cs |
| 7 | Minor | DrawSpectrum `MathF.Min(21f,44f)` 死代码 | 改 `const float maxH = 21f` | TaskbarRenderer.cs |
| 8 | Minor | ShowBar 无调用者（死代码）+ OnTimerTick 注释误导 | 删除 ShowBar；注释改为"插件重载后由新实例恢复"；StopAnimation 置零 _animTimer | TaskbarWindow.cs |
| 9 | Minor | SkiaWindow.Dispose 跨线程调用会失败但无说明 | 加注释声明仅限 UI 线程 | SkiaWindow.cs |

未修（reviewer 评估可接受）：SystemMonitor Dispose 与 GPU 重建的竞态（catch 兜底，后果仅 GPU 显示 "--"）；explorer 重启 TaskbarCreated 监听（需求未要求，暂留意）。

**git 提交（10-25）**：`.zcode/`、`.codegraph/` 加入 .gitignore 并移出暂存；按功能模块三段提交（插件源码 / 文档 / gitignore）。

### 第五轮：用户实测 4 问题修复 + 样式重设计（10-25 上午完成）

用户实测（第三轮 dll + 2.1.0 宿主）报 4 问题。**重要环境发现：当前会话中 PowerShell 注入的鼠标事件（SetCursorPos/mouse_event）被系统级吞掉**（点击系统托盘音量图标无反应、杀宿主后依旧——低级钩子过滤注入事件，嫌疑 ZCode 客户端/安全软件）；上轮"模拟点击验证法"失效。**hover/右键交互只能靠用户物理鼠标实测**；截图类验证不受影响。

| # | 用户问题 | 根因 | 修复 | 验证 |
|---|---------|------|------|------|
| 1 | 全屏后才显示条；应用关闭后条消失（连空壳都没了） | 全屏判据只有 SHQueryUserNotificationState——它会因专注助手/全屏优化等**滞留 BUSY**，应用关了条不恢复；另无任何"条意外消失"的自愈路径 | ① 全屏改双条件：QUNS 报屏蔽态 **且** `HasFullscreenForeground()`（前台窗口真覆盖整屏、排除 Progman/WorkerW/Shell_TrayWnd）；② 新增 `HealthCheck` 每 2s 巡检（仅正常显示态）：IsWindowVisible+位置偏差>8px → Relayout+SHOWWINDOW 自愈 | **程序化实测通过**：WinForms 全屏窗 → 条隐藏；关闭 → 3s 内完整恢复 |
| 2 | 网速改上下两行 ↑↓ | - | `DrawNet` 改两行（行中心 cy-7.5/cy+6 与 DrawMonitor 对齐），箭头改彩色自绘三角（↑青 5EEAD4 / ↓蓝 7CB8FF，全 UI 唯一彩色=方向语义），数值白 245 | 截图确认两行+彩色箭头 ✓ |
| 3 | hover 偶尔失效、无法右键（复现于关闭右键菜单后） | 菜单窗口 capture 期间任务栏收不到鼠标消息，关闭后其 TME_LEAVE/hover 跟踪状态可能卡死 | 菜单 `Closed` 回调（UI 线程）调 `TaskbarWindow.ResetHover()`：_hovered/_hoverAction/_trackLeave/_bgAlpha 整组复位 + Invalidate，下一次移动重走完整 hover 流程；HideBar 也改走 ResetHover | **待用户物理鼠标实测**（注入测试失效） |
| 4 | 媒体控件最右侧竖线 | 媒体区与监控组之间的 1px Divider（alpha 36） | 删除 Divider 模块，改 MediaTailGap=16px 呼吸间距 | 截图确认 ✓ |

**样式重设计（frontend-design 流程，保留胶囊形态/hover 淡入/频谱灵动岛同款）**：
- 字色三层拉开：主内容（歌名/数值/时钟）235-245→245-255，次级（label/歌手/日期）140-165→168-185，封面占位底 26→32；条 hover 背景 232→238、边框 28→30
- 菜单：底 (24,24,28,246)→(19,19,23,250)、圆角 8→10、RowH 32→34、PadV 4→6、SepH 11→13、宽 250→256、hover 30→22（圆角 7）、文字 235→243、分隔线 30→22、标记左移 21→23/文字 14/34→16/36

**部署**：新 dll（含第四+五轮全部修复）已拷 `E:/NotchPeninsula/plugins/PrivateTaskbar.dll`（旧版备份 `plugins/_recycle/PrivateTaskbar.v202-20261024.dll`），2.1.0 宿主已重启加载（08:31）。

### 第六轮：逻辑梳理重构（10-25，用户拍板"避免补丁式更新"）

用户再报 5 问题。日志实锤：**SMTC 媒体会话每 ~270ms 高频重建**（B 站直播页），含瞬时 `[null]` 封面——媒体源本身在抖；媒体控制命令**零失败记录**（都成功发出）。用户明确：**删掉全屏判断这类"聪明逻辑"，从实现逻辑梳理**。（⚠️ 本轮"条恒显示"定调已被第十四轮推翻：全屏隐藏以"前台窗口覆盖主屏"事实判据重新实现，QUNS 缺陷已根治——见第十四轮。）

**重构（第五轮的全屏双条件被推翻删除）**：
1. **删整套全屏隐藏逻辑**（QUNS 探测/前台覆盖判定/_fsHidden/timer 动态降频 + NativeMethods 对应 P/Invoke）——条恒 TOPMOST 恒显示，全屏应用盖住它=自然遮挡，不需要配合隐藏。这是"消失/闪烁/右键失效"的主要嫌疑源。
2. **保留 2s 自愈巡检**（HealthCheck，仅可见性+位置校验，与全屏无关），触发时记日志（rect+期望位置）——下次"消失"有现场证据。OnTimerTick 只剩巡检。
3. **频谱静态柱**（用户问题 1）：暂停/无数据时画 5 根最低 2px 静态柱（暗白 105），媒体区显示时频谱永远有形。
4. **ResetHover 精确化**（用户问题 3"菜单弹出后 hover 消失"的真正机制）：菜单 capture 期间任务栏收不到 move，关闭后**静止的鼠标不会再产生 WM_MOUSEMOVE**——ResetHover 现在探测光标，仍悬在条内则恢复 _hovered=true 并重注册 TME_LEAVE；HideBar 同走此路径。
5. **媒体命令日志**（用户问题 2 排查证据）：Invoke 记录命令+会话激活状态+曲目。**结论：命令全部成功发出无异常——B 站网页视频/直播的 SMTC 常不响应系统级播放控制（宿主灵动岛按钮同样无效），需用网易云/QQ音乐等正规播放器对照验证**。
6. **两模式显式化**（用户问题 5）：BuildLayout 注释定调——模式由「媒体会话激活且媒体开关开」唯一决定；有媒体=封面+文字+按钮+频谱+（时钟/资源/网速按各自开关），无媒体=仅时钟/资源/网速按各自开关；控件不做交叉耦合。

**部署**：重构版已部署 E 盘并重启宿主（08:52），截图验收：暂停态频谱静态柱/网速两行/无竖线/字色全部正常（用户设置里资源监控当前关闭）。

### 第七轮：z 序根因修复 + 首载网速（10-25，复用宿主机制）

用户再报 4 问题。**截图实锤问题 3/4 的同一根因**：右键条弹出的却是**系统任务栏菜单**——条与任务栏同为 TOPMOST，其他窗口开/关/激活重排 TOPMOST 链，条被压到任务栏之下（不可点、被任务栏盖住）。

| # | 问题 | 根因 | 修复 |
|---|------|------|------|
| 1 | 首次加载网速几百 G/s | `Start()` 先设 `_lastTick` 再立即首采，`_lastBytesIn=0` 无基线 → 字节差=开机以来全部流量 ÷ 几毫秒 | `_netBaselineReady` 标志：首采只建基线不产出；dt<0.5s 也拒绝产出（防时钟异常天量值） |
| 2 | 静态频谱颜色不醒目 | alpha 105 太暗 | 提到 170（播放态纯白动态 vs 暂停态白 170 静态，仍可区分） |
| 3+4 | 无法 hover/右键（弹的是任务栏菜单）；窗口化时条被压到任务栏下层 | 条被 TOPMOST 链重排压到任务栏之下 | **复用宿主同款置顶保活**（`NotchWindow.EnsureTopmostAlive` → 插件 `EnsureTopmost`）：每 2s `SetWindowPos(HWND_TOPMOST)` 重申 z 序，Interlocked 重入保护，前台是宿主自己进程时跳过；并入 HealthCheck 节奏 |

用户再次强调：**能用宿主提供的就用**——置顶保活直接照搬宿主机制；网速宿主无 public API（顶部 NET 是第三方 sysmonitor 插件自采），保留自采+修 bug。

**部署**：已部署 E 盘重启宿主（09:11），截图验收：静态频谱醒目、首载网速正常（1.3KB/s）。

### 第八轮：分模式控件开关 + 逐字歌词 + 帧率对齐（10-25 上午完成，v1.1.0 已部署）

用户再报 3 需求 + 要求更新版本号重新部署。全程贯彻"能用宿主提供的就用"。

| # | 需求 | 实现 |
|---|------|------|
| 1 | 无媒体时开的时钟，播放媒体后还显示（开关只有一份，两模式共用） | **分模式记忆**：`TaskbarSettings` 新增 `ShowClockMedia/ShowResourcesMedia/ShowNetMedia` 三键（`show_clock_media` 等持久化）；`BuildLayout` 按 hasMedia 读对应键；右键菜单勾选状态与切换目标按菜单打开瞬间的模式路由（新 `MenuLabel` 灰色说明行标注"有/无媒体模式 · 以下三项独立记忆"）。迁移默认：时钟媒体模式默认关（用户期望行为），资源/网速跟随主键（老设置无感）。互斥方案不需要了 |
| 2 | 歌词逐字效果 | **数据用宿主的**：`MediaController.CurrentLyricProgress`（public，与 CurrentLyric 同源同步）→ `MediaBridge.MediaInfo.LyricProgress`；**算法复刻宿主 `Renderer.DrawKaraoke`**：整句先画 40% 亮度底板 → `ClipRect(x, y-14, x+scanW, y+4)` 重画全亮。暂停时宿主 progress 冻结 → 扫光自然停住 |
| 3 | 频谱动效不如宿主流畅 | 排查结论：**数据源就是宿主的 `AudioAnalyzer`**（public 类自建实例，宿主实例是 NotchWindow private 无出口），渲染参数与宿主 `Renderer.MediaWidget` 完全一致（2px 柱/2.8 间隔/圆角 1.5），**真正差距在帧率**：插件 31ms（≈32FPS）vs 宿主默认档 90fps → 改 15ms（实际 15.6ms ≈ 64FPS，user timer 粒度上限）。`LiquidGlass.FrameIntervalMs` 是 internal 读不到，宿主档位变化需热重载跟随 |

版本号 1.0.0 → **1.1.0**（csproj + PrivateTaskbarPlugin.Version）。

**部署**：编译 0 警告 0 错误，已部署 E 盘重启宿主（10:00，v1.1.0 加载确认）。截图验收：**逐字扫光生效**（"天空海阔"亮/"你与我"暗）、**有媒体模式时钟隐藏**（分模式开关默认值正确）、频谱正常（0 错误日志）、网速两行正常。

**注意**：`cmd //c start` 在 Git Bash 后台卡死（宿主首次启动失败），改用 `powershell Start-Process -WorkingDirectory E:\NotchPeninsula` 成功。

### 第九轮：条被压闪/消失的检测式修复（10-25 上午完成，仍在 v1.1.0）

用户报：①开关应用窗口时条闪一下；②窗口反复开关后右键菜单关闭，条消失约 2s 才回来。

**根因（两个症状同一链条）**：TOPMOST 链重排把条压到系统任务栏之下（第七轮已实锤的机制，触发点包括菜单窗口销毁）。旧保活有两个缺陷：HealthCheck 巡检只查"可见性/位置"——**被压时条仍可见、位置正确，巡检完全无感知**；盲提挂在巡检里 2s 一次——被压后要等最多 2s 才拉回，这就是"消失一段时间"；开关窗口的重排被 2s 盲提拉回的过程即"闪一下"。

**修复（TaskbarWindow.EnsureTopmost 检测式升级）**：
- 从"巡检内 2s 盲提"改为 **OnTimerTick 每帧检测**（纳秒级 GetWindow 遍历）：`IsAboveTaskbar()` 查**条 vs Shell_TrayWnd 的相对 z 序**（从条沿 GW_HWNDPREV 向上走，先遇到任务栏=被压）+ 条自身 WS_EX_TOPMOST 位检查；被压才 `SetWindowPos(HWND_TOPMOST)` 提回并记 Info 日志——恢复延迟从 2s 压到一帧（15.6ms），且留下被压证据。
- `PrivateTaskbarPlugin` 菜单 `Closed` 回调（已知重排点）追加 `EnsureTopmostNow()` 立即补救。

**压力测试实锤的关键教训**：第一版判据"条必须在 TOPMOST 组绝对顶部"与 **IME 输入法窗口每 16ms 无限拉锯**（日志刷屏 5 条/80ms）——IME/开始菜单等系统 UI 合法待在 TOPMOST 组顶、并不盖条的位置。修正为"只看条 vs 任务栏相对序"后四轮压力测试零误报。条不需要压过全世界，只需要压住任务栏。

**部署**：E 盘宿主重启（10:30，PID 8136），四轮程序化 TOPMOST 窗口开关后条稳定、截图正常。

### 第十轮：宽度变化照宿主架构改弹簧动画（10-25 上午完成，仍在 v1.1.0）

用户报：插件宽度变化生硬，参考宿主方案优化。

**宿主方案探索结论（Renderer.cs / NotchWindow.cs）**：窗口创建后**尺寸恒定**（`WINDOW_WIDTH`≥1280 逻辑px × `MAX_WINDOW_HEIGHT`≥220），岛体在窗口内居中绘制（`islandLeft=(WINDOW_WIDTH-currentWidth)/2`），宽度变化是纯绘制层动画：目标宽变化记起点 → `SpringEase(t)=1-cos(2.65·t·2π)·e^(-10.8t)` 逐帧插值 → 0.45s 后 snap。**窗口从不 resize**——没有 DIB 重建、没有 DWM 重排，这正是灵动岛宽度动画流畅的根本原因。

**插件实施（宿主同架构同参数）**：
- `FixedWinW=900` 恒定窗口（覆盖全控件场景 ~830），胶囊在窗口内按位置模式对齐：贴托盘=右对齐（右缘不动左缘伸缩）、开头=左对齐、居中=居中——三种模式的可见缘语义与旧版一致。
- 弹簧参数照搬宿主（2.65Hz / 10.8 衰减 / 0.45s）；每帧目标宽=布局测量宽，**≥2px 才起弹簧**（网速数字每秒 1~2px 的抖动直接贴合，不弹）。
- 内容 `ClipRect` 裁到当前动画宽度（回弹过冲不越界）；hover 背景/命中层（alpha 2/255）跟随胶囊 rect；`TaskbarRenderer.Paint` 加 `offsetX`（内部 Translate，hits 换算回窗口坐标）；`ResetHover` 按**胶囊区**判断（窗口恒定加宽后透明穿透区不算 hover）。
- **整体删除 resize 重排机制**：`WM_APP_RELAYOUT`/`RelayoutThresholdPx`/`_relayoutPending` 消失，ResizeTo 只在 DPI/条高变化时触发。

**部署**：编译 0/0，E 盘宿主重启（11:00，PID 17044）。验证：EnumWindows 实测窗口 **900×46 恒定**（46=任务栏带高自适应）、Start 模式胶囊左对齐 x=8、截图正常（透明区完全隐形、渲染无异常）。弹簧手感受用户实测。

### 第十一轮：死代码清理（10-25，receiving-code-review 流程，仍在 v1.1.0）

逐成员交叉验证引用后删除（全部有实锤：无引用/无调用点/无发送方），行为零变化：

| 文件 | 删除项 | 依据 |
|------|--------|------|
| SkiaWindow.cs | `OnKeyDownEscape` 虚方法 | 无调用点（无 WM_KEYDOWN 分支）、无 override |
| SkiaWindow.cs | `WM_APP_CLOSEMENU` case + `CloseFromExternal` 虚方法 | 无发送方——菜单"点外关闭"早已被 PollTimer 兜底替代 |
| SkiaWindow.cs | `OnAppMessage` 虚方法 + WM_APP 区间分支 + "如任务栏的 RELAYOUT"注释 | 第十轮删 TaskbarWindow.OnAppMessage 后无 override 空转（RunOnUi 的 WM_APP+0x40 有独立 case 不受影响） |
| NativeMethods.cs | `WS_VISIBLE`、`WM_APP_CLOSEMENU` 常量 | 零引用（CreateWindowEx 故意不带 VISIBLE，注释有说明） |
| TaskbarRenderer.cs | `DividerGap` 常量 | 竖线分隔第四轮删除后零引用 |
| TaskbarRenderer.cs | `MediaInfo.IsActive` record 字段 | `Current` 构造时已过滤 `!c.IsActive`，字段无消费方（过滤本身保留） |
| TaskbarRenderer.cs | `Paint` 的未用 `width` 参数 | 方法体内未用（布局自带宽度），调用处同步 |
| TaskbarWindow.cs | `BarHeight` public→private；`_animTimer` 注释 32FPS→15ms 64FPS | 无外部消费者；注释过时 |

**有意保留**：全部 Win32 坑/铁律注释（1412 注销时机、SetWindowPos 必带 NOMOVE、alpha 穿透、热重载 GUID 类名等——它们是项目约定的文档化载体）；`OnMouseLeftUp`（有 WM_LBUTTONUP case 调用，活的空钩子契约）；`SkiaWindow.Dispose`（IDisposable 实现）。

验证：编译 0/0，E 盘宿主重启，截图渲染无回归。

### 第十二轮：二级菜单无法唤出修复（10-25，已在 v1.1.0 内，已提交后又修复）

用户报：右键菜单"位置微调"二级菜单无法唤出。

**病理链条（ContextMenu 子菜单机制一直存在的缺陷，与第十一轮清理无关）**：
1. hover 到"位置微调" → `OpenSubmenu` 弹出子菜单，capture 移交给子菜单窗口；
2. 鼠标稍动 → WM_MOUSEMOVE 发给子菜单（capture）→ 它判 `IsCursorInsideAncestor()`=true → **立即 CloseSelf**（旧逻辑"移回父级就关自己"——但子菜单刚弹出时光标必然还停在父级唤出行上，一动就误杀）；
3. **CloseSelf 不清父的 `_child`** → 父残留已销毁窗口的死引用；
4. 之后再 hover 唤出行 → `OpenSubmenu` 的 `if (_child is not null) return` 被死引用拦死 → **永远无法唤出**。

**修复（ContextMenu.cs）**：
- `CloseSelf` 清父引用（`parent._child = null`，`OnMouseLeftDown`"点回父级"路径同样受益）；
- 子菜单 `OnMouseMove` 出界分支：光标在父链上时**转交 `HandleHoverFromChild`**（父更新 hover 高亮、光标移到非唤出行才关子树收 capture），替换"一见祖先就自杀"；纯出界仍不关（点外由 PollTimer 兜底）。

**部署**：编译 0/0，E 盘宿主重启（10:55，PID 20232，窗口 900×52 确认）。交互效果需用户实测。

### 第十三轮：菜单期间保持 hover + 审查修复（10-25，v1.1.1）

用户需求：①右键菜单打开时插件要保持 hover 状态；②更新版本号，requesting-code-review 审查通过后提交。

**保持 hover（TaskbarWindow）**：右键弹菜单时 SetCapture 移交导致条收到一次 WM_MOUSELEAVE 清掉 hover。修复 = `_menuOpen` 标志：OnMouseRightDown 置位、OnMouseLeave 期间跳过清理（`_trackLeave` 仍复位，防止残留 true 跳过后续 TrackMouseEvent 重注册）、hover 状态含按钮高亮原样冻结；菜单关闭由 ResetHover 清标志并按光标实际位置重算。

**requesting-code-review 审查（reviewer 子代理，范围 01f4f3d..工作区含 6dcca95）**：结论 With fixes（无 Critical，1 Important + 1 Minor），已修：
- **P1**：菜单创建失败路径（CreateAt→CreateNativeWindow false）不走 Closed，`_menuOpen` 永久卡 true 冻结 hover。修复 = `CreateAt/PopupAt/PopupAsChild` 返回 bool；Plugin 失败时 `_menu=null + ResetHover()` 兜底；**OpenSubmenu 创建失败不登记 `_child`**（防死引用拦死后续唤出，同第十二轮病理）。
- **P2**：ResetHover 硬置 `_bgAlpha=0` 在冻结场景产生关菜单瞬间背景跳变。修复 = 删硬置，交给 OnPaint 的 lerp 自然过渡（光标在条上保持全亮、不在则 150ms 淡出）。

版本号 1.1.0 → **1.1.1**（patch：两轮 bug 修复无新功能）。

**部署**：编译 0/0，E 盘部署重启。实测清单追加：①菜单打开期间条背景/按钮高亮保持；②关菜单后光标在条上 hover 立即恢复、不在则平滑淡出；③二级菜单反复开关。

**⚠️ P1 修复引入的回归（11:19 修复）**：P1 把 OpenSubmenu 改成"先 PopupAsChild 成功再登记 _child"，但 `CreateAt` 里的 `SetCapture(child)` 会**同步**触发本窗口 WM_CAPTURECHANGED——此刻 _child 还是 null，OnCaptureLost 的链内接力识别失败 → CloseChain 整链误关 → 移向二级菜单时父菜单关闭、只剩子菜单挂屏（用户实测复现）。修复 = 恢复"先登记 _child 再弹出"（OnCaptureLost 靠 _child 链放行）+ 创建失败回滚 `_child=null`（P1 死引用防护保留）。**铁律：SetCapture 接力路径上，_child 必须在 SetCapture 之前登记**。

### 第十四轮：全屏自动隐藏重新实现（10-25，v1.2.0，未部署）

用户需求：全屏状态下隐藏任务条。**明确推翻第六轮"条恒显示"定调**（用户澄清：当时删除是因为有 BUG，不是需求被否；"全屏当然要隐藏，我要的是没有 BUG 的隐藏"，且**只动插件、不动宿主**）。

**判据设计（对 QUNS 方案的根因级替代）**：旧方案（第三~五轮）用 QUNS"意图"信号，被专注助手滞留 BUSY（BUG：条永不恢复）与 SMTC 会话抖动（BUG：闪烁）污染。新方案只用"事实"信号——**前台窗口矩形覆盖主屏 ±2px**（`FullscreenMonitor.ForegroundCoversScreen`：GetForegroundWindow→GetClassName 排除 Progman/WorkerW/Shell_TrayWnd→pid 排除自身进程→GetWindowRect ⊇ Screen.PrimaryScreen.Bounds）。独占 D3D 全屏的前台窗口矩形同样是整屏，事实判据全场景覆盖（含 QUNS 常漏的无边框窗口化全屏）。**QUNS 整体退役，插件 NativeMethods 零新增 P/Invoke**。

**实现（3 文件 + csproj）**：
- **新文件 `FullscreenMonitor.cs`**：检测内聚类。0.8s 时间戳节流（与宿主 TickFullscreenProbe 同口径）+ 迟滞（进入 1 次确认即隐藏要快；退出连续 2 次确认 ≈1.6~2.4s，吸收瞬时抖动——BUG2 根治）。任何失败按非全屏处理（保守侧：宁可不藏绝不闪烁）。`LastForegroundClass` 供隐藏日志留证。
- **TaskbarWindow.cs**：`_fsHidden` 与 `Hidden`（用户关闭组件）独立记账（写入点各自唯一，HideBar 防御性清 `_fsHidden`）；OnTimerTick 挂 `TickFullscreen`（只消费状态沿做动作）；隐藏=关菜单（若开，经新事件 `MenuDismissRequested`→插件 `_menu?.CloseTree()`，防菜单 capture 吃全屏鼠标输入）→清 hover→`SetAnimInterval(250)` 降频（**绝不能 Stop——timer 是探测唯一驱动源**）→SWP_HIDEWINDOW（带 NOSIZE|NOMOVE|NOZORDER）；恢复=Relayout（全屏期间分辨率/托盘可能变）→升回 15ms→清 hover→`HWND_TOPMOST+SWP_SHOWWINDOW` 一体（隐藏期 TOPMOST 链可能被重排，显式提回一步到位）。**五处门控**：OnPaint/HealthCheck/Relayout 加 `|| _fsHidden`（巡检绝不能把隐藏中的条拉回）、EnsureTopmost 加 `_fsHidden` 早退（短路菜单 Closed 回调的提回）、HideBar 清 `_fsHidden`。
- **OnWindowCreated 首拍**：`TickFullscreen` 提到 `Relayout` **之前**——启用/热重载落点恰在全屏中时窗口还停在屏幕外就已 HIDEWINDOW，零闪现（若先 Relayout 移入屏幕再隐藏，DWM 有单帧闪现窗口，审查实测坐实）。`_fsHidden` 时 timer 已由 EnterFullscreenHide 以 250ms 启动，不再 StartAnimation。
- **timer id 显式化（审查实测坐实的关键修复）**：旧 `SetTimer(Hwnd, IntPtr.Zero=0, ...)` 系统分配 id，本机实测 **`KillTimer` 对分配 id 杀不掉**——`StopAnimation` 在 v1.1.1 里一直是 no-op（HideBar 后 timer 仍 15ms 空转）。改为显式 `AnimTimerId=0x1990`（ContextMenu 的 `PollTimerId=0x5151` 同款先例），Kill/替换语义跨版本确定，HideBar 后 timer 真停。
- **PrivateTaskbarPlugin.cs**：订阅 `MenuDismissRequested`；版本 1.1.1→**1.2.0**（csproj 同步）。

**审查（3 个 code-reviewer 并行：正确性/铁律/简洁性）**：无 P0/P1。已修：P2 timer id（见上）、P2 首拍顺序、Tick 节流哨兵冗余删除（与 HealthCheck 同构）、隐藏日志带前台类名。8 个方向（状态机边界组合/菜单链时序/TME 幽灵事件/迟滞状态沿/旧场景回归/Dispose 竞态/DPI 坐标系/SK 与热重载铁律）全部推演通过。

**部署**：编译 0 警告 0 错误。已部署 E 盘并重启宿主（12:44），日志实锤全屏隐藏/恢复链路真实触发正常（`检测到全屏前台窗口(Chrome_WidgetWin_1)` → 4s 后恢复）。

**实测调优与方案转向（用户两轮反馈，最终定调 12:58 部署）**：
1. "全屏后停一下才隐藏"——探测周期 0.8s（沿用宿主口径）太慢。降为 **250ms**（进入延迟 ≤0.25s；探测仅 4 个 Win32 调用微秒级），恢复迟滞 2 连拍随之 ≈0.5~1s。
2. "出现时没有淡入，比较突兀"——先做过 ULW 整窗 SourceConstantAlpha 200ms 淡入（`SkiaWindow.FrameAlpha` + `_showAlpha/_fadeInAt`），用户仍觉怪，并点破本质：**"感觉全屏时没必要隐藏，只要不浮在全屏的应用、视频上面不就好了"**——隐藏/出现这套流程本身就是多余的。
3. **最终方案：z 序沉底让位（推翻隐藏/淡入整套流程）**——全屏时条 `SetWindowPos(HWND_BOTTOM)` 退 TOPMOST 组沉到 z 序最底，被全屏窗口自然盖住（像被纸压住）；退出全屏 `SetWindowPos(HWND_TOPMOST)` 原位露出。**没有隐藏、没有出现、没有任何过渡动画**——"出现突兀"从机制上不存在。淡入机制（FrameAlpha/_showAlpha/TickFadeIn）整体删除，SkiaWindow 回退。
4. 让位态门控矩阵重审：EnsureTopmost/HealthCheck 门控保留（防每帧提回打架）；**Relayout/OnPaint 门控移除**（条仍可见，分辨率/设置/字体变更实时生效，恢复时位置已新鲜，Exit 无需 Relayout）；timer 降频 250ms 保留（被盖住的帧白画，省电；绝不能 Stop）；MenuDismissRequested 保留（菜单是独立 TOPMOST 窗口，条沉底它不跟，仍会浮在全屏上）。

**误报修复（用户报"点 Win 开始菜单弹出条就消失"，15:02 部署，版本 1.2.0→**1.2.1**）**：日志实锤两个误报源——`Windows.UI.Core.CoreWindow`（开始菜单/搜索的 XAML 窗口，GetWindowRect 报整屏但视觉不满屏）与 `TscShellContainerClass`（任务视图覆盖层）。修复：类名排除加 TscShellContainerClass；XAML shell 弹层按**进程名**排除（StartMenuExperienceHost/SearchHost/SearchApp/ShellExperienceHost），查询放在矩形已覆盖整屏之后（罕见路径，不占热路径）；**不排除 CoreWindow 类名本身，避免误伤真 UWP 全屏**。遗留观察：日志另有 14:56-57 时段"条被压"高频记录（无沉底日志、EnsureTopmost 提回循环）——已向用户澄清这是第九轮的置顶保活机制（TOPMOST 链重排被压即提回），非全屏让位机制的动作；若反复出现需给被压日志加压制者类名排查。

### 第十五轮：沉底让位 → SetParent 挂载系统任务栏（10-25，v1.3.0，已部署待实测）

用户反馈："不行，问题还在。窗口切换还有闪烁""这个条沉底机制能不能避免？主动沉条也没有存在必要吧，我看另一个任务栏插件怎么没有沉底？窗口切换抖都不抖"。

**参考实现取证（反编译 E 盘市场插件 NpsTaskbarWidget v1.3.0）**：用户说的"另一个任务栏插件"= 官方 `com.notchpeninsula.taskbarwidget`（NPSTaskbarWidget，ilspycmd 反编译）。核心做法：
- 窗口样式 `WS_POPUP + WS_EX_LAYERED|NOACTIVATE`（**无 TOPMOST、无 TOOLWINDOW**）；
- `Attach()`：`GetWindowLong(GWL_STYLE)` → `(style & 0x7FFFFFFF) | WS_CHILD` → **`SetParent(hwnd, Shell_TrayWnd)`** 挂成系统任务栏子窗口；
- 全库**零置顶、零全屏检测、零主动 z 序动作**（HWND_TOPMOST 常量定义了但从未使用）——全屏/自动隐藏等行为完全跟随系统任务栏，由 Windows 全权处理，所以"窗口切换抖都不抖"；
- 挂载后**照常 UpdateLayeredWindow**（ULW_ALPHA，DIB+SkiaSharp，与我们同构技术栈）——子窗口分层窗口在 Win10/11 可用的实证；有 `RecreateWindow()`（"组件窗口已失效（多半是 Explorer 重启），正在重建"）——父死子亡实锤。

**新方案（本轮）**：删掉整套让位机制，照抄挂载思路：
- **删除**：`FullscreenMonitor.cs`（整文件）、`_fsYield` 状态机（TickFullscreen/EnterFullscreenYield/ExitFullscreenRestore）、`MenuDismissRequested` 事件、`EnsureTopmost`/`IsAboveTaskbar`/`EnsureTopmostNow`（子窗口 z 序归父窗口管，无竞争即无"被压"）、`SetAnimInterval`（让位降频用途消失）。
- **新增 `AttachToTaskbar()`**（TaskbarWindow.cs，OnWindowCreated 最先调，先挂再布局）：FindWindow("Shell_TrayWnd") → 改样式 WS_POPUP→WS_CHILD → SetParent；失败（含管理员运行被 UIPI 拦截）还原样式保持独立顶层窗口+日志。挂载成功日志 `已挂载到系统任务栏 tray=0x…`。
- **坐标语义切换**：子窗口的 SetWindowPos/ULW ptDst 均为**父客户区坐标**。Relayout 末尾算好屏幕坐标后 `ClientToScreen(taskbar)` 平移换算；HealthCheck 位置比较反向平移（GetWindowRect 恒返回屏幕坐标）；ResetHover 胶囊左缘改用 GetWindowRect().Left（原 _posX 在挂载后是客户区坐标，与屏幕光标混算会错位）。
- **Explorer 重启自愈（重建闭环）**：父死子亡 → 系统在本线程投 WM_DESTROY → `PostQuitMessage` → 消息循环返回 → 插件层 `UiThreadMain` 按 `!Closing` 识别意外死亡 → `RebuildAfterWindowLost(lost)`（`_lifecycleLock` 内：先关僵尸菜单**再查** _disposed（Dispose 竞态下 RunOnUi 对死窗口的投递被丢弃，同步关菜单是唯一机会）→ 10 分钟滑动窗限 5 次 → new TaskbarWindow（**Hidden 状态从 lost 延续**，防"关闭组件"后重启条复活）+ 订阅 + 新 STA 线程替换 `_window/_uiThread`）。Dispose 在同锁内置 `_disposed` + 引用快照，与重建互斥。
- **审查修复（1 P1 + 3 P2，全修）**：P1 **子窗口收不到 WM_DISPLAYCHANGE**（广播只发顶层窗口）——DPI 变更后旧值自洽、巡检永不发现，HealthCheck 加 DPI 比对代偿（GetDpiForSystem ≠ DpiScale100 → OnDpiOrDisplayChanged）；P2 Rebuild 链异常兜底（IsBackground 线程未处理异常会炸宿主）；P2 僵尸菜单清理提到 _disposed 检查前；P2 Hidden 重建延续。
- 审查代理另做 Win32 最小实测排除"SetParent 成功返回 NULL"歧义（实际返回非 NULL 旧内部父句柄），当前判成败写法正确。

**部署验证（15:59）**：构建 0 警告 0 错误；E 盘部署重启；日志 `窗口已创建 … 900x52 @(-32000,-32000)` → `已挂载到系统任务栏 tray=0x101E4`；EnumWindows 顶层列表**不再出现条窗口**（子窗口铁证）；GetWindowRect `(18,1033)-(918,1079)` 与挂载前逐像素一致（客户区换算精确），parent=0x101E4=Shell_TrayWnd。


## 三、待办（新会话从这里继续）


### 1. worktree 清理（命令已给出，用户自行操作）

`H:/Projects/nps-v202-wt`（detached HEAD 5d0afa7，v2.0.2 兼容编译 worktree）已无用——E 盘宿主升 2.1.0 后不再需要 v2.0.2 兼容版；5d0afa7 已包含在 main 历史中，删除不丢提交。worktree 内有未跟踪的 PrivateTaskbar/ 编译副本，需 --force：

```bash
cd H:/Projects/NotchPeninsula && git worktree remove --force H:/Projects/nps-v202-wt
# 若报错（目录被占用/句柄残留）：手动删目录后 git worktree prune
```

⚠️ 删除后将无法再编译 v2.0.2 兼容版插件（v2.0.2 宿主 AssemblyVersion 低，不能加载 2.1.0 API 编译的 dll）。E 盘若还需跑 2.0.2 宿主，`E:/NotchPeninsula/plugins/PrivateTaskbar.dll` 里那份 v2.0.2 编译产物（10-24 21:09）勿覆盖。

### 2. E 盘宿主验证（第十五轮挂载方案 v1.3.0 已部署，待用户实测）

- [x] **部署 v1.3.0 + 重启宿主**：完成（15:59，挂载日志与窗口位置已验证）。
- [ ] **挂载核心验证**：桌面/正常窗口下条显示、位置与 v1.2.1 逐像素一致；**窗口间切换反复操作 → 条纹丝不动**（本轮主诉）；全屏游戏/F11/视频 → 条被盖不浮在上面（跟随任务栏，无任何主动动作）；退出全屏 → 条原位露出。
- [ ] **交互回归**：条上 hover 淡入、左键点按钮（播放/暂停/上一曲/下一曲）、右键弹菜单、菜单内开关设置立即生效、菜单关闭后 hover/右键仍正常（菜单 capture 接力场景）。
- [ ] **Explorer 重启自愈**：任务管理器重启 explorer → 条消失（父死子亡预期行为）→ 日志出现"任务栏窗口已重建" → 条重新挂载显示、位置正确。
- [ ] **DPI 变更代偿**：改系统缩放（100%↔125%）→ ≤2s 内条尺寸/位置/清晰度自动修正（子窗口收不到 WM_DISPLAYCHANGE，巡检代偿路径）。
- [ ] **关闭组件语义**：右键菜单"关闭组件" → 条消失；explorer 重启后仍保持隐藏（Hidden 随重建延续）；插件中心禁用→启用恢复。
- [ ] **热重载回归**：插件中心禁用→立即启用反复数次（旧线程退出/新窗口类注册/挂载竞态）。
- [ ] ⚠️ 若宿主以**管理员运行**：SetParent 会被 UIPI 拦截（日志有"SetParent(Shell_TrayWnd) 失败"），条退回独立顶层 TOPMOST 窗口（旧行为，全屏会浮在上面）——需用户反馈部署形态再定对策。

### 3. 用户验收剩余项（上轮遗留）

- [ ] 媒体区实测（封面/歌名-歌手/歌词/三按钮/5 柱频谱联动播放）
- [ ] 持久化（重启记住位置/微调/模块开关）
- [ ] 空壳模式（全关+无媒体仍可右键）与"关闭组件"→插件中心禁用→启用恢复

### 4. 决策待用户拍板

- [x] **git 提交**：已完成（10-25，三段功能模块提交，`.zcode/`/`.codegraph/` 已 ignore）。
- [ ] **主工程 csproj 加 `<Compile Remove="PrivateTaskbar\**" />`**：主工程默认 Compile glob 会吞编插件源码（死代码进主 exe）；目前插件用 BaseIntermediateOutputPath/BaseOutputPath 重定向+NoWarn CS0436 规避，根治需改主工程一行，因承诺不改宿主留给用户。

## 四、关键技术约定（新会话必读）

- **SK 铁律**：SKPaint 绘制内 using；宿主 SKTypeface（FontConfig.Normal/Bold）现取现用绝不缓存绝不 Dispose；宿主位图（Thumbnail）绝不缓存绝不 Dispose（封面缓存用 ReferenceEquals 失效键的自建副本 `_thumbSmall`）。
- **宿主公共设施**（public 非冻结可直接用）：`MediaController.Instance`（Title/Artist/Thumbnail/IsPlaying/CurrentLyric/TogglePlayPause/Next/Previous/OpenCurrentApp）、`AudioAnalyzer`（`new` 即用，`GetBars()` 返回 float[5]，**当帧现读当帧消费绝不存字段**）、`FontConfig`（含 Changed 事件，Dispose 时必须退订）、`Logger`。
- **Win32 坑**：WNDCLASS 结构体必须 `[StructLayout(..., CharSet=CharSet.Unicode)]`；所有不改位置的 SetWindowPos 必须带 SWP_NOSIZE|SWP_NOMOVE；分层窗口 alpha=0 像素点击穿透（命中需 alpha≥1，常驻 2/255 命中层）；UpdateLayeredWindow 会隐式显示窗口；线程级 SetCapture 对跨进程点击不可靠（轮询兜底）。
- **热重载（本轮新增）**：窗口类必须每实例唯一（GUID 后缀），绝不沿用 RegisterClass=1410 的残留类；Dispose 必须先置 Closing 停工作、Join 超时要 PostThreadMessage(WM_QUIT) 兜底；宿主 Teardown 同步调 Dispose 后 `alc.Unload()` 异步卸载，窗口类/线程残留 = 崩溃定时炸弹。
- **UnregisterClass 时机铁律（第四轮新增）**：WM_DESTROY 内 UnregisterClass 必得 1412（窗口还在销毁序列内），只能在 DestroyWindow 返回后调（现统一在 SkiaWindow.Cleanup 尾部）；1 个窗口类占 1 个 win32k 全局原子（全系统共享），右键菜单每开一次就注册/注销一对，泄漏路径必堵。
- **动画帧率（第十四轮更新）**：SetTimer 15ms（实际 15.6ms ≈ 64FPS，user timer 粒度上限；宿主默认 90fps，`LiquidGlass.FrameIntervalMs` internal 不可读）。**timer 同时是全屏探测唯一驱动源：全屏隐藏期降频 250ms 绝不能 Stop**（停了退出全屏条永不恢复——第四轮确定性回归教训），恢复升回 15ms；调频统一走 `TaskbarWindow.SetAnimInterval`（Kill-then-Set，显式 `AnimTimerId`）。OnTimerTick 的 HealthCheck 自带 2s 时间戳节流，帧率变化不影响。
- **DPI**：进程 PMv2，Screen.WorkingArea 返回物理像素；逻辑量×scale 换物理，wa 不再乘 scale。
- **布局**：贴托盘以 `FindWindow("Shell_TrayWnd")→FindWindowEx("TrayNotifyWnd")` 实际左缘为准；条高 = min(52, 任务栏带高-2) 带内垂直居中。
- **子代理体系**：6 个通用代理在 `C:\Users\Administrator\.zcode\agents\`（explorer/vision/implementer/reviewer/tester/scribe），模型 GLM-5.3-Flash 最高档。

## 五、构建与部署命令（复制即用）

```bash
# 开发版（H 盘 HEAD = 宿主 2.1.0，产物自动装进 bin/Debug/.../plugins）
cd H:/Projects/NotchPeninsula && dotnet build PrivateTaskbar/PrivateTaskbar.csproj -c Debug
# 部署到 E 盘宿主（2.1.0）：拷 Debug 或 Release 产物
cp H:/Projects/NotchPeninsula/bin/PrivateTaskbar/Debug/net10.0-windows10.0.19041.0/PrivateTaskbar.dll E:/NotchPeninsula/plugins/
# 重启 E 盘宿主 2.1.0（先 taskkill 旧进程）
cd E:/NotchPeninsula && ./NotchPeninsula.v2.1.0.exe &
# ⚠️ v2.0.2 兼容编译线（nps-v202-wt worktree）已随 2.1.0 升级废弃，清理命令见待办第 1 节
```
