# MagicView Wallpaper

面向荣耀 **Magic 视界**桌面环境的 Windows 动态/静态壁纸管理器。

项目通过 Win32 将独立壁纸画布挂载到 Magic 视界桌面宿主内部，再使用 MPV 渲染图片或视频，从而在保留天气、性能监控、宠物卡片、日历、Dock 等 Magic 视界组件的同时显示动态壁纸。

> [!IMPORTANT]
> 本项目是非官方实验性适配器，与荣耀（HONOR）及 MPV 项目不存在隶属或授权关系。它依赖 Magic 视界当前版本的内部窗口结构；荣耀软件升级后，相关窗口类名或层级可能变化，届时需要更新适配代码。

## 当前状态

当前文档对应开发版本：**v0.6.4（预览版）**。正式发布时应以 Git tag 和 Release 版本为准，避免 README 中的版本号与源码漂移。

主要功能已可日常使用，但尚未提供 MSI/MSIX 安装包，也尚未在大量不同型号、Magic 视界版本和显示器拓扑上完成兼容性测试。公开发布前建议补充许可证、自动化构建和更多机型测试。

## 快速判断与开始

- **适用**：Windows 11 x64、荣耀 Magic 视界能够正常运行、支持 D3D11 的设备。
- **不适用**：没有 Magic 视界的普通电脑，以及当前窗口结构不同的 Magic 视界版本。
- **获取**：仓库目前尚未提供正式 Release；发布后应在本节加入 Releases 链接、ZIP 文件名和 SHA-256。现阶段请从源码构建。
- **首次运行**：先开启 Magic 视界，再启动管理器；应用壁纸时会请求 UAC，用于启动后台渲染引擎。
- **程序签名**：当前构建未进行代码签名，Windows SmartScreen 可能显示未知发布者；只应运行自己构建或从可信 Release 下载且校验一致的文件。
- **出现问题**：查看 `%LOCALAPPDATA%\MagicViewWallpaper\app.log`，反馈前先删除私人路径。

## 目录

