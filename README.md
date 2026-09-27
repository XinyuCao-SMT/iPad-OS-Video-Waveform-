# VideoScopePad · iPad 采集卡监视器 + 示波器

把 iPad 变成一台**带示波器的现场监视器**：插上支持 UVC 的 USB 采集卡，就能实时看画面、看矢量示波器 / 亮度波形 / RGB Parade，并给画面套 LUT。

整个工程都在这个仓库里，SwiftUI + AVFoundation + Metal，最低 **iPadOS 17.0**。

> **构建状态：已在 GitHub Actions 上编译通过** ✅
> 工具链 Xcode 26.6 / iPhoneOS 26.5 SDK，产物 `VideoScopePad.app` 为 arm64（iPad mini 6 及以后可用），
> `MinimumOSVersion = 17.0`，设备族 iPad + iPhone，Metal 着色器已编译进 `default.metallib`。
> 未签名 IPA 已下载到本地 `dist/VideoScopePad-unsigned.ipa`，可直接用 Sideloadly 侧载（见 `CLOUD-BUILD.md` 第 7 节）。
> 目前残留两条**无害的废弃警告**：`AVCaptureConnection.isVideoOrientationSupported` /
> `videoOrientation` 在 iOS 17 起被 `videoRotationAngle` 取代（只影响内置摄像头作为备用信号源时的方向设置）。

---

## 1. 功能

| 能力 | 说明 |
| --- | --- |
| 读取 UVC 采集卡信号 | `AVCaptureDevice.DeviceType.external`，自动发现/热插拔；同时保留内置摄像头作为备用信号源 |
| iPad 当监视器 | 零拷贝 `CVMetalTextureCache` + Metal 直接出图，按帧渲染（不空转），端到端延迟最低 |
| 矢量示波器 | Cb/Cr 二维直方图，带 75% 彩条色标框、25/50/75/100% 圆环、肤色线（I 轴 123°）、放大倍率可调 |
| 亮度波形 | 0–100 IRE 刻度，按当前量化范围（视频范围 / 全范围）标注；支持「亮度」与「RGB 叠加」两种模式 |
| RGB Parade | R/G/B 三列并排波形，每列独立 IRE 刻度 |
| 画面套 LUT | 导入 `.cube`（支持 1D shaper + 3D LUT，17³/33³/65³），3D 纹理硬件三线性插值，强度 0–100% 可调 |
| 附加调色 | LUT 之后再叠曝光 / 对比度 / 饱和度 / 伽马，纯观看用「quick look」 |
| 取样位置可选 | 示波器可以测 **LUT 之前**（原始 log / 相机信号）或 **LUT 之后**（显示信号） |
| 信号幅度数值读数 | 峰值白 / 黑位 / 平均电平 / 动态范围（IRE）、R/G/B 分量峰值、色度峰值、超白超黑占比，超范围自动橙色告警；整帧全黑会提示「信号源可能没输出」 |
| 显示通道 | 彩色 / 亮度 / 单独 R / G / B 通道 |
| 布局 | 仅画面、底部示波器条、右侧示波器栏、示波器半透明叠加在画面上；竖屏自动适配 |
| 其它 | 冻结画面读数、画面缩放（完整显示 / 铺满裁切）、HUD 信息层、点击画面全屏监视、防息屏 |

---

## 2. 硬性前提（必须先确认）

1. **iPadOS 17.0 或更高。** 系统从 iPadOS 17 起才通过 AVFoundation 向 App 暴露外接 UVC 视频设备
   （`AVCaptureDevice.DeviceType.external`，参见 WWDC23 *Support external cameras in your iPadOS app*）。
   iPadOS 16 及以前，任何 App 都拿不到采集卡的画面。
2. **必须是 USB-C 接口的 iPad**：iPad mini 6 及以后、iPad Air 4 及以后、iPad Pro 2018 及以后、iPad 10 及以后。
   Lightning 接口的 iPad（含 iPad mini 5）**不支持**外接 UVC。
3. **采集卡本身要免驱（UVC 标准）**，并且输出 iPad 能协商到的格式。绝大多数 HDMI→USB 采集卡都满足，
   推荐以 **1080p60** 为目标（4K 输入能否进由采集卡与 iPad 共同决定，本工程默认按 1080p60 低延迟优化）。
