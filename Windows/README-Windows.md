# VideoScopePad · Windows 版（开发中）

同一个软件的第二平台实现：**C# / .NET 8 + WPF + Vortice(D3D11/HLSL) + Media Foundation**。
算法、布局规范、界面设计与踩坑经验全部沿用 iPad 版（`../VideoScopePad/`），代码是新写的。

## 为什么 Windows 版反而更好做

| | iPad 版 | Windows 版 |
|---|---|---|
| 采集 | AVFoundation 外接 UVC（格式信息有限） | **Media Foundation**：能读到 YUV 原始数据 + 视频/全范围标记 + 色彩矩阵，IRE 标定更准 |
| 零拷贝 | CVMetalTextureCache | `MFCreateDXGIDeviceManager` → D3D11 纹理直通 |
| 着色器 | Metal (MSL) | **HLSL**，与 MSL 几乎 1:1 |
| 开发循环 | 本地无 Xcode → 推 GitHub → CI 编译 → 下 IPA → 侧载（一圈 10 分钟起） | **本机 `dotnet build` + 本机跑**，还能离屏出 PNG 自检 |
| 安装 | 未签名 IPA 每 7 天重签 | 一个 exe，双击就跑 |

## 目录结构

```
Windows/
├─ run-app.cmd                 ← **双击启动器**（自动编译 + 开窗口）
├─ VideoScopePad.App/          ← **界面程序**（WPF；双击就是实时监视器）
│   ├─ LiveSession.cs          ← 实时链路：取帧 → GPU 转换 → 示波器 → 合成 → 回读
│   └─ MainWindow.xaml(.cs)    ← 窗口：源选择 + 实时位图 + 状态行（故意做薄）
├─ VideoScopePad.Win/          ← 核心库（不依赖界面，可被自检工具引用）
│   ├─ Core/                   ← 布局与模型（对应 iPad 版 Model/）
│   │   ├─ RectF.cs            ← 轻量矩形（左上角原点、y 向下，与 SwiftUI/CGRect 习惯一致）
│   │   ├─ ScopeModels.cs      ← 格内容 / 示波器种类 / 旋转 / 刻度单位
│   │   └─ ScopeLayout.cs      ← 布局唯一来源（与 ScopeLayout.swift 逐行对应）
│   ├─ Capture/                ← Media Foundation UVC 采集
│   │   ├─ MediaFoundationRuntime.cs ← MFStartup/MFShutdown 的进程级引用计数
│   │   ├─ VideoDeviceEnumerator.cs  ← 设备枚举 + 打开媒体源（含那个 IMFActivate 坑）
│   │   ├─ CaptureDevice.cs          ← 源读取器：原生格式 / 生效格式 / 读帧
│   │   ├─ CaptureFormat.cs          ← IMFMediaType → 格式记录（分辨率/帧率/FourCC/…）
│   │   ├─ VideoColorInfo.cs         ← 量化范围 + 原色/传输函数/矩阵（决定 IRE 标定）
│   │   ├─ MediaSubtype.cs           ← 像素格式 GUID ↔ FourCC 反解
│   │   ├─ MediaAttributes.cs        ← 属性安全读取（缺失不抛）+ 属性集摊开
│   │   ├─ MediaAttributeCatalog.cs  ← GUID ↔ 字段名（反射建表，不用手抄 mfapi.h）
│   │   ├─ YuvFrameConverter.cs      ← YUY2 / NV12 → RGBA8（CPU 版：出 PNG、与 GPU 对拍）
│   │   ├─ YuvFrameEncoder.cs        ← RGB → YUY2 / NV12（造已知码值的测试帧）
│   │   ├─ CapturedFrame.cs          ← 一帧原始码流 + 时间戳
│   │   └─ CaptureRateMeter.cs       ← 实测帧率（按帧时间戳算，不用墙上时钟）
│   └─ Render/                 ← D3D11 渲染
│       ├─ D3DContext.cs       ← 设备 / 离屏目标 / 读回 / 可复用 ReadbackBuffer / 存 PNG
│       ├─ YuvFrameUploader.cs ← YUV 原样上传 + 一趟 compute 转 R'G'B'（GPU 版）
│       ├─ PngWriter.cs        ← 极简 PNG 编码（不依赖 System.Drawing / WIC）
│       ├─ SyntheticSource.cs  ← 合成测试信号（静态帧 + SyntheticLiveSource 动态叠层）
│       └─ Shaders/            ← HLSL（金属着色器逐行移植 + ConvertShaders 新写）
└─ tools/
    ├─ render-selfcheck/       ← 离屏自检：不开窗口渲染并存 PNG
    ├─ mf-capture/             ← 采集自检：list / formats / capture / gpu / probe
    └─ compile-shaders/        ← 只编译 HLSL，专门用来抓语法/绑定点错误
```

