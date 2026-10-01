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
├─ build-release.cmd           ← **打包单文件 exe**（dist\VideoScopePad.exe，可单独拷给别人）
├─ VideoScopePad.App/          ← **界面程序**（WPF；双击就是实时监视器）
│   ├─ LiveSession.cs          ← 实时链路：取帧 → GPU 转换 → 示波器 → 合成 → 回读
│   ├─ ScopeGraticule.cs       ← 刻度层（IRE 数字/网格/矢量目标框…，移植自 iPad 版）
│   ├─ GraticuleElement.cs     ← 刻度层的承载元素（自绘，铺在实时位图之上）
│   ├─ LiveSnapshot.cs         ← 「最新一帧 + 刻度」渲染成 PNG
│   └─ MainWindow.xaml(.cs)    ← 窗口：源选择 + 单位 + 实时位图 + 状态行（故意做薄）
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

# 打包成单文件 exe（给别人用；dist\ 不进仓库）
Windows\build-release.cmd                              # → dist\VideoScopePad.exe（约 69 MB）
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
- [x] **刻度层**：IRE/mV/% 刻度栏 + 网格 + 矢量图 75% 目标框 / 肤色线 / B-Y·R-Y 轴
- [x] **幅度读数**：峰值 / 稳定白 / 黑位 / 平均 / 色度峰 / R·G·B / 超白超黑 + 峰值保持游标
- [x] **冻结参考层**：琥珀幽灵叠在实时轨迹上（不透明度可调）+ 参考读数与 Δ
- [x] **单文件发布**：`build-release.cmd` → `dist\VideoScopePad.exe`（69 MB，自包含，可直接分享）
- [x] **发版与回滚**：`release.ps1` → 一版一个文件夹 + `MANIFEST.txt`（SHA256/自检结果）+ tag，
      见 `Windows/ROLLBACK-Windows.md`
- [ ] 格子内容可选（全屏/四分割逐格换示波器；钻石图与马蹄图刻度按 iPad 版补上）
- [ ] 超标报警 / 斑马纹（iPad 版 v1.2.0 有，Windows 还没移植）
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
8. 🔴 **参考层/第二趟绘制必须自己设像素着色器**：参考层那一趟如果继承了上一趟的
   `PSSolidColor`（画面板底色用的），就会拿面板颜色把整个绘图区糊一遍 ——
   现象是「整块区域都变了颜色」而不是「轨迹上多一层琥珀」。
   自检里那条「差异必须全部落在轨迹上」就是专门抓它的，实测抓到过（40 万像素全变红）。
9. 🔴 **自检里 `session.Stop()` 不能提前调**：抓参考是「下一帧」在渲染线程上执行的，
   链路一停请求就永远没人处理（表现是"抓了但没有参考读数"）。
10. **合成信号的动态元素会污染读数自检**：那根 100 IRE 扫掠线会把峰值顶到 100 IRE、
    75% 绿条会把色度峰抬到 89%（两者都会让「峰值 = 75 IRE」这类断言不成立）。
    读数自检要用静态合成图（`AnimateSynthetic = false`）。

## 幅度读数与冻结参考层

* **读数**（`Render/SignalMeasurement.cs`，与 iPad 版 `SignalMeasurementBuilder` 同口径）：
  峰值白 / 稳定白（0.1% 分位）/ 黑位 / 稳定黑 / 平均 / 色度峰 / R·G·B 峰 / 超白超黑占比 / 样本数。
  **峰值与稳定值必须并列**：峰值是最高非空 bin（一个噪声点就能顶到 100 IRE），
  稳定值是掐掉两端 0.1% 的结果 —— 只给一个都会误导。
* **峰值保持**：创新高立即钉住 → 保持 3 秒 → 按 12 IRE/秒衰减（`PeakHoldTracker`）。
  刻度层上实时游标是黄（峰值）/青（黑位）虚线，参考游标是琥珀细虚线并错开位置。
