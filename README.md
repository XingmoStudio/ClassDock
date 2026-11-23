![Placeholder](Assets/Banner.png)

# ClassDock 课畔

ClassDock 是一个面向教学场景的桌面侧边栏工具集，致力于将常用教学工具整合到同一入口。它强调易用性、便捷性与自由，希望帮助教师在不打断课堂节奏的前提下，更轻松地使用教学中的常用功能，从而优化教学体验环境。

## 当前版本的内容

### 0.1 PaintingTheWorld 绘世

#### 屏幕批注

- [x] 单点书写（可切换粗细、颜色、笔型）

- [x] 擦除(面积擦除),笔速橡皮

- [x] 几何图形(支持类型如下表所示)
  | 原型     | 变种      |
  | :----- | :------ |
  | 矩形     | 圆角矩形    |
  | 三角形    | 直角三角形   |
  | 直线     | 虚直线     |
  | 带箭头的直线 | 带箭头的虚直线 |
  | 圆形     | 椭圆形     |

- [x] 工具
  - 直尺
  - 圆规
  - 30,60,90度的三角板
  - 45,45,90度的三角板
  - 量角器
  - 保存至本地

- [x] 抬笔时笔锋修正

- [x] 记忆退出前的颜色

#### 设置,界面与底层

- [x] 设置 by WinUI 3 界面

- [x] 主题模式(默认:跟随系统)
  - 跟随系统
  - 深色
  - 浅色

- [x] 强调色设置
   - 从系统中获取
   - 自定义

- [x] 动画级别设置
  - 标准
  - 无

- [x] 书写灵敏度调节
   - 低频
   - 默认
   - 高频

- [x] 配置备份

- [x] 发生错误时处理方式设置
    - 静默退出
    - 退出并显示错误信息
    - 静默重启
    - 不处理

- [x] 日志查看器

- [x] 事件抛出器(全局方法,私用方法暂时没有什么办法调用)

## 技术架构

ClassDock 基于 **WinUI 3 (Windows App SDK)** 构建：

- 使用 **C# + WinUI 3 XAML**，Fluent 2 浅色设计语言；
- 非打包模式（Unpackaged）+ WinAppSDK 自包含，Windows App SDK 无需额外安装；
- 通过原生 Win32 / DWM 互操作实现无边框透明覆盖、全局置顶、窗口最小尺寸解除等功能；
- 后续屏幕批注将基于"整个屏幕作为可穿透画布"的设计思想实现。

## AI 创作声明

本项目的代码与界面主要由 **AI（Trae Agent）** 生成与迭代，由项目作者提出需求、审核并整合。

## 运行环境要求

### Windows

- **操作系统**：Windows 10 版本 1809（10.0.17763）及以上，或 Windows 11
- **运行时**：`full` 变体需要 .NET 10 Desktop Runtime（x64）；`self-contained` 变体无需安装
- **推荐环境**：双核 CPU、8GB 内存、500MB 以上可用磁盘空间

## 构建与运行

### 构建前准备

- **.NET SDK**：10.0（`dotnet --version` 可验证）
- **工作负载**：无需单独安装，WinUI 3 与 Windows App SDK 通过 NuGet 包还原
- **平台**：仅支持 Windows x64（项目已固定 `win-x64`）

### 构建

> \[!NOTE]
> 平台已在项目中固定为 `win-x64`，无需额外指定 `-p:Platform`。所有编译与打包产物都统一输出到 `bin\AnyCPU\{Configuration}`，不会再生成额外的 `bin\Debug` / `bin\Release` 根目录。

在项目目录（`ClassDock.csproj` 所在目录）执行：

```powershell
# Debug / Release 都会同时产出启动器 + 核心，以及 full / self-contained 两个变体
dotnet build -c Release
```

产物为两个互不覆盖的目录：

> \[!IMPORTANT]
> 变体名和 `SelfContained` 参数集中在 `Packaging\Variants.props`，以后要交换命名只改这一处。
> 调试功能默认值按构建配置区分：Debug 构建默认开启「测试与调试」，Release 构建默认关闭；已存在的 `Config.json` 仍会尊重用户设置。

