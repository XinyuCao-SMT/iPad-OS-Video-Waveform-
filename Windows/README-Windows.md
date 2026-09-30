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
├─ VideoScopePad.Win/          ← 核心库（不依赖界面，可被自检工具引用）
│   ├─ Core/                   ← 布局与模型（对应 iPad 版 Model/）
│   │   ├─ RectF.cs            ← 轻量矩形（左上角原点、y 向下，与 SwiftUI/CGRect 习惯一致）
│   │   ├─ ScopeModels.cs      ← 格内容 / 示波器种类 / 旋转 / 刻度单位
│   │   └─ ScopeLayout.cs      ← 布局唯一来源（与 ScopeLayout.swift 逐行对应）
│   └─ Render/                 ← D3D11 渲染
│       ├─ D3DContext.cs       ← 设备 / 离屏渲染目标 / 读回 / 存 PNG
│       ├─ PngWriter.cs        ← 极简 PNG 编码（不依赖 System.Drawing / WIC）
│       ├─ SyntheticSource.cs  ← 合成测试信号（彩条 + PLUGE + 灰阶斜坡）
│       └─ Shaders/            ← HLSL（由金属着色器逐行移植）
└─ tools/
    ├─ render-selfcheck/       ← 离屏自检：不开窗口渲染并存 PNG
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
- [ ] Media Foundation UVC 采集
- [ ] WPF 界面与实时窗口（含刻度栏覆盖层）
- [ ] 冻结参考层 + 幅度读数
- [ ] LUT（.cube 解析 + 1D/3D 纹理）
- [ ] 音频套件（WASAPI）

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