* **冻结参考层**：按下「冻结参考」把那一刻的 6 张示波器纹理整块 `CopyResource` 成参考层
  （GPU 拷贝，无额外 pass），之后**实时轨迹照常刷新**，参考层以琥珀色幽灵叠在下面
  （不透明度可调，实时轨迹恒 100%）。读数也存一份参考，底部读数行给出「参考 峰/均」与「Δ 峰/均」。
* **验收方式**（这两条比"看着对"强得多）：
  · 读数：拿**同一张合成图在 CPU 上另算一份 luma 直方图**对拍 ——
    峰 100.00 vs 100.00、稳 99.22 vs 99.22、黑 0.00 vs 0.00、均 37.20 vs 37.20（CPU 平均码值 94.86）。
  · 参考层：比对「画参考前后」同一区域 —— 差异 5342 个像素**全部落在轨迹上**、
    平均红升 +43.7（琥珀色），清除后归 0。
    ⚠️ 别用「数琥珀像素」：参考层与实时轨迹同一信号时两者完全重合，
    加法混合下绿通道饱和，一条琥珀像素都数不到（实测踩过）。

## 刻度层（IRE 数字、网格、矢量目标框）

逐条移植 iPad 版 `Views/ScopeGraticuleView.swift`，两边共用同一份 `ScopeLayout`，
所以刻度与轨迹严格对齐（`plot` / `gutter` / `panel` 是同一组矩形）。

| 画什么 | 与 iPad 版一致的细节 |
|---|---|
| 侧边刻度栏 | 单位名（IRE/mV/%）+ 刻度线 + 数字；字号跟栏宽走（7–12pt）；栏宽 < 22 只留刻度线 |
| 标注密度 | 绘图区 ≥ 240 px 全标、≥ 130 px 只标主刻度、更矮只留 首/中/末 三档 |
| 波形 / Parade | 每 10 单位一条横线（主刻度加亮）+ 每列 1/4·1/2·3/4 竖线 + Parade 三列分隔与 R/G/B 名 |
| 矢量图 | 25/50/75/100% 圈（75% 加亮）+ 十字轴 + **6 个 75% 目标框**（R/Mg/B/Cy/G/Yl）+ 肤色线（123°）+ B-Y / R-Y 轴名 |
| 出图 | 「存一帧 PNG」= 实时帧 + 刻度层一起渲染（`LiveSnapshot`），所以分享出去的图是完整的 |

### ⚠️ 刻度层踩的坑（都实测过）

1. 🔴 **纵轴必须用「全范围」映射**。本工程在采集入口就把 limited(16–235) 展开成 full(0–255)
   （`YuvFrameConverter`），示波器直方图也按展开后的码值分箱，所以刻度是
   **0 IRE = 码值 0、100 IRE = 码值 255**。若照 iPad 版传 `videoRange = true`
   （0 IRE = 16、100 IRE = 235），所有刻度会整体偏 **8%**，而且画面看着"挺正常"。
   自检里专门钉了一条：`YPositionForIre(0) == 绘图区底、YPositionForIre(100) == 顶`。
2. **判据不能只看绿通道**：刻度层里有白色的目标框标签（灰、绿通道高达 234），
   比轨迹还亮 —— 用「绿通道最亮」去找轨迹，会把标签当成轨迹，
   得出"所有目标都偏 15 px"的假结论。正确判据是**绿明显大于红**（轨迹是青绿加法混合，
   灰线/文字的 g−r 恒为 0）。
3. **判据还得是分辨率无关的**：720p 时轨迹整体更暗，写死绝对阈值会把暗的那两条判成"没有"
   （实测 G / Yl 在 720p 下亮度掉到阈值以下）。改成「窗口内 g−r 的峰值 ≥ 20」就同时站得住。
4. **别拿「最亮的那一行」当波形定位判据**：样本最多的码值不是白条 ——
   蓝条亮度只有 14 却占更大面积，实测最亮行是 483（码值 14）。
   正确做法是直接量 75 IRE 那一行有没有整段白条轨迹（75% 白的码值 191 = 74.9% ≈ 75 IRE），
   并检查上下偏 12 px 处几乎为空。