4. **供电**：采集卡 + HDMI 芯片功耗不小，iPad 的 USB-C 口可能供电不足导致掉线或花屏。
   建议用**带外部供电的 USB-C 扩展坞 / Hub**。
5. **HDCP**：带 HDCP 保护的信号源（部分机顶盒、蓝光机、受保护的流媒体）不会通过采集卡输出，
   现象是黑屏或无信号，这不是 App 的问题。
6. 采集卡同一时刻只能被一个 App 占用；如果 FaceTime / 相机 / 其它采集 App 正在用，本 App 会显示「被系统中断」。

---

## 3. 目录结构

```
iPad OS Software Waform/            <- 仓库根目录
├─ .github/workflows/build-ipa.yml  <- 在 GitHub 云 Mac 上编出未签名 IPA
├─ README.md                        <- 本文档（总览）
├─ CLOUD-BUILD.md                   <- 云端编译 + Windows 侧载的详细步骤
├─ NEXT-STEPS.md                    <- 已冻结的待办（等你发话再做）
├─ .gitignore / .gitattributes      <- 仓库卫生（换行统一 LF，排除构建产物）
└─ VideoScopePad/                   <- Xcode 工程目录
   ├─ VideoScopePad.xcodeproj/      <- 已生成，可直接用 Xcode 打开
   ├─ project.yml                   <- XcodeGen 备用方案
   ├─ tools/generate-xcodeproj.mjs  <- 增删源文件后重新生成 .xcodeproj
   ├─ tools/check-sources.mjs       <- Windows 上的静态自检（配平/重名/共享宏/ViewBuilder 上限）
   └─ VideoScopePad/
      ├─ App/        VideoScopePadApp.swift · AppSettings.swift
      ├─ Capture/    CaptureController.swift（UVC 设备/格式/帧回调）· CaptureModels.swift
      ├─ Model/      ScopeModels.swift（枚举）· ScopeLayout.swift（布局唯一来源）· GeometryHelpers.swift
      ├─ Render/     MetalContext · VideoRenderer · ScopeEngine · SignalMeasurement（幅度读数）· LUTCube · LUTTextures · LUTStore · RenderCoordinator
      ├─ Views/      ContentView · MonitorSurface · ScopeGraticuleView · HUDOverlay · ControlBar · SettingsSheet · Controls
      ├─ Shaders/    ShaderTypes.h（Swift/Metal 共享）· DisplayShaders.metal · ScopeKernels.metal · 桥接头
      └─ Resources/  Assets.xcassets
```

---

## 4. 构建与安装

### 路线 A：有 Mac（推荐）

```bash
open VideoScopePad/VideoScopePad.xcodeproj
```

1. 选中 target **VideoScopePad** → *Signing & Capabilities* → 勾上 **Automatically manage signing**，选自己的 Apple ID Team。
2. 顶部设备选你的 iPad（先用数据线连上并在 iPad 上「信任此电脑」）。
3. `⌘R` 运行。首次运行后，iPad 上「设置 → 通用 → VPN 与设备管理」里信任该开发者。

免费 Apple ID 也能装，但签名 **7 天过期**，过期后重新运行一次即可；付费开发者账号（99 美元/年）签名为 1 年。

### 路线 B：只有 Windows（GitHub Actions 云 Mac 编译 + Windows 侧载）

Windows 上**无法**编译 iOS/iPadOS 应用（需要 Apple SDK 与 Metal 着色器编译器，只能在 macOS 上跑）。
但你完全不需要买 Mac。**完整保姆级步骤见 [`CLOUD-BUILD.md`](CLOUD-BUILD.md)**（含注册建仓、
三种推代码方式与登录认证、触发构建、看日志、下载产物、Sideloadly 侧载、开发者模式、
失败速查表、隐私注意事项）。这里只给最短路径：

**第 1 步 · 推到 GitHub**

把整个仓库目录（含 `.github/`、`VideoScopePad/`）推到你的 GitHub 仓库。
公开仓库构建**完全免费不限量**；私有仓库 macOS 虚拟机按 10 倍计费（免费额度约 200 分钟/月）。

**第 2 步 · 云端编译**

仓库 → **Actions** → 左侧 **Build unsigned IPA** → **Run workflow**（分支选 `main`，配置选 `Release`）。
4–6 分钟后，在该次运行底部 **Artifacts** 里下载 `VideoScopePad-unsigned-ipa`，解压得到
`VideoScopePad-unsigned.ipa`（这是**未签名**的，必须自己签才能装）。
构建失败时页面顶部 Summary 会直接列出 `error:` 摘要，另附完整 `build-log` 工件。

