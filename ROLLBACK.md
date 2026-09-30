# 回滚指南 · 版本保留说明

十七个版本都已固定成**不可变的历史点**，随时可以退回去。每个版本都有独立 tag、独立源码归档、独立 IPA（本地 + GitHub Release 永久附件）。

## 十七个版本

| 版本 | 提交 | Tag / 分支 | 内容 | IPA |
|---|---|---|---|---|
| **v1.0.0 监视器版**<br>（最初的稳定版） | `385d8d0` | tag `v1.0.0-monitor`<br>分支 `legacy/v1.0.0-monitor` | UVC 监视 + 矢量示波器 + 亮度波形 + RGB Parade + `.cube` LUT + 信号幅度读数 + 冻结/调色 + 布局预设（仅画面/底部/右侧/叠加） | `dist/VideoScopePad-v1.0.0-monitor-unsigned.ipa` |
| **v1.1.0 四分割版** | `99ad8ec` | tag `v1.1.0-quad` | 全屏/四分割可配内容、示波器侧边大号刻度（IRE/mV/%）、等效 mV 与信号信息自动显示、自动选择输入格式 | `dist/VideoScopePad-v1.1.0-quad-unsigned.ipa` |
| **v1.2.0 看守版** | `67020ef` | tag `v1.2.0-assist` | 真峰值保持游标、超白/黑切割斑马纹、超标报警红框与振动 | `dist/VideoScopePad-v1.2.0-assist-unsigned.ipa` |
| **v1.3.0 录制推流版** | `8e5773a` | tag `v1.3.0-stream` | 本机 MP4 录制（H.264 直通）、手写 RTMP 推流、抓帧存相册、读数 CSV 导出 | `dist/VideoScopePad-v1.3.0-stream-unsigned.ipa` |
| **v1.3.1 修复版** | `22bd5a0` | tag `v1.3.1-fix` | 修「开读数必闪退」（Swift 独占访问）、修四分割比例与重叠、刻度随格子自适应、二级设置按需显示、HUD 移到顶栏一行 | `dist/VideoScopePad-v1.3.1-fix-unsigned.ipa` |
| **v1.4.0 SRT 推流版** | `1c3bdb4` | tag `v1.4.0-srt` | 接入 HaishinKit 2.2.5：RTMP / RTMPS / SRT 推流（替换手写 RTMP）；矢量图缩到 0.78 不再占满半格 | `dist/VideoScopePad-v1.4.0-srt-unsigned.ipa` |
| **v1.4.1 SRT 修复版** | `44cef718` | tag `v1.4.1-fix` | SRT 地址解析加固（全角折叠 / 零宽字符 / 强制小写 scheme / 自动补 mode 与 conntimeo）、18 种拒绝原因翻译、可一键复制的诊断信息、20 秒超时看门狗 | `dist/VideoScopePad-v1.4.1-fix-unsigned.ipa` |
| **v1.5.0 地址表单版**<br>（最新，**推荐装**） | `bfd384ae` | tag `v1.5.0-form` | 推流地址改成**逐栏填写**（RTMP：地址/端口/应用/流密钥/加密；SRT：主机/端口/模式 caller·listener·rendezvous/串流标识 + 高级的延迟·密码·加密位数·超时），实时显示「将连接」地址与逐栏校验，「开始推流」在地址不合法时禁用；保留「粘贴完整地址自动填入」入口；旧设置自动迁移到各栏 | `dist/VideoScopePad-v1.5.0-form-unsigned.ipa` |
| **v1.6.0 品牌与网络修复版** | `fb58d4c8` | tag `v1.6.0-brand` | **补上 `NSLocalNetworkUsageDescription`** —— iOS 14 起访问局域网设备必须授权，缺这个键系统连权限框都不弹、数据包被静默丢弃，推流连 `192.168.x.x` 就表现为一直超时（SRT_REJ_TIMEOUT）；新增网络诊断（本机 IP / 网段比较 / UDP 探测 / 可照做的结论）；四分割逐格适配（每格菜单按比例内缩并 `clipped()` + Metal `setScissorRect` 裁剪）；App 图标（之前资源里没有图片文件）与顶部界面 logo | `dist/VideoScopePad-v1.6.0-brand-unsigned.ipa` |
| **v1.6.1 布局修复版** | `0a8d4c61` | tag `v1.6.1-layout` | 四分割逐格修正：波形/Parade 绘图区**铺满格子**（纵向才是标定过的幅度轴，不再按 2:1 / 6:1 内缩成中间一条）；「刻度栏 + 绘图区」整体居中；内边距按短边、小格子自动收窄刻度栏；矢量图放大时超出绘图区的圈/目标框整圈不画；刻度栏与峰值标注裁剪在格子内；新增**布局调试叠加层**（设置 → 显示）；应用内版本号接通（1.6.1） | `dist/VideoScopePad-v1.6.1-layout-unsigned.ipa` |
| **v1.7.0 色域与推流监看版** | `a85d5d41` | tag `v1.7.0-gamut` | 新增三种格子内容：**钻石图**（⚠️ 此版算法是错的，见 v1.7.1 修正）、**马蹄图**（CIE 1931：380–700nm 光谱轨迹 + BT.709/BT.2020 三角 + D65 白点）、**推流状态**（近 5 分钟曲线：编码码率 / SRT 估计带宽 / SRT 发送速率 / RTT 往返时延，每秒一点共 300 点） | `dist/VideoScopePad-v1.7.0-gamut-unsigned.ipa` |
| **v1.7.1 修复版** | `00ba3841` | tag `v1.7.1-rot` | **钻石图按 Tektronix 原版重做**（依据泰克手册 `2PW_28619_0` 第 2 节：上菱形画 G+B、下菱形画 G+R、**G 在两个菱形左侧**、纯黑在两菱形交会中心、纯白在上下顶端、灰阶是正中竖线、中灰在最宽处中心 → `x=B−G, y=(G+B)/2` 与 `x=R−G, y=−(R+G)/2`）；新增**画面方向**设置（自动跟随界面 / 不旋转 / 顺·逆时针 90° / 180°），画面格子旋转时按对调宽高比适配、UV 在顶点着色器里绕采样区中心旋转（几何矩形不变，绝不溢出格子） | `dist/VideoScopePad-v1.7.1-rot-unsigned.ipa` |
| **v1.8.0 声画延时版** | `5ea9bfd9` | tag `v1.8.0-avsync` | **声画延时（A/V Sync）测量**：音频侧 1 kHz 带通 + 逐样本包络检波（采样级起音），视频侧每帧画面签名（亮度/饱和度）找「黑场 → 彩条」跳变帧，两者同在主机时钟上直接相减 → 报「声音快 / 画面快 xx ms」+ 中位数 / 极差 / 帧间隔量化；新增**画面两侧音柱**（L/R，dB 刻度、峰值保持、CLIP，裁在格子内）；设置新增音频输入选择与测量补偿；Info.plist 增加麦克风权限键 | `dist/VideoScopePad-v1.8.0-avsync-unsigned.ipa` |
| **v1.9.0 声相版** | `a7c9ca2` | tag `v1.9.0-phase` | 新增格子内容**声相（李萨如 / goniometer）**：按样本累加 (Side, Mid) 二维分布 —— 竖直中线 = 单声道/同相、水平 = 反相、45° = 只有 L 或只有 R；同时给出**相关度**（+1 同相 / 0 无关 / −1 反相，带 −1…+1 条形）与 **L/R 平衡 dB**；纯界面绘制（CPU 直方图，不占 GPU），立体声输入即用 | `dist/VideoScopePad-v1.9.0-phase-unsigned.ipa` |
| **v1.10.0 音频分析版** | `2eb8338e` | tag `v1.10.0-audio` | 新增**音频频谱 / 响度**格子：vDSP FFT → 1/3 倍频程 31 段（20 Hz–20 kHz 柱状图 + 100/1k/10k 刻度）+ BS.1770 K 加权算 **Momentary(400 ms) / Short-term(3 s) LUFS**（带 −23 EBU / −24 ATSC 目标刻度）与 RMS / 峰值 dBFS；**冻结语义改为「只冻图表」** —— 示波器轨迹与数值读数停在按下那一刻、**实时画面继续更新**（用上一个信号的图形对比当前画面），音频相关显示永不冻结；新增「冻结时连实时画面一起冻住」选项 | `dist/VideoScopePad-v1.10.0-audio-unsigned.ipa` |
| **v1.10.1 logo/署名版** | `29f038e` | tag `v1.10.1-logo` | 软件内 logo 换成 `SMG + SMT 黑底白字.png`（实测白字 + 透明底：62.6% 全透明 + 37.4% 纯白，重新生成 1x/2x/3x，深色顶栏上不会出现黑底方块）；「关于」页新增**开发者：smt 曹昕宇**并在页首放 logo | `dist/VideoScopePad-v1.10.1-logo-unsigned.ipa` |
| **v1.11.0 冻结参考叠加版**<br>（最新，**推荐装**） | `071e040` | tag `v1.11.0-reference` | 「冻结」升级为**参考层叠加**：不再停住图表，而是把按下那一刻的示波器图形（波形/RGB 叠加/Parade/矢量/钻石/马蹄）整块 blit 拷成参考纹理，**实时图表照常刷新**、参考层以**琥珀色幽灵**叠在实时轨迹上（不透明度可调，实时轨迹恒 100%）；数值读数同样存一份参考，顶部信息行给出「参考 峰/黑/均」与「Δ 峰/均」（差值 <0.5 IRE 绿、<2.0 IRE 黄、更大橙）；峰值保持游标叠一条琥珀色细虚线；着色器 `fsScopeTrace` 新增 `params.w` = 叠加倍率；「冻结时连实时画面一起冻住」保留（默认关） | `dist/VideoScopePad-v1.11.0-reference-unsigned.ipa` |

