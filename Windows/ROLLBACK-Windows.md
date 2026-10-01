# 回滚指南 · Windows 版

与 iPad 版那套（`../ROLLBACK.md`）同一个思路：**一个版本一个文件夹 + 独立清单 + 独立 tag**。
区别只在于产物是 `exe` 而不是 `ipa`，而且 Windows 这边**不需要重签、不需要 CI**——
本机 `dotnet` 直接编译，所以回滚可以做到「拿起旧文件夹里的 exe 就跑」。

## 版本表

| 版本 | 提交 | Tag | 内容 | exe |
|---|---|---|---|---|
| **win-v0.1.0 监视器版**<br>（第一个可回滚版本） | `cb0060d` | tag `win-v0.1.0-monitor` | Media Foundation UVC 采集（枚举 / 原生格式 / 色彩元数据 / 1 秒采集）、D3D11 + HLSL 示波器（波形 / 矢量 / Parade）、刻度层（IRE·mV·% 刻度栏 + 网格 + 75% 目标框 + 肤色线）、**幅度读数**（峰值 / 稳定 / 黑位 / 平均 / 色度 / R·G·B / 超白超黑）、**冻结参考层**（琥珀幽灵 + 参考读数与 Δ）、峰值保持游标、单文件 exe | `dist/win-v0.1.0-monitor/VideoScopePad-win-v0.1.0-monitor-unsigned.exe` |

> 还没有的：格子内容可选（逐格换示波器）、钻石图与马蹄图的刻度、LUT、音频套件。
> 冻结参考层与读数已就位，但**「超标报警 / 斑马纹」还没移植**。

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