## 本机开发循环

```powershell
# 编译
dotnet build Windows\tools\render-selfcheck\render-selfcheck.csproj

# 离屏自检（输出 PNG + 逐点核对像素）
dotnet run --project Windows\tools\render-selfcheck -- out\selfcheck.png 1920 1080

# 只编着色器
powershell -File Windows\tools\compile-shaders.ps1

# 采集自检（三步走，每步一条命令一个验收点）
dotnet run --project Windows\tools\mf-capture -- list        # ① 枚举设备
dotnet run --project Windows\tools\mf-capture -- formats     # ② 原生格式 + 生效格式 + 色彩元数据
dotnet run --project Windows\tools\mf-capture -- capture 1   # ③ 采 1 秒 + 存 capture-frame.png
dotnet run --project Windows\tools\mf-capture -- gpu         # ④ 采集帧 → GPU 转换 → 示波器（对拍 CPU 版）
dotnet run --project Windows\tools\mf-capture -- probe       # 诊断：三条打开设备的路都试一遍

# 界面程序：双击就是实时监视器；也可以无窗口自检（我这边看不到窗口，靠它验收）
Windows\run-app.cmd                                    # ← 双击这个（自动编译再开窗口）
dotnet build Windows\VideoScopePad.App\VideoScopePad.App.csproj
Windows\VideoScopePad.App\bin\Debug\net8.0-windows\VideoScopePad.App.exe
Windows\VideoScopePad.App\bin\Debug\net8.0-windows\VideoScopePad.App.exe `
    --snapshot Windows\out\live.png --frames 240 --source synthetic --width 1920 --height 1080
