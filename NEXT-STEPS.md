# 待办与已完成

> 版本保留与回滚方式见 `ROLLBACK.md`。当前最新版是 **v1.9.0-phase**。

---

## ✅ 已完成

### v1.9.0 声相版（提交 `a7c9ca2`，tag `v1.9.0-phase`，CI run #33 全绿）

新增格子内容 **「声相（李萨如）」**（立体声即可，不涉及 5.1.4）。

**坐标约定**（与专业 goniometer 一致）

```
横向 x = Side = (L − R)/2   （右为正）
纵向 y = Mid  = (L + R)/2   （上为正）
```

| 图形 | 含义 |
|---|---|
| 竖直中线 | 单声道 / 完全同相 |
| 水平线 | 完全反相（单声道兼容播放会抵消） |
| 左上 / 右上 45° | 只有左声道 / 只有右声道 |
| 中间偏上的云团 | 正常立体声，越宽表示 Side 分量越大 |
| 云团偏离中线 | 整体声像偏左 / 偏右 |

**读数**：相关度 `ΣLR/√(ΣL²·ΣR²)`（+1 同相 / 0 无关 / −1 反相，附 −1…+1 条形）、
平衡 `10·log10(ΣL²/ΣR²)`（居中 / 偏左 x dB / 偏右 x dB）。

**实现取舍**
* 直方图在**音频线程按样本**累加 256×256（UInt16）；每约 43 ms（2048 样本）清算一次：
  非零点压成点列表（强度按 log 压缩，最多 1400 点）→ `memset` 清零 → 发一份快照到主线程。
  音频只有 48 kHz，CPU 直方图足够，**不需要 GPU**。
* 单声道输入时 R 取 L，分布自然落在竖直中线上（界面标注「单声道输入」）。
* 纯界面绘制（`PaneContent.isInterfaceOnly`），不走 Metal 管线、不计入 `requiredScopes`。
* 踩坑记录：SwiftUI `GraphicsContext` 没有 `draw(_:in:)`；点云叠加要用
  `ctx.drawLayer { layer in layer.blendMode = .plusLighter; ... }`。


---

## ✅ 已完成

### v1.8.0 声画延时版（提交 `5ea9bfd9`，tag `v1.8.0-avsync`，CI run #31 全绿）

**测试信号**：静音黑场 → 千周声（1 kHz）与彩条**同时**出现 → 一起消失 → 周期重复。
测的就是两件事到达本机的时刻差（声音快还是画面快、多少毫秒）。

**音频侧**（`Audio/AudioMonitor.swift`）
* `AVAudioEngine` + `AVAudioSession(.record, mode: .measurement)` —— measurement 关掉 AGC/降噪，
  测 1 kHz 更准；输入设备可选（采集卡 HDMI 内嵌音频 / USB 声卡 / 内建麦克风）。
* 千周声检测**不用 FFT**：RBJ 二阶带通（1000 Hz、Q=3）+ **逐样本包络检波**；
  连续超阈值 4 ms 确认「来了」，低于半阈值 12 ms 确认「走了」；起音时刻按 buffer 的
  `hostTime` 换算成主机时钟秒（采样级，48 kHz 下约 0.02 ms）。本底噪声自适应。
* 音量柱：每声道 RMS → dBFS → 0…1（-60…0 dBFS），峰值保持 + CLIP。

**视频侧**（`Render/FrameSignatureAnalyzer.swift` + kernel `vsFrameSignature`）
* 每帧一次 64×64 采样的「平均亮度 + 平均饱和度」签名，16 KB 回读由 CPU 归约 ——
  黑场（都低）与彩条（都高）据此区分，不依赖彩条图案，换分辨率/码率都不受影响。
* `VideoBarsDetector` 找「黑场 ≥150 ms → 彩条」的那一帧，给出该帧与上一帧时刻（量化步长）。
* 帧时刻用 AVFoundation 的 PTS —— 与 `AVAudioTime.hostTime` **同一主机时钟**，可直接相减。

**测量**（`Audio/AVSyncMeter.swift`）
* `offset = 视频时刻 − 音频时刻`：> 0 → 画面到得晚 → **声音快**；< 0 → **画面快**。
* 1.2 s 配对窗口防跨周期错配；最近 10 次取中位数 + 极差；状态机（等信号 / 等彩条 / 等千周声 / 已测到）。
* 界面注明「视频帧间隔 xx ms（±1 帧量化）」；带「测量补偿」用于抵消固定系统偏差。

**界面**
* 新格子内容「声画延时（A/V Sync）」：大号结论 + 中位数 / 次数 / 极差 / 帧间隔 /
  音频输入与 1 kHz 电平 + ±200 ms 时间轴（中心 0、左=画面快、右=声音快、绿带=阈值、橙带=极差）。
