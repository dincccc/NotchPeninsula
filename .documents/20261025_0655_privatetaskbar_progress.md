# PrivateTaskbar 开发进度与待办（新会话依据）

> 更新时间：2026-10-25 09:30。承接 `.documents/20261024_1749_privatetaskbar_requirements.md`（需求规格，权威）与 `.documents/20261024_1644_privatetaskbar.md`（实施计划）。本文档是当前最新状态，优先级高于前两份的历史描述。

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

## 三、待办（新会话从这里继续）

### 1. worktree 清理（命令已给出，用户自行操作）

`H:/Projects/nps-v202-wt`（detached HEAD 5d0afa7，v2.0.2 兼容编译 worktree）已无用——E 盘宿主升 2.1.0 后不再需要 v2.0.2 兼容版；5d0afa7 已包含在 main 历史中，删除不丢提交。worktree 内有未跟踪的 PrivateTaskbar/ 编译副本，需 --force：

```bash
cd H:/Projects/NotchPeninsula && git worktree remove --force H:/Projects/nps-v202-wt
# 若报错（目录被占用/句柄残留）：手动删目录后 git worktree prune
```

⚠️ 删除后将无法再编译 v2.0.2 兼容版插件（v2.0.2 宿主 AssemblyVersion 低，不能加载 2.1.0 API 编译的 dll）。E 盘若还需跑 2.0.2 宿主，`E:/NotchPeninsula/plugins/PrivateTaskbar.dll` 里那份 v2.0.2 编译产物（10-24 21:09）勿覆盖。

### 2. E 盘宿主迁移 2.1.0 + 验证（上轮验证清单仍然全部未做）

- [ ] **部署 2.1.0 版插件**：H 盘 HEAD 编译的 dll 拷 `E:/NotchPeninsula/plugins/`（会覆盖 v2.0.2 编译产物，先确认不再需要 v2.0.2）。E 盘启动 `NotchPeninsula.v2.1.0.exe`。
- [ ] **日志验证**：宿主日志确认新 dll 加载成功、窗口创建日志类名带 GUID 后缀、创建位置 -32000。日志位置未最终确认——`$LOCALAPPDATA/NotchPeninsula/` 下有 `app.log`/`app-20261024-20231313.log`/`settings-hub.log`（上次 tail 被取消，未读到内容）。
- [ ] **P0 崩溃场景实测**：插件中心禁用→立即重新启用，反复数次，宿主不崩、条恢复显示。本轮最重要的验证。
- [ ] **窗口类泄漏回归**：反复右键开关菜单数次 + 禁用/启用数次后，宿主日志无 RegisterClass 失败；本轮修复 1 的验证。
- [ ] **全屏回归（重点）**：F11/视频全屏进入→退出，条必须自动恢复（本轮修复 5 的降频 timer 路径）；进入全屏期间 CPU 占用应下降（800ms 空转帧消除）。
- [ ] 左上角闪烁：重启宿主观察启用瞬间。
- [ ] 频谱观感、网速每秒一跳、无歌词单行加粗（回归确认）。
- [ ] 桌面验证方法（已验证可用）：PowerShell Add-Type 截屏(CopyFromScreen)+SetCursorPos/mouse_event 模拟点击+EnumWindows/GetWindowRect 枚举窗口+vision 代理分析截图；注意 ZCode 客户端 UI 可能遮挡任务栏造成误判；PowerShell 5.1 不支持 C#6+ 语法（string?/$""），Add-Type 用封装 Run() 方法模式。
- [ ] E/H 盘宿主共享插件启用/失败名单（复现前需清理注册表状态）。

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
- **全屏 timer 铁律（第四轮新增）**：31ms 动画 SetTimer 是 OnTimerTick 全屏探测的唯一驱动源——隐藏期间只能降频（SetTimer 替换语义换 800ms），绝不能 KillTimer，否则退出全屏后条永久消失。
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