```

**不需要装 Visual Studio**：.NET 8 SDK 自带 WPF 与 HLSL 所需的一切，
着色器用系统的 `d3dcompiler_47.dll`（Vortice.D3DCompiler 封装）编译。

## 自检策略（重要）

渲染结果的正确性用**数值断言**保证，而不是"看着差不多"：

* 合成信号是已知码值的图案（75% 彩条 = 191、PLUGE 0 IRE = 0、灰阶斜坡单调递增），
  上传/读回/通道顺序都能逐点核对；
* 示波器接进来之后，直方图回读到 CPU，断言「75% 白条的亮度峰在 bin 191」
  「彩条落在矢量图的 75% 目标框里」这类**可计算**的结论；
* 出图 PNG 同时留一份给人看（`Windows/out/`）。

## 当前进度

- [x] 工程骨架 + 本机编译链路（.NET 8 / Vortice / HLSL 编译）验证通过
- [x] 布局系统移植（含 fillFactor / 刻度栏 / 单位空间 / 旋转），四分割不重叠、矢量绘图区正方形都有断言
- [x] 离屏出图链路：D3D11 设备（RTX 4060，Level_11_1）→ 纹理上传 → 读回 → PNG ✅
- [x] 合成测试信号（SMPTE 75% 彩条 + 蓝条 + PLUGE + 灰阶斜坡）
- [x] HLSL 示波器着色器移植（14/14 入口点编译通过：7 compute + 1 VS + 6 PS）
- [x] 示波器引擎（GPU 直方图 + 归一化 + 测量回读）—— 波形 / 矢量 / 钻石 / 马蹄全部算对
- [x] 合成渲染器（画面 + 面板底色 + 轨迹 + 逐格 scissor 裁剪）
- [x] Media Foundation 采集：设备枚举 / 原生格式 + 色彩元数据 / 1 秒采集出 PNG
- [x] 采集帧接进示波器链路：YUV 原样上传 GPU + compute 转 R'G'B'（与 CPU 版逐像素对拍）
- [x] **WPF 实时窗口**（源选择：合成信号 / 采集卡 / 摄像头；四分割实时显示）
- [ ] 刻度栏覆盖层（IRE 数字、色标框，按 iPad 版做法叠在窗口上）
- [ ] 冻结参考层 + 幅度读数
- [ ] LUT（.cube 解析 + 1D/3D 纹理）
- [ ] 音频套件（WASAPI；本机采集卡的音频功能是 UT-AUD 00K0601910）

## 自检现状（48 项全绿）

`dotnet run --project Windows\tools\render-selfcheck -- out 1920 1080` 会依次验证：

1. **合成信号像素**（8 项）：上传/读回无偏移、通道不交换
2. **直方图落点**（14 项）：75% 白条落在 bin 191（样本数 198 000 vs 白条面积 197 280）；
   7 条彩条各自落在用**同一套 BT.709 Cb/Cr 公式**算出的矢量 bin
   （黄(32,136) 青(149,32) 绿(54,40) 品红(201,215) 红(106,223) 蓝(223,119)）；
   钻石图灰阶正中竖线 2 432 589 样本（一像素写两菱形，总量正好 2×）；马蹄图 1 895 820 样本
3. **示波器纹理**（5 项）：波形第 64 行（= 191 bin）平均 255.0、第 200 行为 0.0；
   矢量纹理 7 个亮点都在对应 bin（bin → 纹理 y 翻转正确）
4. **布局**（10 项）：绘图区/画面区都在格子内、矢量绘图区在盒空间是正方形、格子两两不重叠
5. **整机合成**（10 项）：画面格是白条 (191,191,191)；波形格白条那几列的纵向剖面峰值落在
   191 IRE 对应行（±4 px）且有 123 个亮列；矢量图 7 个亮点；Parade 三列都有轨迹

出图在 `Windows/out/`：`composite-quad.png`（四分割整机）、`scope-*.png`（6 种示波器纹理）。

⚠️ 写断言时注意：着色器用的是 **int() 向零截断**，断言里用四舍五入会差 1 个 bin；
CIE 统计别用步长抽样（会正好踩空）。

## 采集链路（Media Foundation）—— 实测记录

三轮验收都跑过了（本机 UT-VID 00K0601910 + 内建摄像头），结论固化在这里，下次不用重跑：

### ① 设备枚举（`mf-capture list`，1 项断言）

本机 6 台视频采集设备（顺序不保证稳定，**一律按名字/符号链接定位，不要写死序号**）：

| # | 名字 | 说明 |
|---|---|---|
| 0 | Integrated Camera | 内建摄像头（NV12 为主） |
| 1–4 | NDI Webcam Video 1–4 | NDI 虚拟摄像头（占位，别误选） |
| 5 | **UT-VID 00K0601910** | 采集卡，`vid_1f6a&pid_15ae`，USB UVC |

属性键在 **`Vortice.MediaFoundation.CaptureDeviceAttributeKeys`**，是**裸 `Guid` 字段**
（不是 `MediaAttributeKey<T>`），与 C 宏一一对应：

```
SourceTypeVidcap                    8AC3587A-4AE7-42D8-99E0-0A6013EEF90F  ← MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID
SourceType                          C60AC5FE-252A-478F-A0EF-BC8FA5F7CAD3
SourceTypeVidcapSymbolicLink        58F0AAD8-22BF-4F8A-BB3D-D2C4978C6E2F
SourceTypeVidcapCategory            77F0AE69-C3BD-4509-941D-467E4D24899E
SourceTypeVidcapHwSource            DE7046BA-54D6-4487-A2A4-EC7C0D1BD163
FriendlyName                        60D0E559-52F8-4FA2-BBCE-ACDB34A8EC01
```

枚举入口是 `MediaFactory.MFEnumVideoDeviceSources()` → `IMFActivateCollection`（可 foreach、可 Dispose）。

### ② 格式与色彩元数据（`mf-capture formats`，12 项断言）

UT-VID 卡给 **128 条原生格式，全部是 YUY2（未压缩）**：最大 1920×1080@60，
另有 1440×1080 / 1024×576 / 960×540 / 856×480 / 800×600 / 768×576 / 720×576 /
720×480 / 640×480 / 640×360 / 432×240 / 352×288 / 320×240 / 176×144，
每档 8 个帧率（60 / 59.94 / 50 / 30 / 29.97 / 25 / 15 / 7.5）。**没有 NV12，也没有 MJPG。**

驱动**确实给了**色彩元数据（这正是 Windows 版比 iPad 版强的地方）：

| 属性 | 值 | 含义 |
|---|---|---|
| `VideoNominalRange` | 2 | **16–235（limited / 视频范围）** |
| `VideoPrimaries` | 2 | BT.709 |
| `TransferFunction` | 缺失 | 未声明（按 BT.709 处理） |
| `YuvMatrix` | 2 | **BT.601** ← 1080p 报 601，见下 |
| `VideoLighting` | 3 | Dim |
| `DefaultStride` | 3840 | = 1920×2，YUY2 一行正好 2 字节/像素 |

⚠️ **HD 分辨率却声明 BT.601 矩阵**：廉价 UVC 卡的常见默认填法。矢量图的 Cb/Cr 解码
若照声明走 601，7 条彩条的落点会整体偏一点（601 的红是 (81,90,240)，709 是 (63,102,240)，
差 18 个码值）。**等接上已知彩条信源实测后再拍板**，系数只在一处
（`YuvFrameConverter.Coefficients.Select`），改起来是改一行。

### ③ 采集（`mf-capture capture 1`，14 项断言）

```
生效格式    1920×1080 @ 60 fps  YUY2（自动挑选：优先未压缩 + 最大分辨率）
实测帧率    59.999 fps（偏差 0.0%）—— 61 帧 / 1.000 秒
帧间隔      最小 16.66 ms / 最大 16.68 ms / 平均 16.67 ms（抖动 ±0.02 ms，等时传输很稳）
行跨距      3840（= width×2，驱动没补行），缓冲 4 147 200 字节
```

内建摄像头（对照，走 NV12 路径）：1280×720@30，实测 29.807 fps（-0.6%），
行跨距 1280 但**缓冲 1080 行 = 高 × 1.5**（UV 交织平面跟在 Y 后面）。

> ⚠️ **两张卡现在都给出「整幅均匀」的画面**：采集卡 Y 全部 = 16（limited 黑电平，
> 即 HDMI 没接信号源 / 源在输出黑场），摄像头 Y 全部 = 12（full range 下接近纯黑）。
> 管道本身已验证正确（见下面的自证断言），**换一个有画面的信号源就能看到内容**；
> `capture-frame.png` 目前因此是 40.9 KB 的纯黑图。

## Media Foundation 踩坑（都复现过，别再踩）

1. 🔴 **不能把 `IMFActivate` 带出 `IMFActivateCollection` 的作用域。**
   集合的 `Dispose()` 会连它交给你的子包装一起 Dispose —— 带出去的那个 `IMFActivate`
   指针已经变成 0，再 `ActivateObject` 抛的是 **`NullReferenceException`**
   （SharpGen 的 ComObject 在指针为 0 时取 Vtbl 的表现），看着完全不像 COM 错误。
   `mf-capture probe` 三条路实测：集合内 `ActivateObject` ✓ / 带出后 ✗（`NativePointer = 0`）/
   `MFCreateDeviceSource` ✓。**所以打开设备走「按符号链接 MFCreateDeviceSource」**，
   枚举集合只用来取静态信息。
2. **尺寸与帧率是「两个 UInt32 打包进一个 UInt64」**：`MF_MT_FRAME_SIZE` = high 宽 low 高、
   `MF_MT_FRAME_RATE` = high 分子 low 分母。直接 `GetUInt64` 当数值用会得到 8246337209400
   这种数。自检里专门用原始值反解一遍对拍（`MF_MT_FRAME_SIZE 8246337209400 → 1920×1080`）。
3. **`MFNominalRange` 的枚举名是反的**：`Normal(1)` = **0–255**（full）、
   `Wide(2)` = **16–235**（limited）。而且 `Normal` 与 `Range0_255` 是同一个值（别名），
   switch 里同时写会编译报错 —— 判断一律用等值比较。
4. **「属性缺失」与「值为 Unknown(0)」必须分开**：色彩元数据的 0 就是 Unknown，
   驱动不给是常态，这时才能回退到推断（MJPEG→full、未压缩 YUV→limited），
   并且要在界面上标出「这是推断值」。搞混的话 IRE 会差 7.8（16–235 当 0–255 用，
   100 IRE 会读成 92.2 IRE）。
5. **行跨距必须用驱动给的值**（`IMF2DBuffer.Lock2D` 的 pitch），不要用 width×bpp 推；
   而且要**按缓冲实际长度 ÷ 行跨距算行数**：NV12 是 `height × 1.5` 行，按 height 拷就只拿到 Y 平面。
6. **YUV→RGB 用四舍五入，不是向零截断**：`1.164×(180−16) = 190.90 → 191`（75% 白）。
   用截断会得到 190，和合成信号的 191 差 1，采集与合成两条链就对不上。
   ⚠️ 这与「断言用向零截断」那条规矩不冲突：那条说的是**直方图分箱**（着色器 `int()`），
   这里是**像素换算**（业界 libyuv / swscale / GPU `round()` 都是四舍五入）。
   将来把这段搬进 HLSL 时，着色器里必须用 `round()` 而不是 `int()`，否则同样差 1。
7. **同步 `ReadSample` 会阻塞到有帧**，返回 null 只可能是「流 tick / 格式变化」这类无样本调用；
   遇到就继续读，不要当出错（`MF_SOURCE_READERF_ERROR` 才是真错，标志位里查）。
8. **`dotnet run` 的进程当前目录取决于你在哪个目录敲的命令**（不是项目目录）——
   `.NET` 的文件 API 全走进程 CWD，于是同一个命令在两个目录下跑会把图存到两个地方。
   `mf-capture` 改成从当前目录往上找仓库根（认 `Windows\VideoScopePad.Win` 这个标记），
   再拼 `<仓库根>\Windows\out`。

### 采集侧的自证断言（怎么在没画面的情况下证明管道是对的）

没有信号源时画面全黑，但**不能因此说"看不出对错"**。这几条能证明管道是通的：

* 整帧单一码值 → 断言 RGBA **整幅均匀**且等于该码值经同一套系数换算的结果
  （limited 的 16 → (0,0,0)，full 的 12 → (12,12,12)）；
  ⚠️ 别写成「均匀 = 一定纯黑」，full range 下不成立；
* 同时断言 **U/V 都是 128** —— 这一条能区分「真黑场」与「把某个平面读错位了」
  （读错平面会读出 128 而不是 16，或干脆有杂色）；
* 转换器本身用已知码值对拍：16→0、180→191（75% 白）、235→255、
  BT.601 纯红 (81,90,240) → 纯红、BT.709 纯红 (63,102,240) → 纯红，NV12 与 YUY2 各测一遍；
* 帧缓冲长度 = 行跨距 × 缓冲行数；PNG 回读 IHDR 尺寸 = 帧尺寸。

## 实时链路：采集帧 → GPU → 示波器 → 窗口

验收命令：`mf-capture gpu`（18 项断言）+ 界面程序的无窗口自检（4 项断言）。

### 链路长什么样

```
采集卡（YUY2/NV12 原始码流）
   │  IMF2DBuffer.Lock2D 拿到行跨距 → 拷进托管数组（CapturedFrame）
   ▼