* **画面两侧音柱**：贴画面格子左右边缘，整格裁一次绝不越界，带 dB 刻度 / 峰值线 / CLIP。
* 设置新增「音频（音柱 / 声画延时）」；Info.plist 加 `NSMicrophoneUsageDescription`。

**精度与使用注意（重要）**
* 音频是采样级，视频受帧率量化：60p ≈ ±8 ms、30p ≈ ±17 ms。
* 音频输入**优先选与画面同源的那一路**（采集卡 HDMI 内嵌音频或 USB 音频），两条链路时延接近；
  用 iPad 麦克风拾音会叠加声程（约 3 ms/米），可用「测量补偿」抵消。
* 两个时刻都取自 iPad 主机时钟；若发现固定偏差，用「测量补偿」校零即可。
* 采集卡若**不把 HDMI 内嵌音频交给 iPad**（多数廉价采集卡如此），就只能用麦克风拾音或外加 USB 声卡。


---

## ✅ 已完成

### v1.7.1 修复版（提交 `00ba3841`，tag `v1.7.1-rot`，CI run #29 全绿）

**一、钻石图按 Tektronix 原版重做（v1.7.0 那版算法是错的）**

依据：泰克应用手册 **《Color Grading with the Spearhead Display》**（`2PW_28619_0`，第 2 节
*Color Grading and Gamut Monitoring with the Diamond Display*；可从
`download.tek.com/document/2PW_28619_0_HR.pdf` 取得）。原文逐条：

| 原文 | 结论 |
|---|---|
| "the top diamond … indicates levels of blue and green" | 上菱形画 **G 与 B** |
| "the bottom diamond displays only red and green" | 下菱形画 **G 与 R** |
| "with the green signal indicated on the left side of both diamonds" | **G 在两个菱形左侧** |
| "pure black where the two diamonds join in the center" | 纯黑在两菱形交会的**中心** |
| "pure white … center top of the top diamond and the center bottom of the bottom diamond" | 纯白在上菱形顶 / 下菱形底 |
| "a perfectly monochromatic signal … displays as a perfect vertical line" | 灰阶是**正中竖线** |
| "pure gray is indicated in the center of the broadest part of both diamonds" | 中灰在**最宽处中心** |

由此唯一确定坐标（显示坐标 x、y ∈ −1…1，y 向上，原点 = 纯黑）：

```
上菱形：x = B − G      y = (G + B)/2      （0 … 1）
下菱形：x = R − G      y = −(R + G)/2     （−1 … 0）
```

* kernel 每帧累加两个点（上/下钻石），共用同一张 256×256 直方图（section 1）。
* 刻度层：两个菱形 + 25% / 50% / 75% 等值线（以纯黑点为心缩放 = 「两分量之和」的等值线）
  + 灰阶竖线 + 两条最宽处水平线 + 中心黑点 + W / B / G / R 标注。
* v1.7.0 用的是自行推导（RGB 立方体沿白轴投影 → 单六边形），形状与轴向都不对，已废弃。

**二、画面方向跟随设备**

* 新增 `PictureRotation`（自动跟随界面 / 不旋转 / 顺时针 90° / 逆时针 90° / 180°），
  默认「自动跟随界面」：界面竖屏时画面顺时针转 90°，横屏不动。
* `ScopeLayout.compute` 增加 `pictureRotation`；画面旋转 90/270 时按**对调后的宽高比**适配显示区
  （否则竖屏会被裁掉或留大片黑边），角度记进 `PaneLayout.rotation`。
* `VSQuadUniforms` 增加 `misc.x` = 旋转角度，顶点着色器绕采样区中心旋转 UV；
  **几何矩形不变** → 画面永远不会溢出自己的格子（四分割安全）。
* 只作用于画面格子，示波器格子不受影响。

**待办（下轮顺手做）**
* `PaneContent.detail`（每种格子的一句话说明）还没接到界面上 —— 适合做成分格菜单的副标题。
* 想更贴近泰克家族的话，还可以加 **Split Diamond**（两个菱形左右错开，便于看近黑细节）
  与 **Arrowhead**（复合色域，需要做 composite 编码）。


### v1.7.0 色域与推流监看版（提交 `a85d5d41`，tag `v1.7.0-gamut`，CI run #28 全绿）

三个新的格子内容（全屏与四分割的内容选择里都能选）：

**一、钻石图（RGB 色域菱形图）**
* 数学：把 RGB 立方体沿白轴投影 —— `x = (√3/2)(B − G)`、`y = R − (G + B)/2`；
  三个轴 R 朝上、G 朝左下、B 朝右下，白色落在中心 (0,0)。