**第 3 步 · 在 Windows 上侧载到 iPad**

1. 安装 iTunes（**Apple 官网下载版，不要用 Microsoft Store 版**，它提供 Apple 设备驱动）。
2. 下载 [Sideloadly](https://sideloadly.io/)（或 [AltStore](https://altstore.io/)）。
3. iPad 用数据线连电脑，iPad 上点「信任此电脑」并输入锁屏密码。
4. **iPadOS 16+ 必须先开「设置 → 隐私与安全性 → 开发者模式」并重启 iPad**（首次侧载后这个开关才会出现）。
5. 把 `VideoScopePad-unsigned.ipa` 拖进 Sideloadly，**Apple ID 填自己的**（免费账号即可），Start。
6. iPad 上「设置 → 通用 → VPN 与设备管理 → 开发者 App」里点信任，然后就能打开。

免费 Apple ID 的限制：**每 7 天要重新签一次**，同时最多 3 个自签 App。过期后重新侧载即可，
**IPA 不用重新编译**。想省事可以用 AltStore + AltServer（装在 Windows 上，同一 Wi-Fi 下自动续签）。

> 注意：这里不涉及任何企业证书或越狱。整套流程就是「你自己给自己签一个 App」。
> 你的 Apple ID 只交给本机的 Sideloadly，不会经过 GitHub。

---

## 5. 使用说明

1. 插上采集卡 → 打开 App → 顶部出现「UVC 已连接」。
2. 底栏左起：**设备菜单**（选采集卡，会标注「内置 / UVC」）→ **格式菜单**
   （列出该设备所有可用「分辨率 + 帧率 + 像素格式」，默认优先 1080p60）。
3. 中间三个按钮是示波器开关：**矢量 / 波形 / RGB**；旁边「波形：亮度 / RGB 叠加」切换波形模式，
   「取样 LUT 前 / LUT 后」决定示波器测的是哪一段信号。
4. **布局菜单**：仅画面 / 底部示波器 / 右侧示波器 / 叠加在画面上；旁边是显示通道（彩色 / 亮度 / R / G / B）。
5. **LUT 菜单**：导入 `.cube` → 选中即生效 → 「LUT 开 / 关」总开关；编辑区里的 **LUT 强度** 做混合。
   也可以把 `.cube` 直接放进「文件」App → 本应用 → `LUTs` 目录（App 目录已开启文件共享），再回来点刷新。
6. **调色**按钮展开曝光 / 对比度 / 饱和度 / 伽马；**冻结画面**按钮让你有时间慢慢读示波器读数。
7. 点一下画面区域可以隐藏所有 UI，得到干净的全屏监视画面；再点一下恢复。
8. 设置面板（底栏最右）里有全部参数、示波器精度、LUT 管理、输入信号详情。

### 示波器怎么读

* **IRE 刻度**按当前量化范围标注：视频范围（16–235）时 0 IRE 对应码值 16、100 IRE 对应 235；
  如果是全范围（0–255）则 0/100 IRE 就是 0/255。
* **波形**横轴 = 画面水平位置，纵轴 = 亮度（或 R/G/B）码值，越亮说明该码值的像素越密。
* **矢量示波器**横轴 = Cb(B-Y)、纵轴 = Cr(R-Y)。75% 彩条的六个目标框和 75% 圆环用于客观对色，
  **肤色线**（左上那条橙色虚线）是所有肤色的落点参考线 —— 白平衡偏了，肤色的点会明显偏离这条线。
  「矢量图放大」只放大显示，不改变数据。
* 示波器默认统计的是 **LUT 之后**的显示信号；如果你在给 log 素材套 LUT，想监看原始信号，
  就把取样切到 **LUT 之前**。
* 「统计精度」默认 1/2 采样（每 2×2 像素取一个），1080p60 下完全够用；追求极致可切「精确（每像素）」，
  旧机型上若掉帧再退回。

### 信号幅度能测到什么程度

底栏「读数」按钮（或设置里的「显示信号幅度数值读数」）会打开右上角的数值面板，每秒更新约 10 次：

| 显示项 | 含义 |
| --- | --- |
| 峰值白 | 稳定白电平（从最高码值往下累计到 0.1% 像素处），去掉了噪点干扰，判断白电平是否落在 100 IRE |
| 最高码值 | 绝对最高码值，含噪点，仅作参考 |
| 黑位 | 稳定黑电平，用来判断黑场是被压（<0 IRE）还是被抬高（>8 IRE） |
| 平均 | 整帧平均电平 |
| 动态范围 | 峰值白 − 黑位 |
| R / G / B | 三个分量各自的稳定峰值，用来查是不是某个分量单独超范围 |
| 色度峰值 | 从矢量图半径换算的饱和度百分比，>100% 即超出 BT.709 边界 |
| 超白 / 超黑 | 高于 100 IRE / 低于 0 IRE 的像素占比 |
| 警告行 | 命中异常时用橙色列出，例如「整帧全黑」「超白 0.32%」「白电平偏高 106 IRE」 |

**这一层是「数字码值幅度」，不是模拟电压。** 边界要说清楚：

* ✅ 能测：数字化之后的 R'G'B' / Y' 码值幅度、IRE 电平、峰值与黑位、超白超黑占比、
  各分量峰值、色度饱和度峰值、以及"有帧但整帧全黑"（大概率信号源没输出或被 HDCP 挡住）。
* ❌ 测不到：采集卡输入端的**模拟电压幅度**（0.7Vpp、同步头 300mV）、HDMI TMDS 电平、
  以及「采集卡的 HDMI 输入是否丢信号」这个硬件状态位 —— UVC 协议只把**已经数字化、
  自动钳位和自动增益之后**的画面交给系统，电平检测和信号丢失标志都不在 AVFoundation 的接口里。
  想要那一层只能上硬件波形监视器。
* 读数的取样点和示波器一致（跟着「取样 LUT 前 / LUT 后」走），所以面板上直接标了当前取样位置。
* 为了几乎不增加 GPU 开销，测量是**每 6 帧做一次、每 4×4 像素取 1 个样本**，
  反正电平读数不需要逐像素精度；需要极限精度时把「统计精度」调成「精确（每像素）」，
  波形那边会逐像素统计，读数面板仍按上述低频节奏刷新。

---

## 6. 技术实现要点

* **零拷贝采集**：`AVCaptureVideoDataOutput` 直接要 `420v/420f` 双平面输出（原生是非压缩双平面就不做转换，
  否则退化为 BGRA，MJPEG 采集卡由系统解码），再用 `CVMetalTextureCache` 把 `CVPixelBuffer` 直接映射成
  `r8Unorm` + `rg8Unorm` 纹理，全程不经过 CPU。
* **按帧渲染**：`MTKView` 设为 `isPaused + enableSetNeedsDisplay`，每来一帧才画一帧，
  既不浪费 GPU，也没有额外排队延迟。
* **色彩**：YUV→R'G'B' 用 BT.601 / BT.709 / BT.2020 矩阵（按键帧的 `ColorPrimaries` 扩展自动选择），
  量化范围自动展开；输出层标注 sRGB，让系统做显示色彩匹配。
* **示波器**：一个 compute kernel 对中间纹理做原子累加，得到
  4 个平面（R/G/B/Y）× 512 列 × 256 bin 的波形直方图 + 256×256 的 Cb/Cr 直方图；
  再归一化成纹理，用加法混合画成经典「辉光」轨迹。
* **LUT**：`.cube` 解析后上传成 `rgba16Float` 的 **3D 纹理**，靠硬件三线性插值一次完成三轴插值
  （没有 2D 图集的接缝问题）；`.cube` 里的 1D shaper LUT 会用 N×1 纹理先做一次整形；
  未加载 LUT 时绑定恒等占位纹理，避免空绑定。
* **幅度读数**：GPU 侧只往一块 4KB 出头的全局直方图（4 通道 × 256 bin + 64 bin 色度半径）里做原子累加，
  CPU 侧回读后算峰值白/黑位/平均值/超范围占比/色度峰值 —— 不需要在 GPU 上做 min/max 原子操作，
  也不需要把整帧读回 CPU；双缓冲保证读写不打架，采样降到每 6 帧一次，性能影响可以忽略。
* **布局单一来源**：`ScopeLayout` 用归一化单位空间算一次，Metal 画轨迹、SwiftUI Canvas 画刻度/
  文字，两者天然对齐；比例计算与像素密度无关，所以点空间和像素空间结果一致。

---

## 7. 已知限制

* 4K 输入：能不能进 4K 取决于采集卡与 iPad；本工程默认按 1080p60 优化（4K 下示波器会自动降采样）。
* HDR / HLG / PQ 输入：目前按 SDR 处理（未做 HLG→SDR tone mapping），10bit P010 也没走专门管线。
* 录制 / 截图 / 波形导出：本版没做（示波器是实时监看用的）。
* 音频：未处理采集卡的音频（HDMI 内嵌音频不输出）。
* 外接显示器输出（把干净画面送到 HDMI）：未做。
* 色域：按 BT.709/sRGB 处理，未做 P3 出图。

---

## 8. 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| 顶栏一直显示「未检测到视频输入设备」 | 采集卡没插好；用了 Lightning 的 iPad；iPadOS < 17；采集卡不是 UVC 免驱；换带供电的 Hub 试试 |
| 有设备但一直黑屏 | 格式没协商成功 → 在格式菜单里换一个（例如换成 `1920×1080 60p · 420v`）；或信号源没输出；或 HDCP |
| 显示「采集被系统中断」 | 采集卡被其它 App 占用（FaceTime、相机、其它采集软件），关掉它们 |
| 画面偶尔卡一下、HUD 里「丢帧」增加 | USB 带宽/供电不足，降低帧率或分辨率；把示波器精度切到 1/4 也能减轻 GPU 负担 |
| 打开 App 提示没有相机权限 | 设置 → 隐私与安全性 → 相机 → 允许「VideoScopePad」 |
| 导入的 LUT 报错 | 只支持 `.cube`；1D 只有 `LUT_1D_SIZE` 也支持；`LUT_3D_SIZE` 建议 ≤ 65；文件编码需为 UTF-8/ASCII |
| 编译报错找不到 `ShaderTypes.h` | 桥接头路径由 `SWIFT_OBJC_BRIDGING_HEADER` 指定（`VideoScopePad/Shaders/VideoScopePad-Bridging-Header.h`），确认工程没有被改过路径 |

---

## 9. 二次开发

* 新增/删除源文件后重新生成工程：
  ```bash
  cd VideoScopePad && node tools/generate-xcodeproj.mjs
  ```
  （不跑也行，直接在 Xcode 里把文件拖进对应分组即可。）
* 在 Windows 上没法编译，但可以先跑两个自检：
  ```bash
  cd VideoScopePad
  node tools/check-sources.mjs     # 括号配平、跨文件重名、Swift/Metal 共享宏、ViewBuilder 子视图上限
  node tools/parse-swift.mjs       # 用 tree-sitter 真语法解析全部 .swift，报 ERROR/MISSING 节点
  node tools/ci-locate-selftest.mjs # 验证 CI 里的工程目录定位逻辑（5 种目录结构）
  ```
  `parse-swift.mjs` 需要先装解析器（一次即可，装完设 `VSP_TS_DIR` 指向该目录）：
  ```bash
  mkdir %TEMP%\vsp-swift-parse && cd /d %TEMP%\vsp-swift-parse
  npm init -y && npm i web-tree-sitter@0.20.8 tree-sitter-wasms@0.1.13
  set VSP_TS_DIR=%TEMP%\vsp-swift-parse
  ```
  注意版本必须配套：`web-tree-sitter` 0.27 载入不了 `tree-sitter-wasms` 的语法 wasm。
  另外它在 Node 24 上退出时会崩一次，**看最后一行结论，别看退出码**。
* 只想改色调映射、加个示波器类型（比如直方图、YCbCr Parade）：
  * 加直方图统计 → `Shaders/ScopeKernels.metal` 里复用现有直方图缓冲区，加一个 normalize kernel；
  * 加一个面板 → `Model/ScopeModels.swift` 的 `ScopePanelKind` 加一个 case，
    `ScopeLayout`、`ControlBar`、`ScopeGraticuleView` 会自动跟着走。
* 想加 HDR：在 `fsVideoBiPlanar` 之后插入 HLG/PQ → 线性 → tone map 的一步即可，
  示波器统计放在 tone map 之后就是标准的 SDR 读数。

---

## 10. 许可

自用工程，随你改。采集卡、LUT 文件、iPad 的商标与版权各归其主。