| 变体               | 发布目录                                                     | 主程序         | 目标机要求                                |
| ---------------- | -------------------------------------------------------- | ----------- | ------------------------------------ |
| `full`           | `bin\AnyCPU\Release\dist\ClassDock-{版本}-full\`           | 框架依赖        | 需安装 **.NET 10 Desktop Runtime（x64）** |
| `self-contained` | `bin\AnyCPU\Release\dist\ClassDock-{版本}-self-contained\` | 自带 .NET 运行时 | 无需安装运行时                              |

两个变体的目录布局一致：

```
ClassDock-{版本}-{变体}\
├─ ClassDock.exe          ← 启动器（自带运行时，不依赖目标机 .NET）
├─ App-{版本号}\           ← 主程序、运行库、.pri/.xbf/Assets
└─ data\                  ← 首次运行生成：Config.json / logs / Backup
```

启动器优先使用 NativeAOT（自带运行时，任何机器都能先起来）。本机 AOT 工具链（VS C++ 生成工具 + Windows SDK）不完整时，回退为 self-contained + trimming + 单文件（当前仓库即此方案，约 13 MB）；工具链补齐后把 `Launcher\ClassDock.Launcher.csproj` 里的 `PublishAot` 改回 `true` 即可。

### 运行

```powershell
# 通过启动器运行（推荐，会自动选择最新的 App-{版本号}）
.\bin\AnyCPU\Release\dist\ClassDock-{版本}-self-contained\ClassDock.exe
```

### 常见问题

- **构建报 MSB3027（文件被占用）**：ClassDock 正在运行，先结束进程再构建：
  ```powershell
  Stop-Process -Name ClassDock,ClassDock.Desktop
  ```
- **启动后看不到白条**：确认没有其他置顶窗口遮挡屏幕右缘，并检查分辨率缩放（白条按 DPI 自适应，支持 Per-Monitor V2）。

## 版本之旅

### 起行前的准备 **Exploration 开拓**

- [x] 0.1 PaintingTheWorld 绘世

### 第一站 **Amphoreus 翁法罗斯**

- [ ] v1.0 **HelloWorld! 你好,世界!**

## 贡献

感谢你对 ClassDock 的关注！由于本项目目前由个人利用业余时间维护，代码审阅与合并通常在**周末**集中处理，回复可能会有延迟，感谢你的理解与耐心。

### 提交 Issue

提交 Issue 前，建议先搜索是否已有类似问题。如果是 Bug 反馈，请尽量提供：

- 操作系统版本；
- ClassDock 版本号；
- 复现步骤；
- 预期行为与实际行为；
- 相关截图或日志。

如果是功能建议，可以说明使用场景、期望效果，以及是否会影响现有功能。

### 代码贡献流程

为了保证代码质量与项目方向的一致性，提交 Pull Request 前请遵循以下流程：

1. **先沟通，后编码**：对于新功能或较大的改动，建议先提交 Issue 讨论方案。未经沟通的大型 PR 可能因架构不符而被拒绝。
2. **Fork 与分支**：Fork 本仓库，并基于 `main` 分支创建你的特性分支（如 `feature/xxx` 或 `fix/xxx`）。
3. **编写代码**：
   - 保持代码风格与现有项目一致；
   - 确保在本地通过 `dotnet build -c Release` 构建成功；
   - 提交信息请使用清晰的描述（如 `fix: 修复橡皮擦光标显示异常`）。
4. **提交 Pull Request**：
   - PR 标题请简明扼要；
   - PR 描述中请关联对应的 Issue（如 `Closes #123`），并说明改动内容与测试情况。
5. **等待审阅**：维护者会在周末集中审阅 PR，可能会提出修改建议，请根据反馈进行迭代。

### 开发准备

如果你希望参与开发，需要安装以下环境：

- **.NET SDK 10.0**
- **Visual Studio 2022**（或具备 C# 开发环境的 IDE）
- **Windows 10 1809+ / Windows 11**

## 许可证

ClassDock 基于 GPL-3.0 开源，详见 [LICENSE](./LICENSE)。