* 0–100% 的合法区域是投影六边形（顶点 R(0,1) / M(1,0.5) / B(1,−0.5) / C(0,−1) / G(−1,−0.5) / Y(−1,0.5)），
  也就是「上下两个菱形叠起来」的形状 —— 这正是「钻石图」名字的来源。
  **任何分量超出 0–100%（超白 / 负值 / 非法 YCbCr 结果）都会把点推到六边形外**，是查色域越界最直接的图。
* 刻度：100% 外框、75% 内框、25% / 50% 辅助菱形、中心白十字、R/G/B/M/C/Y/W 标注。

**二、马蹄图（CIE 1931 色度图）**
* 像素路径：`R'G'B'` →（BT.709 OETF 反函数去伽马）→ 线性 RGB → XYZ → 色度坐标 `xy`。
* 刻度：光谱轨迹（380–700nm、5nm 步长的 CIE 1931 2° 标准观察者数据 + 紫边闭合）、
  BT.709 / BT.2020 三角、D65 白点、0.1 网格与 x / y 标注。
* xy 平面用**正方形窗口**（x / y 同一比例尺），映射常量写在 `ShaderTypes.h`
  （`VS_CIE_ORIGIN_X/Y`、`VS_CIE_SPAN`），Metal 与 SwiftUI 共用 → 分布与刻度严格对齐。
* 过暗像素（XYZ 之和 < 2e-4）丢弃：色度在低亮度下会乱跳，专业监视器同样这么处理。

**三、推流状态（近 5 分钟折线图）**
* 每秒采样、保留 300 点；曲线：编码码率（我们自己编码器的字节计数）、SRT 估计带宽
  （libsrt `performanceData.mbpsBandwidth`）、SRT 发送速率（`mbpsSendRate`）、
  SRT 往返时延 RTT（`msRTT`，走右轴 ms）。
* **RTMP 协议本身没有链路反馈**，所以只画编码码率并在图内注明 —— 这是协议边界，不是没做。
* 左轴 Mb/s、右轴 ms，网格按分钟标注，纵轴上限按 1/2/5 取整，图例带当前值与 RTT 近 10 秒平均。
* 纯界面绘制（`PaneContent.isInterfaceOnly`），不走 Metal 管线、不计入 `requiredScopes`。

**实现取舍（以后照这个改）**
* **没有新增 Metal pipeline**：直方图缓冲区扩成 3 段 256×256（矢量 / 钻石 / 马蹄），
  归一化仍复用 `vsNormalizeVectorscope`，用 `refs.z` 选段；累计用 `flags.y` 传「要哪几种」的位掩码。
* 顺带省算力：不看波形 / Parade 时跳过波形原子累加（`flags.z`）。
* 三种二维图统一按正方形绘图区处理（`PaneContent.needsSquarePlot`），
  留白系数：矢量 0.78 / 钻石 0.86 / 马蹄 0.94。

---

## ✅ 已完成

### v1.6.1 布局修复版（提交 `0a8d4c61`，tag `v1.6.1-layout`，CI run #26 全绿）

实机反馈：非全屏（四分割）时格子里的内容不居中 / 显示不全 / 跑到别的格子。审计结论（以后别再翻代码）：

* **波形 / Parade 的绘图区不能按纹理比例内缩**。纵向才是标定过的幅度轴（码值 → IRE，与刻度线共用同一个
  矩形），横向只是「这一行的取样位置」—— 所以**铺满可用区域**即可；按 2:1 / 6:1 内缩只会让它在四分割里
  剩中间一条窄带。**只有矢量图必须保持 1:1**（Cb / Cr 两轴比例尺要一致）。`plotAspect` 因此改名
  `textureAspect`，只用于说明纹理尺寸，不再参与尺寸计算。
* **带刻度栏的格子要把「刻度栏 + 绘图区」当整体居中**；只让绘图区在剩余区域里居中会让整块内容偏右。
* 内边距按 `min(宽, 高)` 的 3.5% 取（横竖屏观感一致）；窄格子上先自动收窄刻度栏，别把绘图区挤没。
* **矢量图放大时超出绘图区的圈 / 75% 目标框要整圈不画**，否则被裁成半圈、看着像图形缺失；
  肤色线长度按方向裁到绘图区边界（新增 `maxLength`）。
* 刻度栏整体裁在**格子**内；绘图区裁剪外扩 1.5pt（否则边框线被裁一半，看着「缺一条边」）；
  峰值保持标注在窄绘图区里改画左侧。
