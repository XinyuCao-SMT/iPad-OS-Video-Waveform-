# 待办与已完成

> 版本保留与回滚方式见 `ROLLBACK.md`。当前最新版是 **v1.4.0-srt**。

---

## ✅ 已完成

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