> ⚠️ **v1.3.0 有已知崩溃**：只要打开「读数」，`PeakHoldTracker` 的独占访问违规就会让 App 崩溃，
> 而且读数开关是持久化的，**重启后照样崩、根本进不去**。要修只能装 v1.3.1 或更早版本（v1.2.0 及以前没有这个 bug）。
>
> v1.0.0 对应的是**第一次云端构建成功**的那份代码（CI run #9），它没有四分割逻辑，已核对过归档内容。
> 每个版本都用 `default.metallib` 哈希 + 主程序字符串核对过「新代码确实进了包」：
> v1.0.0/v1.1.0 的 metallib 相同（只改 Swift/UI），v1.2.0 变大且哈希不同（斑马纹着色器），
> v1.3.0 的 metallib 与 v1.2.0 相同、主程序从 1588 KB 涨到 1921 KB 且出现 `Recordings`/`rtmp://`/`StreamController` 字符串。
> v1.3.1 的 metallib 仍是 `20D723D0C9…`（没动着色器），主程序降到 1871 KB（删掉 `HUDOverlay`、加入 `TopInfoChips`）。
> v1.4.0 的 metallib 仍是 `20D723D0C9…`，主程序涨到 7.7 MB（HaishinKit + libsrt 静态链入），
> IPA 从 0.49 MB 变成 2.44 MB，包内出现 `HaishinKit`/`RTMPStream`/`SRTStream`/`srt_connect` 且 `RTMPClient` 消失。
> v1.4.1 的 metallib 仍是 `20D723D0C9…`，主程序 7.84 MB，包内出现 `conntimeo` 与新的中文诊断文案。
> v1.5.0 的 metallib 仍是 `20D723D0C9…`，主程序 8.0 MB，包内出现地址表单的新文案。
> v1.6.0 的 metallib 仍是 `20D723D0C9…`，主程序 8.06 MB；IPA 从 2.51 MB 涨到 **4.24 MB**（加了 App 图标），
> `Assets.car` 从 17.7 KB 涨到 1.72 MB，产物里能看到 `AppIcon60x60@2x.png` 与 `AppIcon76x76@2x~ipad.png`，
> Info.plist 里 `NSCameraUsageDescription` / `NSPhotoLibraryAddUsageDescription` / `NSLocalNetworkUsageDescription` 三个键齐全。
> v1.6.1 的 metallib 仍是 `20D723D0C9…`，主程序 8.07 MB，包内出现「布局调试叠加层」文案。
> v1.7.0 的 metallib 变为 `56109FF3143BE9FC…`（**预期**：`ScopeKernels.metal` 增加了钻石图与马蹄图的坐标变换），
> 主程序 8.15 MB，包内出现「钻石图（RGB 色域）」「马蹄图（CIE 色度）」「推流状态（近 5 分钟）」等新文案，`CFBundleShortVersionString = 1.7.0`。
> v1.7.1 的 metallib 变为 `17A4B77546503A89…`（改了 `DisplayShaders.metal` 的画面旋转与 `ScopeKernels.metal` 的钻石图变换），
> 主程序 8.17 MB，包内出现「自动跟随界面」等新文案，`CFBundleShortVersionString = 1.7.1`。
> v1.11.0 的 metallib 变为 `2C9E8DE862AB4BFD…`（**预期**：`DisplayShaders.metal` 的 `fsScopeTrace` 新增参考层倍率 `params.w`，
> 81 592 bytes），主程序 8.31 MB，`Assets.car` 仍是 1700 KB，
> 包内出现 `freezeReference` / `referenceOpacity` 两个新设置键，`CFBundleShortVersionString = 1.11.0`。