* **新增「布局调试叠加层」**（设置 → 显示，默认关）：画出每格 格子(绿) / 绘图区(青) / 刻度栏(黄) /
  画面区(品红) 的实际矩形 + 实时尺寸标签 —— 以后再遇到这类问题，截一张图就能定位。
* 顺带：应用内版本号接通（工程原先没设 `MARKETING_VERSION`，「关于」页一直显示 1.0）。
  现在由生成器的 `appVersion` 统一给出，发版改一处重新生成即可。
* 工具：`push-via-api.ps1` 增加回退 —— 找不到与远端 tree 一致的本地提交时（git 的换行规范化会造成
  这种失配），改用上一个本地提交做 diff 基点。

### v1.6.0 品牌与网络修复版（提交 `fb58d4c8`，tag `v1.6.0-brand`，CI run #25 全绿）

**一、SRT 实机超时的真凶：缺「本地网络」权限说明（🔴 重要，以后别再踩）**

实机诊断给到的是 `SRT_REJ_TIMEOUT`，地址 `srt://192.168.6.106:9000?mode=caller&conntimeo=5000`
—— 地址解析完全正确（拼出来的就是这一条），所以问题在网络层。原因是：

> **iOS 14 起，App 访问局域网设备（192.168.x.x / 10.x.x.x）需要用户授权，前提是 Info.plist 里有
> `NSLocalNetworkUsageDescription`。没有这个键，系统连权限框都弹不出来，发往局域网的包被静默丢弃**，
> 表现就是连接一直超时（对端超时）。RTMP 连局域网地址同样会中招。

* 工程生成器加 `INFOPLIST_KEY_NSLocalNetworkUsageDescription`（Debug/Release 都加）；
* CI 打包步骤加兜底注入（与相机/相册权限同等处理）并打印结果；
* 产物核对：三个权限键齐全。

**二、网络诊断（卡在哪一层，直接写出来）**

新增 `Stream/NetworkDiagnostics.swift`：
* `localIPv4Addresses()`（getifaddrs 读 en0/en1…）、`subnetPrefix(of:)`（比 /24 网段）；
* `probeUDP(host:port:)`（Network.framework 发一个 SRT 风格控制包头）区分三种结果：
  收到回应 / 明确拒绝（ICMP 端口不可达 → 端口填错）/ 完全无回应；
* `StreamController` 失败时自动追加「网络探测」段 + 按「结果 + 是否同网段」给一句可照做的结论
  （权限 → 防火墙 → 服务未监听 → streamid/密码）。
* 踩坑：`NWEndpoint.Port(rawValue:)` 是**可失败**初始化器；`NWConnection` 的几个回调
  在复合表达式里推断不出参数类型，需要显式标注。

**三、四分割逐格适配**

* `PaneChromeOverlay` 原用固定像素偏移 `.position(x: rect.maxX - 44, y: rect.minY + 16)` 放每格菜单，
  按钮宽度随文字变化 → 小格子上超出格子。改为「容器=格子矩形 + 按格子宽度缩放的内边距（3–12pt）
  + 右上角对齐 + `clipped()`」。
* `VideoRenderer` 增加 Metal 裁剪框 `setScissorRect`：画面格子、示波器底/刻度栏/轨迹各自裁剪到自己的格子。
  从此任何 iPad 尺寸、竖屏横屏、矢量图放大都不会溢到邻格。

**四、App 图标与界面 logo**

* `AppIcon.appiconset` 里**原本没有图片文件**（等于没有图标）→ 用用户给的 1500×1500 图生成
  1024×1024 `AppIcon.png`（先铺白底去 alpha）并写进 Contents.json；
* 界面 logo：2029×395 带透明的源图 → `BrandLogo.imageset` 1x/2x/3x（205/411/616 px 宽），
  新增 `BrandLogoView` 固定在顶部一行最左边（不随 chip 滚动）。
* 体积影响：IPA 2.51 → 4.24 MB，Assets.car 17.7 KB → 1.72 MB。

### v1.5.0 地址表单版（提交 `bfd384ae`，tag `v1.5.0-form`，CI run #23 全绿）

用户反馈：手打一整条地址容易被符号格式坑到（全角冒号、漏斜杠、scheme 大小写、粘贴带的零宽字符），
而 SRT 只要库里看到的 scheme 不是严格小写 `srt` 就直接 `unsupportedUri`（`... error 1.`）。
所以把地址**拆成逐栏填写**：