- [功能](#功能)
- [界面与后台的关系](#界面与后台的关系)
- [实现原理](#实现原理)
- [兼容性](#兼容性)
- [使用方式](#使用方式)
- [数据与配置位置](#数据与配置位置)
- [从源码构建](#从源码构建)
- [常见问题](#常见问题)
- [安全与隐私](#安全与隐私)
- [已知限制与后续计划](#已知限制与后续计划)
- [GitHub 发布前注意事项](#github-发布前注意事项)

## 功能

- 可视化壁纸库，支持搜索、选择、应用和随机下一张。
- 导入一个或多个文件，或递归导入整个文件夹。
- 为动态壁纸自动提取视频帧作为预览缩略图。
- 静态图片格式：`.jpg`、`.jpeg`、`.png`、`.bmp`。
- 动态视频格式：`.mp4`、`.webm`、`.mkv`、`.avi`、`.mov`、`.wmv`、`.m4v`。
- 自动随机切换，并避免立即重复当前壁纸。
- 可配置切换间隔；图片和视频共用一个淡入淡出时长。
- `Cover`（铺满裁剪）与 `Contain`（完整显示）适配方式。
- 保持视频源帧率，或限制为 60/30 FPS。
- D3D11 GPU 渲染及 D3D11VA 硬件视频解码。
- 可选择主显示器、指定显示器或全部显示器。
- 全部显示器模式为每块屏幕建立独立画布，分别处理分辨率和 DPI。
- 支持混合缩放，例如主屏 200%、HDMI 副屏 100%。
- 独立后台引擎，关闭管理界面后继续渲染和自动切换。
- 系统托盘：打开管理器、随机下一张、暂停自动切换、退出引擎。
- 可选开机启动，通过 Windows 计划任务启动后台引擎。
- 开始菜单、桌面快捷方式及专用图标。
- 本地日志和 JSON 配置，方便诊断宿主、DPI、画布和播放器问题。

## 界面与后台的关系

同一个 `MagicViewWallpaper.App.exe` 包含两种运行模式：

```text
普通启动
  └─ WPF 管理器（壁纸库、导入、设置、状态显示）
       └─ 本机命名管道 ──────────────┐
                                      ▼
--engine 启动                    后台引擎
  ├─ 系统托盘                    ├─ 壁纸扫描/随机切换
  ├─ 单实例互斥锁                ├─ Magic 视界宿主探测
  ├─ 开机启动计划任务            ├─ 每屏 Win32 画布
  └─ 用户级 IPC 权限             └─ 每屏一个 MPV 渲染进程
```

- 管理器以普通权限运行，仅负责界面和发送命令。
- 后台引擎以 `--engine` 参数运行，首次启动时可能出现 UAC 授权窗口。
- 前后台通过 `MagicViewWallpaper.Engine.v1` 命名管道交换 JSON 命令。
- 管道 ACL 仅授权当前 Windows 用户。
- 引擎使用 `Local\MagicViewWallpaper.Engine` 互斥锁，防止同一 Windows 会话内运行多个同名后台引擎。
- 关闭管理器不会关闭后台引擎；需要通过托盘或界面中的“退出后台引擎”结束。

## 实现原理

### 1. 定位 Magic 视界宿主

后台引擎枚举 Windows 顶级窗口及子窗口，查找窗口类：

```text
HonorDesktop_HideWnd_Class
```

候选窗口必须具有有效客户区。常见层级的快速路径还会验证以下卡片挂载子窗口：

```text
NewExplorerCardMountHost
```

回退路径会枚举同类窗口，并通过客户区尺寸排除尺寸为零、只用于通知或消息处理的窗口；当前按屏幕计算交集的候选筛选不强制要求每个候选都包含上述子窗口。多显示器情况下，引擎根据目标显示器与候选宿主的交集面积选择宿主。该逻辑依赖未公开的内部层级，后续应统一并强化候选验证。

### 2. 创建透明壁纸画布

引擎在宿主内部创建名为 `MagicViewWallpaperCanvas` 的 Win32 子窗口，主要样式包括：

- `WS_CHILD | WS_VISIBLE`
- `WS_EX_LAYERED`
- `WS_EX_TRANSPARENT`
- `WS_EX_NOACTIVATE`

画布被禁用且不会激活，因此不应拦截桌面鼠标操作；桌面右键菜单和 Magic 视界卡片仍可交互。

### 3. 将 MPV 嵌入画布

每个目标显示器对应一个独立画布和一个 MPV 进程。MPV 使用 `--wid=<HWND>` 嵌入画布，通过 D3D11 输出：

```text
--vo=gpu
--gpu-api=d3d11
--hwdec=d3d11va
--loop-file=inf
```

静态图片也由 MPV 无限持续显示，使图片和视频共用相同的多屏、缩放、过渡和生命周期管理逻辑。

### 4. 多显示器和混合 DPI

Magic 视界宿主、WinForms `Screen` 与 Windows 物理显示模式有时会返回不同坐标空间。例如一块 3840×2160、200% 缩放的显示器，桌面宿主实际可能使用 1920×1080 的逻辑区域。

当前实现使用以下启发式兼容策略：

1. 获取每块屏幕的 `Bounds` 和 `WorkingArea`。
2. 当两轴倍率均不低于 125% 且倍率接近时，推断存在 DPI 坐标差异。
3. 使用 `ScreenToClient` 同时换算屏幕左上角和右下角。
4. 以宿主客户区中的坐标差值作为最终画布宽高。
5. 每块屏幕单独创建画布，避免将主屏 DPI 直接套用到副屏。

后台每秒检查**已有画布**的位置和尺寸，但只有数据真正变化时才调用 `SetWindowPos`；调整时保持已有 Z 轴层级，避免周期性露出 Magic 视界原壁纸。新增/移除显示器、设备名变化或目标屏幕集合变化时，不会自动创建或销毁 MPV 进程，必须重新应用壁纸，必要时重启后台引擎。

这套 DPI 判断不是 Windows 所有显示拓扑的通用解法。任务栏布局、旋转、非标准缩放和特殊桌面宿主仍可能造成误判，目前仅对下方列出的实测组合负责。

### 5. 壁纸切换

切换时不会立即销毁旧画布。引擎先创建并预热新画布，然后逐步提高新画布透明度；过渡完成后才停止旧 MPV 进程并销毁旧画布。因此静态与动态之间的四种组合使用相同的淡入淡出流程。

### 6. 动态缩略图

壁纸库使用 MPV 在视频约 10% 处截取一帧，缩放到 640 像素宽并保存为 PNG。缓存键由文件路径、大小和最后修改时间生成 SHA-256；视频被替换后会自动生成新缩略图。

## 兼容性

### 必要条件

- Windows 11 x64。
- 已安装并可以正常开启荣耀 Magic 视界。
- Magic 视界当前版本仍包含可访问的 `HonorDesktop_HideWnd_Class` 宿主。
- 支持 Direct3D 11 的显卡和驱动。
- 框架依赖发布包需要 .NET 10 Desktop Runtime x64；源码构建需要 .NET 10 SDK x64。
- 当前登录账户本身是管理员，并以同一账户确认 UAC。使用另一管理员账户的凭据提升可能导致数据目录、身份和管道 ACL 不一致，当前未保证兼容。

### 已实际验证的场景

- 荣耀电脑内置主屏，3120×2080。
- 主屏 200% DPI 缩放。
- HDMI 副屏，桌面逻辑区域 1920×1080。
- 主、副屏使用不同 DPI/坐标空间。
- 静态和动态壁纸渲染到单屏或全部显示器。

开发阶段尚未记录可复现的 Magic 视界版本号、Windows build、GPU/驱动和 MPV build。创建首个公开 Release 前应补齐这些信息；在此之前，上述内容只代表当前开发机实测，不构成同类机型兼容保证。

### 尚未保证的场景

- 没有 Magic 视界的普通 Windows 电脑。
- 华为、小米、联想、戴尔等其他厂商的桌面组件。
- Magic 视界窗口结构已改变的未来版本。
- Windows 10、Windows on ARM、32 位 Windows。
- HDR、竖屏、显示器旋转、远程桌面、显卡热切换和三屏以上组合。
- 每块显示器使用不同壁纸。目前“全部显示器”是在各屏独立适配同一个壁纸。

程序可以在没有 Magic 视界的机器上打开管理界面，但应用壁纸时会提示找不到 Magic 视界桌面宿主。

## 使用方式

### 1. 准备发布目录

正式 Release 发布后，下载压缩包并完整解压到一个长期不移动的目录。当前尚无公开 Release 时，请按照“从源码构建”生成发布目录。不要只复制 EXE；以下内容必须保留：

```text
MagicViewWallpaper.App.exe
MagicViewWallpaper.App.dll
MagicViewWallpaper.App.deps.json
MagicViewWallpaper.App.runtimeconfig.json
Assets/
Runtime/mpv/
Install-Shortcuts.ps1
Uninstall-Shortcuts.ps1
安装快捷方式.cmd
卸载快捷方式.cmd
```

移动目录后需要重新安装快捷方式并重新启用开机启动，因为快捷方式和计划任务保存的是绝对路径。

### 2. 避免多个壁纸引擎冲突

首次使用前退出 Lively Wallpaper、Wallpaper Engine 和旧版 `MagicViewWallpaperAdapter` PowerShell 脚本。多个程序同时管理桌面层级或切换壁纸会导致覆盖、闪烁或 GPU 占用叠加。

### 3. 启动和导入

1. 确认 Magic 视界已经开启。
2. 双击 `MagicViewWallpaper.App.exe`。
3. 首次应用壁纸时允许 UAC 授权，以启动后台引擎。
4. 使用“导入壁纸”或“导入文件夹”建立壁纸库。
5. 单击卡片选中壁纸，再点击“应用所选”；也可以双击卡片直接应用。

### 4. 播放与切换设置

- **自动切换**：按设定时间随机选择下一张。
- **切换间隔**：单位为秒，最小值为 10 秒。
- **过渡时间**：单位为毫秒，范围 0–5000。
- **铺满裁剪（Cover）**：填满屏幕，宽高比不同时裁剪边缘。
- **完整显示（Contain）**：保留完整画面，宽高比不同时可能有黑边。
- **视频帧率**：保持源帧率、60 FPS 或 30 FPS。
- **目标显示器**：主屏、指定屏幕或全部屏幕。

更改目标显示器或渲染参数后，保存设置并重新应用一次壁纸。

### 5. 后台和托盘

关闭管理器窗口后，后台引擎继续运行。托盘菜单提供打开管理器、随机下一张、暂停/恢复自动切换和退出后台引擎。如果 Explorer 重启，后台最多约 3 秒后重新注册托盘图标。

### 6. 开机启动

在“播放设置”中勾选“开机启动”。程序创建以下计划任务：

```text
任务名称：MagicViewWallpaperEngine
触发条件：用户登录后延迟 10 秒
运行级别：最高权限
命令：MagicViewWallpaper.App.exe --engine
```

取消勾选会删除任务。互斥锁会阻止重复后台实例。

### 7. 快捷方式

双击发布目录中的 `安装快捷方式.cmd`，可创建开始菜单和桌面快捷方式。随后可以：

- 在开始菜单搜索 `MagicView Wallpaper`，右键固定到“开始”。
- 右键桌面快捷方式，选择“显示更多选项”，再固定到任务栏。

Windows 11 不允许普通桌面程序静默固定任务栏，因此最后一步需要用户确认。运行 `卸载快捷方式.cmd` 可移除快捷方式。

## 数据与配置位置

用户数据默认保存在：

```text
%LOCALAPPDATA%\MagicViewWallpaper
```

```text
MagicViewWallpaper/
├─ settings.json          # 播放、切换、显示器及当前壁纸设置
├─ app.log                # 后台引擎和渲染诊断日志
├─ Library/               # 从界面导入的壁纸副本
└─ Cache/Thumbnails/      # 动态壁纸预览图缓存
```

默认来源目录包含内部 `Library`，并兼容开发阶段的 `D:\Wallpapers`。机器没有 D 盘时该路径会被忽略，不影响内部壁纸库。

| 字段 | 含义 | 默认值 |
| --- | --- | --- |
| `SourceFolders` | 壁纸扫描目录 | 内部 Library、`D:\Wallpapers` |
| `AutoSwitchEnabled` | 自动切换 | `true` |
| `SwitchIntervalSeconds` | 切换间隔 | `300` |
| `TransitionDurationMs` | 淡入淡出时长 | `900` |
| `FitMode` | `Cover` 或 `Contain` | `Cover` |
| `TargetMonitor` | `PRIMARY`、`ALL` 或设备名 | `PRIMARY` |
| `VideoFpsLimit` | `0` 表示保持源帧率 | `0` |
| `ScaleQuality` | MPV 缩放算法 | `bilinear` |
| `MuteVideo` | 视频静音 | `true` |
| `CurrentWallpaper` | 当前壁纸绝对路径 | 空 |

## 项目结构

```text
MagicViewWallpaper/
├─ MagicViewWallpaper.slnx
├─ NuGet.Config
├─ README.md
├─ tools/GenerateIcon.ps1
└─ src/MagicViewWallpaper.App/
   ├─ App.xaml(.cs)              # 进程模式入口、引擎单实例
   ├─ MainWindow.xaml(.cs)       # WPF 管理界面
   ├─ EngineHost.cs              # 托盘、定时切换、IPC、开机启动
   ├─ EngineService.cs           # 宿主、画布、MPV、多屏和过渡
   ├─ EngineIpc.cs               # 命名管道协议与客户端
   ├─ Models.cs                  # 设置、壁纸模型和本地存储
   ├─ ThumbnailService.cs        # 视频缩略图缓存
   ├─ Assets/AppIcon.ico
   ├─ Runtime/mpv/               # MPV 运行时文件
   └─ *Shortcuts*                # 快捷方式安装/卸载脚本
```

## 从源码构建

### 开发环境

- Windows 11 x64
- [.NET 10 SDK x64](https://dotnet.microsoft.com/download/dotnet/10.0)
- 支持 WPF、Windows Forms 和 Win32 API 的 Windows 桌面环境
- MPV Windows x64 运行文件，放在 `src/MagicViewWallpaper.App/Runtime/mpv/`

项目不依赖第三方 NuGet 包，`NuGet.Config` 会清空远程包源；桌面框架和命名管道功能来自 .NET Windows Desktop SDK。

仓库当前包含开发阶段使用的 MPV 文件，但尚未记录其准确版本、构建来源和校验和，因此还不能视为可复现的公开依赖。维护者应先确认许可证与来源，再在本节固定可信下载地址、版本、所需文件清单和 SHA-256。运行所需的最低目录为：

```text
Runtime/mpv/mpv.exe
Runtime/mpv/d3dcompiler_43.dll
Runtime/mpv/msvcr100.dll
```

### 完整开发流程

```powershell
# 1. 克隆后进入仓库根目录
git clone <repository-url>
cd MagicViewWallpaper

# 2. 检查 SDK
dotnet --info

# 3. 按上文准备 Runtime/mpv/，然后编译
dotnet build .\src\MagicViewWallpaper.App\MagicViewWallpaper.App.csproj -c Release

# 4. 开发运行（先开启 Magic 视界）
dotnet run --project .\src\MagicViewWallpaper.App\MagicViewWallpaper.App.csproj -c Release
```

编译成功标准为 0 个错误；运行后应出现管理界面，应用壁纸时启动后台引擎并在系统托盘显示图标。

`NuGet.Config` 清空了远程源，适合当前无第三方包且 SDK/目标包已安装的离线构建。如果自包含发布提示缺少 `win-x64` runtime pack，需要在可联网、来源受控的构建环境中恢复对应 .NET 运行时包，或调整 NuGet 源后由 CI 固定和验证依赖。

### 编译

```powershell
dotnet build .\src\MagicViewWallpaper.App\MagicViewWallpaper.App.csproj -c Release
```

### 框架依赖发布

目标机器需要 .NET 10 Desktop Runtime x64：

```powershell
dotnet publish .\src\MagicViewWallpaper.App\MagicViewWallpaper.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o .\artifacts\publish\win-x64
```

### 自包含发布

自包含版本体积更大，但目标机器不需要预装 .NET Runtime：

```powershell
dotnet publish .\src\MagicViewWallpaper.App\MagicViewWallpaper.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\artifacts\publish\win-x64-self-contained
```

发布后确认 `Runtime/mpv/mpv.exe`、图标和快捷方式脚本均在输出目录。

## 常见问题

### 提示“未找到 Magic 视界桌面宿主”

1. 确认 Magic 视界已开启并显示卡片/Dock。
2. 退出后台引擎后重新启动，确保宿主先于引擎创建。
3. 查看 `app.log` 中是否找到了 `HonorDesktop_HideWnd_Class`。
4. 如果荣耀更新后持续失败，可能需要更新窗口类和层级探测逻辑。

### 壁纸被放大或只显示一部分

- 检查“画面适配”是否为“铺满裁剪”；需要完整画面时使用“完整显示”。
- 确认选择了正确显示器。
- 完全退出旧后台引擎后再启动新版本；DPI 感知模式在进程启动时确定。
- 查看日志中的 `Bounds`、`WorkingArea`、`Effective` 和 `Child`；`Child` 应与 Magic 视界宿主实际桌面区域一致。

### 屏幕闪回 Magic 视界原壁纸

- 使用 v0.6.4 或更高版本。
- 退出 Lively Wallpaper 和其他桌面壁纸程序。
- 检查是否同时运行旧版 PowerShell 适配脚本。
- 通过托盘完全退出旧引擎，再从当前发布目录启动。

### GPU 占用较高

- “全部显示器”会为每块屏幕启动一个 MPV 进程，开销随屏幕数量和分辨率增加。
- 4K、高刷新率或高码率视频会增加负载。
- 可限制为 30 FPS、使用 `bilinear` 缩放，并选择较低分辨率的视频源。
- 确认没有同时运行其他动态壁纸引擎。

### 托盘没有图标，但壁纸仍在运行

- 点击任务栏托盘的“显示隐藏的图标”。
- Explorer 重启后等待约 3 秒。
- 若仍未出现，通过任务管理器结束旧引擎，再启动当前版本。

### 移动目录后开机启动失效

开机任务保存 EXE 绝对路径。取消并重新勾选“开机启动”，同时重新运行 `安装快捷方式.cmd`。

## 安全与隐私

- 程序没有内置遥测、自动更新或云服务。若手工把来源目录或壁纸路径配置为 UNC/网络路径，Windows 文件 API 和 MPV 仍可能访问网络。
- 壁纸、缩略图、配置和日志均保存在本机。
- 管理器默认不提升权限；后台引擎通过 UAC 启动。
- IPC 使用仅引擎身份可访问的命名管道 ACL，用于阻止其他 Windows 用户连接；这不等于防范当前用户会话中的恶意进程。
- 后台引擎及其启动的 MPV 子进程当前均在提升权限下运行。管道的 `Apply` 命令只检查路径是否存在，尚未规范化并限制到壁纸库，也未在引擎端再次校验扩展名。因此当前版本应只用于可信本机环境。
- 导入文件会复制到内部 Library；导入文件夹会扫描并复制支持的文件。
- 日志会记录壁纸完整路径。公开反馈问题前请移除用户名或私人目录信息。

## 已知限制与后续计划

- Magic 视界没有公开的第三方动态壁纸 API；当前实现使用内部 Win32 窗口结构，兼容性无法像官方接口一样保证。
- 当前所有目标显示器使用同一张壁纸，尚未支持每屏独立壁纸列表。
- 显示器热插拔、旋转、HDR 和睡眠唤醒仍需更多测试。
- 当前只有目录式便携发布，尚无安装器、自动更新和完整卸载程序。
- 尚未实现播放器崩溃后的自动重建。
- 显示器热插拔后不会自动增删渲染表面，需要重新应用壁纸。
- 高权限引擎的文件路径验证和 MPV 降权尚需加固。
- 尚无单元测试、UI 自动化测试和 GitHub Actions 发布流水线。

建议后续优先完成：

1. 将 Magic 视界宿主探测抽象为可版本化适配层。
2. 增加显示器拓扑变化事件和睡眠/唤醒恢复。
3. 支持每屏独立壁纸及独立播放参数。
4. 增加播放器崩溃重启和画布层级自愈。
5. 建立 GitHub Actions 构建、版本号、校验和及 Release 流程。
6. 制作 MSIX/WiX 安装包并提供干净卸载。

## 参与开发与反馈

提交 Issue 时建议附带：

- 电脑型号和 Magic 视界版本。
- Windows 版本。
- 显示器数量、连接方式、分辨率和缩放比例。
- 静态还是动态壁纸出现问题。
- `%LOCALAPPDATA%\MagicViewWallpaper\app.log` 中与问题时间对应的片段。

请先删除日志中的私人路径信息。涉及窗口层级或 DPI 的问题，最好同时提供 Windows 显示设置截图和 Magic 视界桌面截图。