## 三种回滚方式

### 方式 A：只把 iPad 上的 App 换回旧版（最快，不动代码）

1. 用 **Sideloadly** 装想要的版本，例如 `dist/VideoScopePad-v1.0.0-monitor-unsigned.ipa`
2. 或从 GitHub Release 下载（**永久有效**，不受工件 30 天限制）：
   * v1.0.0 <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.0.0-monitor>
   * v1.1.0 <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.1.0-quad>
   * v1.2.0 <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.2.0-assist>
3. 同一个 Bundle ID 重装属于升级/降级安装，**设置和 LUT 通常保留**；保险起见可先把 iPad 上「文件 → 本应用 → LUTs」里的 `.cube` 拷一份出来

### 方式 B：把源码回滚到旧版

```bash
cd "E:\harness\iPad OS Software Waform"

git checkout v1.0.0-monitor     # 或 v1.1.0-quad / v1.2.0-assist / v1.3.0-stream / v1.3.1-fix / v1.4.0-srt / v1.4.1-fix / v1.5.0-form / v1.6.0-brand / v1.6.1-layout / v1.7.0-gamut / v1.7.1-rot / v1.8.0-avsync / v1.9.0-phase / v1.10.0-audio / legacy/v1.0.0-monitor
# …要验证/构建就在这个状态跑 tools/ci-cycle.ps1

git checkout main               # 回到最新版
git log --oneline -1            # 确认当前位置
```