* 新增 `Stream/StreamEndpoint.swift`：
  * `SRTModeOption`（呼叫/监听/会合，自带中文说明 —— 库里的 `SRTMode` 是 internal 用不了）；
  * `EndpointSanitizer`：全角折叠、去零宽字符、端口与可选整数的校验；
  * `RTMPEndpoint.make(...)`：主机/端口/应用/流密钥/RTMPS → `rtmp(s)://主机:端口/应用` + 流名；
  * `SRTEndpoint.make(...)`：主机/端口/模式/串流标识/延迟/密码/加密位数/连接超时 →
    小写 `srt://主机:端口?mode=…&streamid=…`，拼完复核 scheme；
  * 两条路径共用清洗规则：`make`（表单）与 `parse`（粘贴导入）。
* `StreamController`：拆成十几个持久化字段（`vsp.stream.rtmp.host` 等），启动时把旧的
  `vsp.stream.rtmpURL` / `srtURL` 一整条 URL 解析后填进各栏（老设置不丢）；
  新增 `addressPreview`（将连接的实际地址）、`addressProblem`（哪一栏有问题）、`importAddress()`。
* `StreamPanelView`：按协议显示栏位；SRT 有模式分段选择器与「高级」折叠区；
  实时显示「将连接」的实际地址；地址不合法时「开始推流」禁用；
  保留「粘贴完整地址自动填入各栏」入口。
* 顺带修：v1.4.1 里 RTMP「勾了加密就把端口改成 443」的兜底会覆盖主机栏里显式写的端口，删掉。
* `check-sources.mjs` 增加守卫：目录里每个源文件都必须在 `project.pbxproj` 里出现。
  （这次就是因为新增文件忘了重新生成工程，CI 报了一堆 `cannot find type 'X' in scope` 白烧一轮。）

**教训：新增/删除源文件后，必须跑 `node tools/generate-xcodeproj.mjs` 再提交**，
现在本地自检会拦住这类问题。

### v1.4.1 SRT 修复版（提交 `44cef718`，tag `v1.4.1-fix`，CI run #21 全绿）

实机 SRT 推流失败，面板只有一行裸错误码：

```
The operation couldn't be completed. (SRTHaishinKit.SRTConnection.Error error 1.)
```

查了 HaishinKit 2.2.5 源码后可以确定这个码的含义（记录在这里，以后不用再查）：

| 码 | 分支 | 触发条件 |
|---|---|---|
| 0 | `invalidState` | `SRTSocket.open` 抛出的非 rejected 错误（socket 建不起来 / 超时 / 被中断），或 connect 里 generic catch |
| 1 | **`unsupportedUri`** | `SRTSocketURL(uri)` 返回 nil —— 它**只检查** `url.scheme == "srt"`（严格相等，不做大小写/前缀容错） |
| 2 | `failedToConnect` | socket 层 `SRTSocket.Error.rejected(reason)`，带 `SRT_REJ_*` 原因码 |

也就是说 error 1 只可能来自地址本身。这一版做了：

* **地址解析重写成手工解析**：先做 `folding(.widthInsensitive)` 全角折叠（中文输入法的 `：／ｓｒｔ`），
  去掉零宽字符（200B/FEFF/200E/200F/2060）与首尾空白，剥掉 `SRT://` / `srt:` 前缀（大小写不敏感），
  手工拆 `host[:port]?query`，再用**小写 `srt://` 重新拼装**并复核 scheme —— 从此不会再出现
  「库认为 scheme 不对」；缺 `mode` 自动补 caller/listener，缺 `conntimeo` 补 5000ms；
  支持 `[::1]:9000`；兼容 `srt://host:9000/live/xxx` 路径写法（当 streamid 用）。
* **错误翻译**：三个分支各自的中文说明（并强调 SRT 走 **UDP**，防火墙要放行 UDP）；
  `failedToConnect` 把 18 个 `SRTRejectReason` 全部翻译（badsecret=密码不对、peer=对端拒绝……
  timeout=对端超时，地址/端口很可能不对）。
* **诊断串 + 一键复制**：面板新增「诊断信息」框，包含输入原文、解析结果、失败阶段、错误分支、
  libsrt 版本、`scheme/host/port/mode/streamid`、完整地址、编码参数；`StreamController.copyDiagnostics()` 复制到剪贴板。
* **20 秒超时看门狗**：RTMP `connect+publish`、SRT `connect` 都有上限，避免界面一直停在「连接中」
  （libsrt 的 `conntimeo` 只管 socket 层）。
* SRT 连上但 `connected == false` 时不再假报成功。

> 附：这次实机安装失败（Sideloadly `SSLEOFError developerservices2.apple.com`）与 IPA 无关 ——
> 是 Windows 那台机器的系统代理（Clash Verge 的 127.0.0.1:7897）打断了 Apple 开发者服务的 TLS 握手，
> 直连时该端点返回 200 正常。侧载前关掉系统代理，或给 `*.apple.com` 加 DIRECT 规则。

