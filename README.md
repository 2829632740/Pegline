# Pegline（Windows 版）

把截图挂在屏幕顶上的一根晾衣绳上：鼠标顶到屏幕上沿，绳子滑下来；移开，绳子收回去。

这是 [Tendedero](https://github.com/alejandrobujan/tendedero)（作者 Alejandro Buján，仅支持 macOS）
在 Windows 上的移植。Tendedero 的 LICENSE 规定代码是 MIT，但**名字和图标不在授权范围内，修改版必须换名、换图标**，
所以这里叫 Pegline，图标是重新画的（深绿底、一根绳、一个小金属夹夹着一张照片）。

## 运行

```bash
build.cmd
```

产物是 `build/Pegline.exe`，单文件、约 90 KB，双击即可运行，常驻在右下角托盘。

**零依赖**：用的是 Windows 10/11 自带的 .NET Framework 4.8 和它附带的 C# 编译器（`csc.exe`），
不需要装 Visual Studio、.NET SDK 或任何东西。拷到别的 Windows 10/11 电脑上也能直接运行。

**多屏不同缩放**：按显示器分别处理 DPI（Per-Monitor V2，见 `src/app.manifest`）。笔记本 150% 加外接屏 100% 这种组合，
两块屏幕上都按各自的缩放清晰绘制，绳子、图片、点击和拖拽的位置都对得上。

## 操作

| 操作 | 效果 |
|:--|:--|
| 鼠标悬停在图片上约 0.35 秒 | 在图片下方显示大图预览；移开、点击、右键、拖拽或收起绳子时关闭，不抢焦点 |
| 单击 | 复制图片（同时带图片和文件，粘贴进聊天软件、Word、文件夹都行） |
| 长按 | 用画图打开编辑，保存后绳子上的缩略图自动刷新 |
| 双击 | 用默认看图软件打开 |
| 拖进某个应用 | 发一份副本，图还留在绳子上 |
| 拖进文件夹 / 桌面 | 文件移过去保存，图离开绳子 |
| 拖进回收站，或点左上角的叉 | 扔掉 |
| 右键 | 复制 / 打开 / 编辑 / 在资源管理器中显示 / 保存到桌面 / 丢弃 |
| 鼠标顶在屏幕最上沿约 0.3 秒 | 在那块屏幕上放下绳子 |
| 点击屏幕最上沿（标签页、标题栏） | 收起绳子 |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>T</kbd>，或左键点托盘图标 | 显示 / 收起 |
| <kbd>Ctrl</kbd>+<kbd>Alt</kbd>+<kbd>S</kbd>（可在托盘菜单「截图快捷键」里改） | 截图，截完自动挂上绳子 |

## 截图从哪来

Pegline 不自己实现截图，用的是 Windows 自带的截图界面：

- **Ctrl+Alt+S**（默认，可改）：打开系统截图框选界面（和 Win+Shift+S 同一个），截的图**一定会挂上绳子**，
  不需要打开「接管截图」。只认按下快捷键后一分钟内的那一张，不会顺带开始读你的剪贴板。
  默认值故意避开了 QQ（Ctrl+Alt+A）、微信（Alt+A）、钉钉和飞书（Ctrl+Shift+A）的截图键；
  想换成别的，在托盘菜单「截图快捷键」里直接按下新组合，被别的程序占用时会提示。

也可以照常用原来的快捷键：

- **Win+PrtScn**：Windows 会把文件存到「图片\Screenshots」，Pegline 监听这个文件夹（位置改过也能找到）。
- **Win+Shift+S、PrtScn**（只进剪贴板，不存文件）：首次启动会问你要不要「接管截图」。
  同意后，剪贴板里新出现的截图会存进 `%LOCALAPPDATA%\Pegline\Screenshots` 并立刻挂上；
  不想要的点叉会进回收站，想留的拖进文件夹或右键「保存到桌面」。随时可以在托盘菜单里关掉。
  托盘菜单「清空截图文件」会把这个文件夹里的截图全部移进回收站（先确认，绳子上对应的图一起取下）；
  「全部取下」只取下绳子上的图，不删文件。

为了不把你在浏览器、文档里复制的图片误当成截图，只有剪贴板里**只有图片**（没有 HTML、文字、文件）时才会接管。
同一张截图同时出现在剪贴板和 Screenshots 文件夹时（Win+PrtScn、Win11 截图工具都会这样），只挂一张，也只留一份文件：
保留 Windows 存在 Screenshots 里的那份，Pegline 自己存的副本会删掉。判断依据是几秒内先后到达、而且画面内容相同，
所以连按两次截到的两张不同截图不会被误当成重复。

## 和 Mac 版的差异

| Mac 版 | Windows 版 | 原因 |
|:--|:--|:--|
| 停在菜单栏 | 顶在屏幕最上沿（任务栏放在顶部时就是任务栏那一条） | Windows 没有顶部菜单栏 |
| 长按打开系统「标记」编辑器 | 长按用画图打开 | Windows 没有对应的系统编辑器 |
| 接管模式改系统截图设置（关浮动缩略图、改保存位置），退出时还原 | 接管模式读剪贴板，不改任何系统设置 | Win+Shift+S 默认只进剪贴板；不改系统设置也就不需要还原 |
| 截图文件记录了截取区域，截图从原处飞上绳子 | 按图片尺寸推断：整屏 / 当前窗口 / 以松开鼠标处为右下角的区域；推断不出就直接落下 | Windows 不在文件上记录截取区域 |
| 毛玻璃相框 | 半透明白色相框（不做背景模糊） | WPF 不支持对窗口后面的内容做实时模糊 |
| 拖拽时系统画缩略图 | Pegline 自己画一个跟随鼠标的半透明缩略图 | Windows 普通文件拖拽不带预览 |
| 系统音效 | 内置合成的三个短音效 | 不依赖系统音效文件 |
| 英语、西班牙语 | 英语、西班牙语、中文（跟随系统语言） | |

全屏应用（视频、游戏、演示）在前台时绳子不出现。

## 数据放在哪

- 设置：`%APPDATA%\Pegline\settings.ini`，挂着的图片列表：`%APPDATA%\Pegline\pegged.txt`
- 接管下来的截图：`%LOCALAPPDATA%\Pegline\Screenshots`
- 出错日志：`%APPDATA%\Pegline\error.log`；在同目录建一个空文件 `trace.on` 会额外记录 `trace.log`（剪贴板、拖拽事件）
- 开机启动：写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`

没有账号、没有联网、没有统计。

## 代码结构（`src/`）

| 文件 | 作用 | 对应 Mac 版 |
|:--|:--|:--|
| `App.cs` | 托盘、快捷键、绳子何时放下 / 收起、截图飞上绳子 | `AppDelegate.swift`、`main.swift` |
| `LineWindow.cs` | 屏幕顶部那条透明、不抢焦点、只在图片上接收鼠标的窗口；绳子本身 | `LinePanel.swift`、`LineView.swift` |
| `CardView.cs` | 一张图：相框、夹子、摆动、微风 | `PeggedView.swift` |
| `Gestures.cs` | 单击、长按、拖拽、右键菜单、拖拽预览 | `GrabArea.swift` |
| `HoverPreview.cs` | 悬停大图预览、异步加载和多屏预览位置 | |
| `Line.cs` | 挂着什么、每张图能做什么、持久化 | `Line.swift`、`Markup.swift` |
| `Watchers.cs` | 监听 Screenshots 文件夹、接管剪贴板截图、全局快捷键 | `ScreenshotWatcher.swift`、`Inbox.swift`、`HotKey.swift` |
| `Flight.cs` | 截图飞上绳子、丢弃时掉落、全屏检测 | `CaptureFlight.swift`、`FullScreen.swift` |
| `Support.cs` | 多语言、设置、音效、弹簧动画 | `Localization.swift` |
| `Shortcut.cs` | 快捷键的保存格式、「截图快捷键」设置窗口 | |
| `AppIcon.cs` | 用代码画的图标（托盘和 exe 共用） | `scripts/make-icon.swift` |
| `Native.cs` | Win32 调用 | |

动画没有用 WPF 的 Storyboard，而是每帧手工积分弹簧（`Spring`），参数直接对应原版 SwiftUI 的
`spring(response:dampingFraction:)` 和 `interpolatingSpring(stiffness:damping:)`，所以手感和原版一致。