GitHub 上十六个 tag 都已推送，换台机器 `git clone` + `git checkout <tag>` 同样有效。

### 方式 C：完全离线恢复（连 git / GitHub 都没有）

解压这些归档即可得到完整工程（含 Xcode 工程、scheme、CI 工作流）：

```
E:\harness\_backup\src-v1.11.0-reference-071e040.zip     ← 最新版源码
E:\harness\_backup\src-v1.10.1-logo-<sha>.zip           ← 上一版源码
E:\harness\_backup\src-v1.0.0-monitor-385d8d0.zip       ← 最早那版源码
E:\harness\_backup\build-records.txt                    ← 各版本的 CI 运行地址与 IPA 哈希
```

> 注意：v1.4.0 起工程有一个 SPM 依赖（HaishinKit）。离线构建前需要有网解析包，
> 首次编译时 Xcode 会自动拉取（各版本依赖见 `tools/generate-xcodeproj.mjs` 里的 `swiftPackage`）。

## 构建工件地址（GitHub Actions，30 天后过期）

| 版本 | CI 运行 |
|---|---|
| v1.0.0-monitor | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36291748106> |
| v1.1.0-quad | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36299560754> |
| v1.2.0-assist | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36300439675> |
| v1.3.0-stream | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36302386663> |
| v1.3.1-fix | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36304201815> |
| v1.4.0-srt | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36305507610> |
| v1.4.1-fix | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36306831825> |
| v1.5.0-form | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36307727940> |
| v1.6.0-brand | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36308909145> |
| v1.6.1-layout | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36432295696> |
| v1.7.0-gamut | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36575613952> |
| v1.7.1-rot | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36671677533> |
| v1.8.0-avsync | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36706991542> |
| v1.9.0-phase | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36723156753> |
| v1.10.0-audio | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36678521214> |
| v1.10.1-logo | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36737098626> |
| v1.11.0-reference | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36680959498> |