### v1.4.0 SRT 推流版（提交 `1c3bdb4`，tag `v1.4.0-srt`，CI run #19 全绿）

* **矢量图缩小**：四分割里格子高度就是半屏，矢量图又是 1:1 正方图，圆环直径正好等于格子高度、上下顶格，
  看着像「占了半屏还多」。新增 `ScopeLayout.fillFactor`：矢量图 **0.78**、亮度波形 0.96、RGB Parade 0.97、
  画面 1.0（扁长图形缩太多反而浪费高度）；绘图区先按纹理比例内缩再乘系数，条带高度计算同步除以该系数。
* **接入 HaishinKit 2.2.5（SPM）**：按你选的方案 A，`RTMPHaishinKit` 接管 RTMP（并顺带支持 **RTMPS**），
  `SRTHaishinKit` 接管 SRT（自带 libsrt 的 xcframework）。**手写的 `RTMPClient.swift` 已删除**。
  * 新增 `Stream/StreamTransport.swift` 作为唯一传输层。**关键点：不让 HaishinKit 编码** ——
    `RTMPStream.append` / `SRTStream.append` 遇到 `formatDescription.isCompressed == true` 的
    CMSampleBuffer 会直接当 RTMP 视频消息 / MPEG-TS 发出，所以我们 VideoToolbox 的编码结果
    **一份数据同时喂 MP4 录制与推流**，没有二次编码，也不需要 MediaMixer 那一层离屏渲染。
  * 地址：RTMP 为 `rtmp://主机[:端口]/应用` + 流密钥栏（也支持写在地址里）；
    SRT 为 `srt://主机:端口?mode=caller[&streamid=…]`（默认端口 9710）。
  * SRT 侧显式 `setExpectedMedias([.video])`：我们是直接 append 已编码帧的，
    `publish()` 里靠 `outgoing` 判断媒体的逻辑会落空，不显式指定则 PAT/PMT 里没有流、接收端看不到画面。
  * 状态：RTMP 订阅 NetStatus（发布成功 / 被拒原因直接显示在面板）；SRT 无事件，轮询 `readyState` + `connected`。
    确认发布后强制一个关键帧，接收端秒开。
  * 面板加协议选择（RTMP / SRT）与各自地址栏；`srtURL` / `transportKind` 照旧持久化。
  * 体积：IPA 0.49 MB → **2.44 MB**（主程序 1.87 → 7.7 MB）。已核对包内含
    `HaishinKit`/`RTMPStream`/`SRTStream`/`srt_connect` 等符号且 `RTMPClient` 消失。
* **工程侧**：`tools/generate-xcodeproj.mjs` 现在会生成 SPM 依赖（`XCRemoteSwiftPackageReference` +
  三个 `XCSwiftPackageProductDependency`），重新生成工程不会丢依赖；CI 增加独立的「解析 Swift Package 依赖」步骤，
  解析失败与编译失败分开看日志（本次解析到 HaishinKit 2.2.5 + Logboard 2.6.0）。

### v1.3.1 修复版（提交 `22bd5a0`，tag `v1.3.1-fix`，CI run #18 全绿）— 实机反馈四项

实机测试 v1.3.0 后反馈的四个问题：

1. **🔴 修闪退（严重）**：一开「读数」就崩，且因为 `showMeasurement` 被持久化，**重启后照样崩、根本进不去**。
   根因是 Swift **独占访问违规**（`Simultaneous accesses to …`）：`PeakHoldTracker.update` 里写成
   `track(..., &state.whitePeakIRE)`，而 `track` 函数内部又去读 `state.hasData` —— 同一个属性的
   inout 访问与读取重叠。现在改成先取局部变量、算完再整体写回 `state`；`WarningLatch` 里
   「边遍历边改字典」也改成快照后整体替换。
2. **修四分割比例**：绘图区宽高比原来写死 1.45 / 1.6，但示波器纹理是 512×256（2:1）与 1536×256（6:1），
   于是轨迹被横向拉伸、格子越小越别扭。现在 `plotAspect` 与纹理严格一致（波形 2.0 / Parade 6.0 /
   矢量 1.0 / 画面 16:9），绘图区按比例在可用区域里**等比内缩居中**，格子之间不再互相压。
3. **刻度与标注随格子自适应**：刻度栏宽度改成跟格子高度挂钩（避免小格子里刻度栏把绘图区挤没），
   字号按宽度缩放到 7–12pt；绘图区变矮时自动减少标注密度（全量 → 只主刻度 → 只留 0/中/满三档）；
   矢量图的圆环 / 色标框 / 肤色线标注按半径缩放，过小时直接隐藏，`< 22pt` 的刻度栏不画数字。
