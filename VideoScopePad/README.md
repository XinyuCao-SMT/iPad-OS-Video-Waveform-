# VideoScopePad（Xcode 工程目录）

iPad 上的 UVC 采集卡监视器 + 示波器 + LUT。
**完整的说明、构建步骤、Windows 侧载流程都在仓库根目录的 `README.md` 里**，这里只放最短的操作路径。

## 有 Mac

```bash
open VideoScopePad.xcodeproj       # Xcode 16 / 15 都能打开
```

1. target `VideoScopePad` → *Signing & Capabilities* → 选自己的 Team（免费 Apple ID 即可）。
2. 连上 iPad，⌘R 运行。

## 没有 Mac（只有 Windows）

1. 把仓库推到 GitHub；
2. 仓库 → **Actions** → **Build unsigned IPA** → **Run workflow**；
3. 下载 Artifacts 里的 `VideoScopePad-unsigned-ipa`，解压出 `VideoScopePad-unsigned.ipa`；
4. Windows 上装 iTunes + [Sideloadly](https://sideloadly.io/)，拖入 IPA、填 Apple ID、Start；
5. iPad：设置 → 通用 → VPN 与设备管理 → 信任开发者。

## 维护工具

```bash
node tools/generate-xcodeproj.mjs   # 增删源文件后重新生成 .xcodeproj
node tools/check-sources.mjs        # 括号配平 / 跨文件重名 / 共享宏是否缺失（Windows 上没法编译，用这个兜底）
```

## 目录

| 目录 | 内容 |
| --- | --- |
| `App/` | 入口与全局设置（含 UserDefaults 持久化） |
| `Capture/` | UVC 设备发现、格式协商、帧回调、统计 |
| `Model/` | 枚举、布局计算（Metal 与 SwiftUI 共用的唯一来源）、几何辅助 |
| `Render/` | Metal 上下文、逐帧渲染、示波器统计引擎、信号幅度数值读数、`.cube` 解析与 LUT 纹理、协调器 |
| `Views/` | 监视器 MTKView、示波器刻度叠加、HUD、控制栏、设置面板 |
| `Shaders/` | `ShaderTypes.h`（Swift/Metal 共享）+ 显示着色器 + 示波器 compute kernel |