十七个版本的 IPA 都已挂到对应 Release 的附件里，**永久可下载**：

- v1.0.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.0.0-monitor>
- v1.1.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.1.0-quad>
- v1.2.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.2.0-assist>
- v1.3.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.3.0-stream>
- v1.3.1：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.3.1-fix>
- v1.4.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.4.0-srt>
- v1.4.1：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.4.1-fix>
- v1.5.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.5.0-form>
- v1.6.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.6.0-brand>
- v1.6.1：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.6.1-layout>
- v1.7.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.7.0-gamut>
- v1.7.1：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.7.1-rot>
- v1.8.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.8.0-avsync>
- v1.9.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.9.0-phase>
- v1.10.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.10.0-audio>
- v1.10.1：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.10.1-logo>
- v1.11.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.11.0-reference>

## 校验哈希（确认手里的是哪一版）

| 文件 | 大小 | SHA256（前 16 位，完整值见 `_backup\build-records.txt`） |
|---|---|---|
| `VideoScopePad-v1.0.0-monitor-unsigned.ipa` | 0.34 MB | `21AD96947C0250CB…` |
| `VideoScopePad-v1.1.0-quad-unsigned.ipa` | 0.39 MB | `ED016D218EF5917D…` |
| `VideoScopePad-v1.2.0-assist-unsigned.ipa` | 0.42 MB | `4D4FA00688063CD0…` |
| `VideoScopePad-v1.3.0-stream-unsigned.ipa` | 0.50 MB | `52DF03D6064DC039…` |
| `VideoScopePad-v1.3.1-fix-unsigned.ipa` | 0.49 MB | `4C1285F786196AD4…` |
| `VideoScopePad-v1.4.0-srt-unsigned.ipa` | 2.44 MB | `3C2BF0A9545CFDFF…` |
| `VideoScopePad-v1.4.1-fix-unsigned.ipa` | 2.46 MB | `250438E3F0160966…` |
| `VideoScopePad-v1.5.0-form-unsigned.ipa` | 2.51 MB | `4EADF1A0276CAADA…` |
| `VideoScopePad-v1.6.0-brand-unsigned.ipa` | 4.24 MB | `74D67AC763388F15…` |
| `VideoScopePad-v1.6.1-layout-unsigned.ipa` | 4.25 MB | `6DCCE87111EB8711…` |
| `VideoScopePad-v1.7.0-gamut-unsigned.ipa` | 4.27 MB | `DBE884071DCB3894…` |
| `VideoScopePad-v1.7.1-rot-unsigned.ipa` | 4.28 MB | `E12E4D5A0DE23F87…` |
| `VideoScopePad-v1.8.0-avsync-unsigned.ipa` | 4.33 MB | `7DB6AD03F771FEB4…` |
| `VideoScopePad-v1.9.0-phase-unsigned.ipa` | 4.35 MB | `AD9C5E58F9C4C34D…` |
| `VideoScopePad-v1.10.0-audio-unsigned.ipa` | 4.36 MB | `B5487B8C72BDB308…` |
| `VideoScopePad-v1.10.1-logo-unsigned.ipa` | 4.34 MB | `CF998675DBCC0FDE…` |
| `VideoScopePad-v1.11.0-reference-unsigned.ipa` | 4.35 MB | `2A8EC0592F8592D7…` |

PowerShell 校验：

```powershell
Get-FileHash .\dist\VideoScopePad-v1.11.0-reference-unsigned.ipa -Algorithm SHA256
```

## 说明

* `main` 分支始终是最新版；旧版只存在于 tag / `legacy/*` 分支上，不会被后续提交覆盖。
* 每次要发新版前，建议照这个套路固定一次：`git tag -a v1.x.y-xxx <提交> -m "说明"` + `git push origin --tags`，再把 IPA 挂到 Release 上。
* 待办清单（Peak Hold / 斑马纹 / 超标报警）仍在 `NEXT-STEPS.md` 里冻结着，与本次改版无关。