4. **画面上的每格实时读数已移除**：数值统一到顶部信息行，小格子里不再有文字压轨迹。
5. **二级设置按需显示**：`AppSettings` 增加一批派生开关（`showsPicturePane` / `showsWaveformPane` /
   `showsScaleUnitOption` / `showsVectorscopeGainOption` / `showsPeakHoldOption` / `showsZebraOption` …），
   控制栏与设置面板据此隐藏当前布局用不到的项 —— 没有波形/Parade 格子就不显示「波形模式」与「刻度单位」，
   没有矢量格子就不显示「矢量放大」，没有画面格子就不显示「斑马纹」。
6. **HUD 移到最顶上一行**：新增 `Views/TopStatusBar.swift`（`TopInfoChips`），把设备、分辨率、声明/实测帧率、
   像素格式·量化范围、色彩空间三件套、峰值白/黑位/平均/色度峰值、LUT·已调色、丢帧、已确认报警
   全部收进顶栏一行（超宽可横向滚动）；`Views/HUDOverlay.swift` 已删除，**画面不再被信息层遮挡**。
   设置里的开关改名为「顶部显示信号 / 读数信息行」。

> 踩过的坑：本轮我用 PowerShell 的 `Set-Content -Encoding UTF8` 去替换 `SettingsSheet.swift` 里的中文，
> 造成文件双重编码损坏（2800 多处语法错误）。已 `git checkout --` 恢复、改用专用编辑工具重做。
> **结论：这个工程的中文源码一律不要用 PowerShell 写。**

### v1.3.0 录制推流版（提交 `8e5773a`，tag `v1.3.0-stream`，CI run #17 全绿）

* **本机录制 MP4**：VideoToolbox H.264 + AVAssetWriter 直通写入，与推流共用同一次编码（不二次编码）；文件在「文件 → 本应用 → Recordings」。录制的是**输入信号**（原始素材）——这与广播监视器的做法一致：LUT 只是监看视图。
* **RTMP 推流（纯 Swift 实现）**：握手 → AMF0 `connect`/`createStream`/`publish` 状态机 → Set Chunk Size → FLV 视频标签（AVC sequence header 在服务器确认 publish 后发）。地址支持 `rtmp://主机:端口/应用/流密钥`，流密钥也可单独一栏。
  ⚠️ **未对着真实服务器联调过**：编译与静态检查都过，第一次连你的服务器可能需要调一轮（面板会显示状态与错误文本）。
* **抓帧**：把当前输入帧存进相册（需要照片写入权限）。
* **读数 CSV 导出**：时间、峰值白/黑位/平均/动态范围（IRE 与等效 mV 同时给）、R/G/B 峰值、色度峰值、超白超黑占比、报警状态；可导出到「文件」或直接分享。
* 退到后台自动停止录制与推流；面板参数（码率/关键帧/地址）自动持久化。

### v1.2.0 看守版（提交 `67020ef`，tag `v1.2.0-assist`，CI run #13 全绿）

* **真峰值保持 Peak Hold**：新峰值立即钉住，保持 N 秒后按 12 IRE/秒 衰减；波形/RGB 格子上用虚线游标标出峰值与黑位，数值跟随刻度单位；控制栏「辅助」菜单可开关、可一键清除。
* **斑马纹 Zebra**：超白打黄色 45° 斜纹（阈值可调 60–109 IRE，默认 100）、黑切割打蓝色斜纹；**只加在 `fsDisplay`**，已用脚本按函数区间核对，不会污染示波器与幅度读数。
* **超标报警**：连续命中 N 次（默认 3 次 ≈ 0.3 秒）才确认，避免闪烁；确认时监视区描红 + 顶部信息行列出「已确认」计数（v1.3.1 起从画面 HUD 移到顶栏）+ 一次触感振动；门槛可选 1/3/6/12 次。

### v1.1.0 四分割版（提交 `99ad8ec`，tag `v1.1.0-quad`，CI run #11 全绿）

* **全屏 / 四分割可配内容**：一键来回切；全屏内容可选（画面或任一种示波器），四分割每格内容独立可选（格子右上角有菜单，控制栏也有对应选择器）。
* **示波器侧边大号数值刻度**：波形/RGB 左侧独立刻度栏，12pt 半粗数字 + 刻度短线；单位可切 **IRE / mV / %**；每格左上角显示实时读数。
* **等效 mV 与信号信息**：数值读数跟随刻度单位（可直接读 mV）；强制显示分辨率、声明帧率、实测帧率、像素格式、量化范围、色彩原色 / 传输函数 / YCbCr 矩阵；**默认自动选择输入格式**（手动选择降级到设置里的「高级」）。

