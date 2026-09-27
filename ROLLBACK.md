# 回滚指南 · 版本保留说明

两个版本都已固定成**不可变的历史点**，随时可以退回去。改版前的稳定版永久保留。

## 两个版本

| 版本 | 提交 | Tag / 分支 | 内容 | IPA |
|---|---|---|---|---|
| **v1.0.0 监视器版**<br>（改版前的稳定版） | `385d8d0` | tag `v1.0.0-monitor`<br>分支 `legacy/v1.0.0-monitor` | UVC 监视 + 矢量示波器 + 亮度波形 + RGB Parade + `.cube` LUT + 信号幅度读数 + 冻结/调色 + 布局预设（仅画面/底部/右侧/叠加） | `dist/VideoScopePad-v1.0.0-monitor-unsigned.ipa` |
| **v1.1.0 四分割版**<br>（最新） | `99ad8ec` | tag `v1.1.0-quad` | 在 v1.0.0 上增加：全屏/四分割可配内容、示波器侧边大号刻度（IRE/mV/%）、等效 mV 与信号信息自动显示、自动选择输入格式 | `dist/VideoScopePad-v1.1.0-quad-unsigned.ipa` |

> v1.0.0 对应的是**第一次云端构建成功**的那份代码（CI run #9）。它没有四分割逻辑，已核对过归档内容。

## 三种回滚方式

### 方式 A：只把 iPad 上的 App 换回旧版（最快，不动代码）

1. 用 **Sideloadly** 装 `dist/VideoScopePad-v1.0.0-monitor-unsigned.ipa`
2. 或从 GitHub Release 下载（**永久有效**，不受工件 30 天限制）：
   <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.0.0-monitor>
3. 同一个 Bundle ID 重装属于升级/降级安装，**设置和 LUT 通常保留**；保险起见可先把 iPad 上「文件 → 本应用 → LUTs」里的 `.cube` 拷一份出来

### 方式 B：把源码回滚到旧版

```bash
cd "E:\harness\iPad OS Software Waform"

git checkout v1.0.0-monitor     # 回到旧版（或 git checkout legacy/v1.0.0-monitor）
# …要验证/构建就在这个状态跑 tools/ci-cycle.ps1

git checkout main               # 回到最新版
git log --oneline -1            # 确认当前位置
```

GitHub 上两个 tag 都已推送，换台机器 `git clone` + `git checkout <tag>` 同样有效。

### 方式 C：完全离线恢复（连 git / GitHub 都没有）

解压这个归档即可得到完整工程（含 Xcode 工程、scheme、CI 工作流）：

```
E:\harness\_backup\src-v1.0.0-monitor-385d8d0.zip
```

## 构建工件地址（GitHub Actions，30 天后过期）

| 版本 | CI 运行 |
|---|---|
| v1.0.0-monitor | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36291748106> |
| v1.1.0-quad | <https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/actions/runs/36299560754> |

两个版本的 IPA 都已挂到对应 Release 的附件里，**永久可下载**：

- v1.0.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.0.0-monitor>
- v1.1.0：<https://github.com/XinyuCao-SMT/iPad-OS-Video-Waveform-/releases/tag/v1.1.0-quad>

## 校验哈希（确认手里的是哪一版）

| 文件 | 大小 | SHA256 |
|---|---|---|
| `VideoScopePad-v1.0.0-monitor-unsigned.ipa` | 0.34 MB | `21AD96947C0250CB114F8F752920CE233A809E80CB826320DCDE9AF4FDD8C4BF` |
| `VideoScopePad-v1.1.0-quad-unsigned.ipa` | 0.39 MB | `ED016D218EF5917D33FDC9CE0AFAA42277E607DBE1070D8660F986E723BA843B` |

PowerShell 校验：

```powershell
Get-FileHash .\dist\VideoScopePad-v1.0.0-monitor-unsigned.ipa -Algorithm SHA256
```

## 说明

* `main` 分支始终是最新版；旧版只存在于 tag / `legacy/*` 分支上，不会被后续提交覆盖。
* 每次要发新版前，建议照这个套路固定一次：`git tag -a v1.x.y-xxx <提交> -m "说明"` + `git push origin --tags`，再把 IPA 挂到 Release 上。
* 待办清单（Peak Hold / 斑马纹 / 超标报警）仍在 `NEXT-STEPS.md` 里冻结着，与本次改版无关。