5. **解码后的"目标框"位置与轨迹的实测偏差 ≤ 2 px**（1080p 实测：Yl 338↔339、R 449↔449、
   B 626↔625、中心 482↔482…），这条就是刻度可用的证据，已写成 6/6 命中断言。

## 打包与发版

```powershell
Windows\build-release.cmd                                   # 随手打一个 dist\VideoScopePad.exe
powershell -File Windows\release.ps1 -Tag win-v0.1.1-fix -Note "修 xxx"
                                                            # 正式发版：一版一个文件夹 + 清单 + tag
```

* **单文件 / 自包含**：目标机不用装 .NET；着色器已嵌进程序集，拷一个 exe 就能跑。
* **一版一个文件夹**：`dist\win-<tag>\VideoScopePad-<tag>-unsigned.exe` + `MANIFEST.txt`
  （版本 / SHA256 / 大小 / 源码提交 / 构建时间 / **独立自检逐项结果**）。
  旧版本一个都不动 —— 回滚就是「把旧文件夹里的 exe 拿出来用」，
  不用重新编译（重新编译出来的不一定等于当初那一份）。
* **自检不过不发版**：`release.ps1` 会把 exe 单独拷到空目录跑一遍 `--snapshot`，
  退出码非 0 直接抛错、不留半成品。
* 回滚的完整说明（三种方式 + 哈希校验）见 `Windows/ROLLBACK-Windows.md`。

### 发版/打包踩的坑

1. **单文件发布会漏掉外部内容文件**：第一次发出来的 exe 启动就报找不到 `ScopeKernels.hlsl`。
   着色器改嵌入资源（csproj `EmbeddedResource`）之后，`ShaderLibrary` 优先读资源、
   读不到才退回目录 —— 开发期改 HLSL 免编译生效，发布版只靠一个 exe。
2. **运行中的实例会锁住 `bin` 下的 dll**，重新生成报 MSB3027。`run-app.cmd` 会先关掉旧实例。
3. **`.NET` 的 ProductVersion 会被拼上 SourceLink 的 git 提交号**（`0.1.0+cb0060d…`）——
   清单里只取 `+` 前面那半段，提交号单独记一行。
4. **PowerShell 里发多行提交信息时，正文含 ASCII 双引号会提前结束字符串**
   （`判成"没有"` 这种）→ 改用 `git commit -F 文件`；这个坑这次踩了两次。
5. **API 推标签**：`POST /git/tags`（annotated tag 对象）在这个 token 上一直返回 **422（响应体还是空的）**，
   而 `POST /git/refs` 建**轻量标签**一次就成 —— 回滚只需要「tag 名 → 提交」这一层，
   所以脚本走轻量标签路线。

* **自包含**（`--self-contained`）：目标机器**不需要装 .NET**。
* **单文件**（`PublishSingleFile`）：着色器已经**嵌进程序集**
  （csproj 里 `EmbeddedResource`），所以不用带着 `Render\Shaders` 目录跑。
  `ShaderLibrary` 优先读嵌入资源、读不到才退回目录 —— 开发期改 HLSL 免编译即生效，
  发布版又只靠一个 exe。（这个「单文件里外部文件不会被打进去」的坑很隐蔽：
  第一次发布出来的 exe 会在启动时报找不到 ScopeKernels.hlsl。）
* 唯一的系统依赖是 **`d3dcompiler_47.dll`**（启动时编译 HLSL 用），Win10/11 自带。
* 未签名 exe 首次运行会有 SmartScreen 提示 →「更多信息」→「仍要运行」。
* 验证方式（**别只在仓库里跑**）：把 exe 单独拷到一个空目录，在那里跑
  `VideoScopePad.exe --snapshot out.png --frames 90 --source synthetic`，
  看 `out.png.report.txt` 是否全绿 —— 这一步同时证明了「自包含」与「着色器已内嵌」。