### v1.0.0 监视器版（提交 `385d8d0`，tag `v1.0.0-monitor`，CI run #9 全绿）

* UVC 采集卡接入（设备发现、格式协商、零拷贝帧回调）、矢量示波器、亮度波形、RGB Parade、`.cube` LUT（1D+3D）、信号幅度数值读数、冻结画面、曝光/对比度/饱和度/伽马、四种布局预设、HUD。

### 工程侧

* 云端编译（GitHub Actions）+ 一键循环脚本 `tools/ci-cycle.ps1`（推送 → 触发 → 等待 → 失败自动抓错误）。
* Windows 上的三层静态自检：`check-sources.mjs`（配平/重名/共享宏/ViewBuilder 上限）、`parse-swift.mjs`（tree-sitter 真语法解析）、`ci-locate-selftest.mjs`（CI 目录定位）。

---

## ⏳ 待办（都没确认过，随时可以挑）

### ~~等你拍板：SRT 怎么接~~ → 已按方案 A 实施（v1.4.0）

你选了 **方案 A：直接调用 HaishinKit**，v1.4.0 已经落地（`RTMPHaishinKit` + `SRTHaishinKit`，手写 RTMP 删除）。
查证过程留在这里备查：SRT 是 libsrt 那套 ARQ 重传 + 加密 + 握手，手写不现实；
HaishinKit 官方就是「RTMP + SRT」双协议库（[仓库](https://github.com/HaishinKit/HaishinKit.swift)），
而且 2.x 把协议拆成了独立 product，还支持直接吃**已压缩**的 CMSampleBuffer —— 正好能复用我们自己的编码器。

### 推流/录制的后续

* **联调 RTMP / SRT**：现在两边的报错都会显示在面板上（RTMP 用服务器 NetStatus，SRT 用连接状态轮询）。
  对着你的服务器跑一次，把面板上的状态/错误发我，我按报错改。
* **断线重连 / 自适应码率**：HaishinKit 有 `StreamBitRateStrategy` 与 `NetworkMonitor`，还没接；
  现在断流会提示但不会自动重连。
* **把 `Package.resolved` 提交进仓库**：目前 CI 每次按 `upToNextMajorVersion 2.2.5` 解析
  （本次解析到 HaishinKit 2.2.5 + Logboard 2.6.0）。要完全钉死版本就把解析出来的
  `Package.resolved` 一并提交。
* **post-LUT 录制/推流**：现在录/推的是输入信号；想录"套了 LUT 之后的画面"需要在渲染器里把显示纹理回读到 CVPixelBuffer（多一个渲染通道 + 缓冲池）。
* **带示波器与顶部信息行的合成截图**：现在抓帧只有画面；要连刻度与信息层一起截，需要在 Metal 合成结果上做回读，再叠加 SwiftUI 图层。
* **音频**：采集卡的 HDMI 内嵌音频不走视频采集通道，需要单独找音频输入设备（UVC 音频或 USB 声卡）。
  HaishinKit 支持 AAC，接上音频设备后可以直接走同一条会话。

### 画质与信号

* **HDR / HLG / PQ**：在 `fsVideoBiPlanar` 之后插一段 HLG/PQ → 线性 → tone map；示波器统计放在 tone map 之后就是标准 SDR 读数。工程里已经能读出传输函数（PQ/HLG 会显示出来），但还没做映射。
* **10bit P010 输入**：需要先确认 iPad 侧 UVC 是否真的给出 P010 格式；给出的话要走新的像素格式分支与着色器路径。
* **YCbCr Parade**：现在有 RGB Parade，加一条 YCbCr 的只需要在 `ScopeKernels.metal` 里换一组平面。

### 其它功能

* **干净画面外送**：把不带 UI 的画面送到外接 HDMI / AirPlay（iPad 上可用外接显示器 + 独立窗口）。
* **峰值保持的逐通道游标**：现在画的是亮度峰值与黑位；R/G/B 各自的游标已经有数据（`PeakHoldState` 里有），只差画出来。
* **直方图面板**：RGB 三通道直方图，可以复用现有的全局测量直方图缓冲区（零额外 GPU 开销）。

### 已知限制（不是 bug，是接口边界）

* **真实模拟电平读不到**：UVC 只交数字化并做过钳位/增益的码流；界面上的 mV 是按 `100 IRE = 700 mV` 换算的**等效值**。
* **信号源本身的帧率读不出**：采集卡若做帧率转换，只能读到「实际收到的流」的帧率（界面已标注「实测」）。
* 采集卡供电不足会掉线；带 HDCP 的源不出图；采集卡同一时刻只能被一个 App 占用。
