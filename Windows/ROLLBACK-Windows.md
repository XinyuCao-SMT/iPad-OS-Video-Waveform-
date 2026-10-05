# 回滚指南 · Windows 版

与 iPad 版那套（`../ROLLBACK.md`）同一个思路：**一个版本一个文件夹 + 独立清单 + 独立 tag**。
区别只在于产物是 `exe` 而不是 `ipa`，而且 Windows 这边**不需要重签、不需要 CI**——
本机 `dotnet` 直接编译，所以回滚可以做到「拿起旧文件夹里的 exe 就跑」。

## 版本表

| 版本 | 提交 | Tag | 内容 | exe |
|---|---|---|---|---|
| **win-v0.6.0 音频分析版**<br>（最新，**推荐装**） | `fc7acc4` | tag `win-v0.6.0-audio-dsp` | 在 0.5.0 基础上加入**音频分析层**（尚未接界面）：BS.1770 响度（对 EBU Tech 3341 标准值）、1/3 倍频程频谱（ISO 带中心）、声相（相关性 + 李萨如）、**逐轨声画延时**、8 声道电平表（峰值保持/CLIP 锁存）、一帧音频报告；视频侧功能与 0.5.0 相同 | `dist/win-v0.6.0-audio-dsp/VideoScopePad-win-v0.6.0-audio-dsp-unsigned.exe` |
| **win-v0.5.0 工具版** | `b632bd4` | tag `win-v0.5.0-tools` | 在 0.4.0 基础上：**LUT（.cube）解析与显示**（恒等/反相/强度逐像素验证）、**读数 CSV 导出**（表头与 iPad 逐字一致 + 界面按钮与记录开关）、**布局预设**（底部条/右侧栏/叠加）与**画面方向**（自动/不转/顺逆90/180）、**应用图标与署名**、**顶部信号信息行**、**布局调试叠加层** | `dist/win-v0.5.0-tools/VideoScopePad-win-v0.5.0-tools-unsigned.exe` |
| **win-v0.4.0 看守版** | `263759e` | tag `win-v0.4.0-guard` | 在 0.3.1 基础上：**斑马纹**（超白 70–100 IRE 可调 / 黑切割，带偏黄与偏蓝区分）、**超标报警**（超白·超黑·白电平·黑位·色度·整帧全黑，边沿触发锁存 + 红框 + 报警条 + 确认门槛）、**选中坏设备不再卡死**（NDI 虚拟摄像头这类会让读取永久阻塞的设备改为"先探测后打开"）、诊断日志（UI/渲染心跳与停摆检测） | `dist/win-v0.4.0-guard/VideoScopePad-win-v0.4.0-guard-unsigned.exe` |
| **win-v0.3.1 界面修复版** | `a3b0ad5` | tag `win-v0.3.1-ui` | 修 0.3.0 的两个界面问题：**下拉项白底白字**（弹出列表没跟着深色主题，看着像"全是灰的、点不动"）、**自动选中的设备没送达会话**（下拉显示采集卡但画面还是合成信号）；另外下拉展开时不再重建列表 | `dist/win-v0.3.1-ui/VideoScopePad-win-v0.3.1-ui-unsigned.exe` |
| **win-v0.3.0 多设备版** | `d8cd41b` | tag `win-v0.3.0-multidev` | 在 0.2.0 基础上：**多采集卡选择**（信号源下拉来自实时枚举 + 刷新按钮 + 记住上次选择）、**热插拔恢复**（拔掉显示「等待设备接入」，插回自动重开）、**色彩矩阵可覆盖**（跟随驱动 / 强制 601 / 强制 709，驱动谎报时黄字提示）、`mf-capture bars` 彩条校对命令、`--list-devices` | `dist/win-v0.3.0-multidev/VideoScopePad-win-v0.3.0-multidev-unsigned.exe` |
| **win-v0.2.0 布局版** | `db699a6` | tag `win-v0.2.0-layout` | **格子内容可选**（全屏 / 四分割逐格换：画面 · 波形亮度 · 波形 RGB 叠加 · Parade · 矢量 · 钻石图 · 马蹄图），引擎按可见格子决定要算什么；**钻石图刻度**（上下菱形 100%/75%/等值线 + 灰阶竖线 + W/B/G/R 标注）与**马蹄图刻度**（CIE 1931 光谱轨迹 380–700nm + BT.709/2020 三角 + D65） | `dist/win-v0.2.0-layout/VideoScopePad-win-v0.2.0-layout-unsigned.exe` |
| **win-v0.1.0 监视器版** | `cb0060d` | tag `win-v0.1.0-monitor` | Media Foundation UVC 采集（枚举 / 原生格式 / 色彩元数据 / 1 秒采集）、D3D11 + HLSL 示波器（波形 / 矢量 / Parade）、刻度层（IRE·mV·% 刻度栏 + 网格 + 75% 目标框 + 肤色线）、幅度读数（峰值 / 稳定 / 黑位 / 平均 / 色度 / R·G·B / 超白超黑）、冻结参考层（琥珀幽灵 + 参考读数与 Δ）、峰值保持游标、单文件 exe | `dist/win-v0.1.0-monitor/VideoScopePad-win-v0.1.0-monitor-unsigned.exe` |