UpdateSubresource（rowPitch = 驱动给的行跨距）
   │  YUY2：一张 R8G8B8A8 纹理，每个 texel = (Y0,U,Y1,V) 两个像素
   │  NV12：R8_UNORM ×1（Y 平面）+ R8G8_UNORM ×1（UV 交织平面）
   ▼
CSYuy2ToRgb / CSNv12ToRgb（Compute，一趟约 200 万线程）
   │  系数由 cbuffer 传入（来源 = VideoColorInfo，与 CPU 版同一处选择逻辑）
   ▼
R'G'B' 纹理（R8G8B8A8）
   ├─→ ScopeEngine.Encode（GPU 直方图 → 波形/矢量/Parade 纹理）
   └─→ VideoRenderer.Render（画面格 + 面板 + 轨迹 → 合成纹理）
                                    ▼
                        合成纹理建为 **B8G8R8A8** → 回读 → WPF Bgra32 位图
```

**为什么先转成 RGB 再进示波器**：示波器是 GPU 直方图（compute 读一张纹理累加），
它只认「已经转好的 RGB 纹理」，没法边采样边转。多这一趟换来的是显示与统计一行都不用改，
合成信号那条路用的也是同一张 RGB 纹理。

### 实测数据（RTX 4060 Laptop / Level_11_1）

| 项目 | 结果 |
|---|---|
| GPU 与 CPU 转换对拍 | 1920×1080 YUY2：**最大差 1 LSB**，620 万样本里 0 个差 >1 |
| 已知码值 | 75% 白条 `(191,191,191)`、PLUGE `(0,0,0)`，GPU/CPU 两条链都精确命中 |
| 示波器落点 | 白条样本 **197 280 = 白条面积**（±5% 内），黑电平 bin 0 有样本 |
| 上传+转换耗时 | 1080p YUY2 **4.10 ms/帧**、720p NV12 **0.60 ms/帧**（60 fps 预算 16.67 ms） |
| 窗口实时帧率 | 1920×1080 合成 **144 fps**（6.5 ms/帧：转换 1.6 + 回读 4.9） |
| 摄像头（NV12 实机） | 采集 29.989 fps（声明 30），画面格取样 `(12,12,12)` = 卡的均匀暗场 |
| 采集卡（未插） | 不崩：报「找不到设备」并自动退回合成信号 |

### 实时链路的坑（都实测过）

1. **合成纹理用 B8G8R8A8，不要 R8G8B8A8**。WPF 没有 `Rgba32` 这个像素格式，
   而 D3D 的分量是**按语义**映射的（`.x→R`、`.y→G`、`.z→B`），格式只决定内存排列 ——
   所以让 D3D 直接以 BGRA 输出，着色器照旧写 (R,G,B)，回读出来的字节顺序正好是 WPF 要的，
   界面一层一次 memcpy。否则每帧要在 UI 线程上做 200 万次通道交换（白白几毫秒）。
   ⚠️ 这条要用**非对称颜色**验证：白条抓不到 R/B 互换，得看黄条
   （BGRA 内存里必须是 `(0,191,191)`）。
2. **着色器里别再手动 `round()`/`int()`**：输出目标是 UNORM，硬件写入时按就近取偶自动量化
   （190.90 → 191），与 CPU 版的「+0.5 截断」天然一致。手动截断会得到 190，差 1。
3. **读回要复用 staging 纹理与数组**（`ReadbackBuffer`）：每帧新建 + 每帧 8 MB 分配，
   光 GC 就能吃掉一半帧率。
4. **D3D11 是异步提交**：示波器与合成的 GPU 时间不会记在各自那一项上，
   而是算进之后的 `Map`（回读）—— 看耗时分解时别以为「示波器 0 ms」是没算。
5. **取样断言必须走布局的画面区矩形**：画面格里的视频是等比适配的（可能有留白），
   按帧缓冲比例取样会取到别的彩条上（第一版就这么误报过）。
   用 `ScopeLayout` 里的 `pane.Video` 做「视频像素 → 帧缓冲像素」换算。
6. **合成信号别每帧重建整幅图**：1600×900 每帧重算 144 万像素 ×3 次 `Math.Round` = 37 ms/帧，
   直接掉到 24 fps。静态部分缓存（`SyntheticLiveSource`），每帧只叠动态元素。
7. **界面线程绝不碰 D3D**：渲染线程独占设备，UI 只做「拷贝最新一帧 → WriteableBitmap」，
   并用双缓冲 + 锁而不是队列（监视器永远只要最新帧，追不上就丢帧）。

