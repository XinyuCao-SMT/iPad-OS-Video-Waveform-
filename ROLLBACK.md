# 回滚指南 · 版本保留说明

十个版本都已固定成**不可变的历史点**，随时可以退回去。每个版本都有独立 tag、独立源码归档、独立 IPA（本地 + GitHub Release 永久附件）。

## 十个版本

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
| **v1.6.1 布局修复版**<br>（最新，**推荐装**） | `0a8d4c61` | tag `v1.6.1-layout` | 四分割逐格修正：波形/Parade 绘图区**铺满格子**（纵向才是标定过的幅度轴，不再按 2:1 / 6:1 内缩成中间一条）；「刻度栏 + 绘图区」整体居中；内边距按短边、小格子自动收窄刻度栏；矢量图放大时超出绘图区的圈/目标框整圈不画；刻度栏与峰值标注裁剪在格子内；新增**布局调试叠加层**（设置 → 显示）；应用内版本号接通（1.6.1） | `dist/VideoScopePad-v1.6.1-layout-unsigned.ipa` |

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

git checkout v1.0.0-monitor     # 或 v1.1.0-quad / v1.2.0-assist / v1.3.0-stream / v1.3.1-fix / v1.4.0-srt / v1.4.1-fix / v1.5.0-form / v1.6.0-brand / legacy/v1.0.0-monitor
# …要验证/构建就在这个状态跑 tools/ci-cycle.ps1

git checkout main               # 回到最新版
git log --oneline -1            # 确认当前位置
```

GitHub 上十个 tag 都已推送，换台机器 `git clone` + `git checkout <tag>` 同样有效。

### 方式 C：完全离线恢复（连 git / GitHub 都没有）

解压这些归档即可得到完整工程（含 Xcode 工程、scheme、CI 工作流）：

```
E:\harness\_backup\src-v1.6.1-layout-<sha>.zip          ← 最新版源码
E:\harness\_backup\src-v1.6.0-brand-8c9709e02.zip       ← 上一版源码
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

十个版本的 IPA 都已挂到对应 Release 的附件里，**永久可下载**：

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

PowerShell 校验：

```powershell
Get-FileHash .\dist\VideoScopePad-v1.6.1-layout-unsigned.ipa -Algorithm SHA256
```

## 说明

* `main` 分支始终是最新版；旧版只存在于 tag / `legacy/*` 分支上，不会被后续提交覆盖。
* 每次要发新版前，建议照这个套路固定一次：`git tag -a v1.x.y-xxx <提交> -m "说明"` + `git push origin --tags`，再把 IPA 挂到 Release 上。
* 待办清单（Peak Hold / 斑马纹 / 超标报警）仍在 `NEXT-STEPS.md` 里冻结着，与本次改版无关。