> 还没有的：LUT 的**界面入口**（载入 .cube / 强度 / 示波器取样 LUT 前后）；音频套件（WASAPI：
> 实际声道数、8 条电平表、每通道与整体 BS.1770 响度、可选声道对声相、1/3 倍频程频谱、逐轨延时）。

## 三种回滚方式

### 方式 A：只把手上的 exe 换回旧版（最快，不用编译）

```powershell
# 旧版都还留在 dist\ 下，一个版本一个文件夹
Get-ChildItem dist\win-*\*.exe | Select-Object FullName, Length, LastWriteTime

# 直接跑旧的那一个（例如回到 v0.1.0）
dist\win-v0.1.0-monitor\VideoScopePad-win-v0.1.0-monitor-unsigned.exe
```

`dist\` 不进仓库（`.gitignore`），所以这是**本机**的回滚路径；要跨机器就用方式 B 或 C。

### 方式 B：把源码回滚到旧版

```powershell
cd "E:\harness\iPad OS Software Waform"
git tag -l "win-v*"                  # 看有哪些版本
git checkout win-v0.1.0-monitor      # 切到那一版的源码
Windows\run-app.cmd                  # 直接编译 + 开窗口（本机开发循环，不用 CI）

git checkout main                    # 回到最新版
git log --oneline -1                 # 确认当前位置
```

### 方式 C：完全离线恢复

```powershell
# 旧版产物本身就是一个可执行文件，拷到任何 Windows 10/11 机器双击即可
# （自包含单文件：目标机不用装 .NET；唯一系统依赖是系统自带的 d3dcompiler_47.dll）
copy dist\win-v0.1.0-monitor\VideoScopePad-win-v0.1.0-monitor-unsigned.exe D:\备份\
```

源码层面还可以按 `Windows\README-Windows.md` 的「本机开发循环」在任意机器上重建：
只需要 .NET 8 SDK，不需要 Visual Studio、不需要联网（NuGet 包首次要联网还原）。

## 校验哈希（确认手里的是哪一版）

| 文件 | 大小 | SHA256 |
|---|---|---|
| `VideoScopePad-win-v0.6.0-audio-dsp-unsigned.exe` | 72,098,546 字节（68.8 MB） | `0C2AD8B792EB129B6D9120313B1F503F063C9D9E6BD4D412B50231B42B278EEB` |
| `VideoScopePad-win-v0.5.0-tools-unsigned.exe` | 72,086,311 字节（68.7 MB） | `A4197882127B49D116FA5CC3341E1D9FFA3CD2B9ED66C1E8ACED16E5B94F9D65` |
| `VideoScopePad-win-v0.4.0-guard-unsigned.exe` | 见 MANIFEST.txt | `7FB5E6CC80DDFDBF8D041248A4CD45050B3F1BA6B89D26A33A425BE8528BBE70` |
| `VideoScopePad-win-v0.3.1-ui-unsigned.exe` | 72,022,633 字节（68.7 MB） | `D317644DBE32B683465DC0D14CE73AB4F7963C67B65B2E2CBE5F3572E8126E65` |
| `VideoScopePad-win-v0.3.0-multidev-unsigned.exe` | 72,022,043 字节（68.7 MB） | `3043DF14D980B1D995FA877205641AD0E6AE2183403CB082E9CD9F64A4332A79` |
| `VideoScopePad-win-v0.2.0-layout-unsigned.exe` | 72,015,252 字节（68.7 MB） | `790CCBF1678D33395FD5F4B2B057FA266697F628F66EDDBD1AFC07D3AE10DF4E` |
| `VideoScopePad-win-v0.1.0-monitor-unsigned.exe` | 72,007,429 字节（68.7 MB） | `33D08C2A02907A68229CC089D32C888CD572D166ABE830AC15725A6238A3B76B` |

```powershell
Get-FileHash .\dist\win-v0.1.0-monitor\VideoScopePad-win-v0.1.0-monitor-unsigned.exe -Algorithm SHA256
```

每个版本文件夹里的 `MANIFEST.txt` 还记着**当时那次独立自检的逐项结果**
（把 exe 单独拷到空目录里跑 `--snapshot`，证明自包含 + 着色器已内嵌），
所以"这一版当初是不是好的"是可查的，不用靠记忆。

## 怎么发新的一版

```powershell
# 1) 先把源码提交干净（清单会记提交号；工作区有改动会标注出来）
git add -A; git commit -m "..."

# 2) 发版：编译 → 单文件 exe → dist\win-<tag>\ → 清单 → 打 tag
powershell -ExecutionPolicy Bypass -File Windows\release.ps1 -Tag win-v0.1.1-fix -Note "修 xxx"
```

`release.ps1` 会**先做独立自检，不通过就不发版**（直接抛错、不留半成品文件夹）。

## 说明

* `main` 始终是最新版；旧版只存在于 tag 上，不会被后续提交覆盖。
* Windows 这边的 tag 命名前缀是 **`win-v…`**，与 iPad 版的 `v1.x.y-…` 区分开，
  两套版本号互不干扰（同一个仓库里可以并存）。
* iPad 的 IPA 挂在 GitHub Release 附件上做永久备份；Windows 的 exe 目前只在
  `dist\win-<tag>\`（本机）。要不要也挂 Release / 拆独立仓库，见 `NEXT-STEPS.md` 里的待定项。
