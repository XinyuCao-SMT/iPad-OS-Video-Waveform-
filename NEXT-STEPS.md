# 下一步待办（已冻结，等你说「好了」再动）

> 这份文件的用途：你在自己编辑工程期间，这里记录**已经明确要做、但故意先不做**的改动。
> 你编辑完打招呼，我照这份清单继续，并先跟你确认顺序。

---

## 0. 当前冻结状态（2025 冻结点）

* 编译源文件 **24** 个；`tools/check-sources.mjs` 全绿（括号配平 / 跨文件重名 / Swift-Metal 共享宏 / ViewBuilder 10 个子视图上限）。
* `project.pbxproj`：定义 74、唯一引用 74、**悬空引用 0**。
* 工程可用 Xcode 15/16 打开，最低 iPadOS 17.0，仅限 USB-C 的 iPad（iPad mini 6 及以后）。
* ⚠️ **仍未在真机 / Xcode 上编译过**（开发机是 Windows）。首次构建如果有报错，贴给我改。
* 冻结期间请不要动这些名字，否则我回来要对齐：`ShaderTypes.h` 里的宏、`VSRenderUniforms` / `VSScopeUniforms` 字段顺序、`VS_MEASURE_*` 布局。

**冻结期间仍允许改的（与 App 代码无关）：** CI 工作流 `.github/workflows/build-ipa.yml`、
文档 `README.md` / `CLOUD-BUILD.md` / 本文件、仓库卫生文件 `.gitignore` / `.gitattributes`、
以及 `VideoScopePad/tools/` 下两个 Node 脚本。这些都不参与 App 编译，改它们不会影响冻结。
（最近一次：应你要求，把云端编译那一步的 CI 做扎实了 —— 错误摘要自动汇总、失败时上传完整日志、
打包前校验并兜底补上相机权限说明、可选手动选 Release/Debug。）

---

## 1. 真峰值保持 Peak Hold

**目标**：把一段时间内的最高 / 最低电平用游标钉在波形图上，方便看守。

* 数据源已经有了：`SignalMeasurement.stableWhiteIRE` / `stableBlackIRE` / `redPeakIRE` 等，`SignalMeasurementBuilder` 每 6 帧算一次。
* 做法：在 `Render/SignalMeasurement.swift` 里加一个 `PeakHoldTracker`（保持时间 + 衰减速率两个参数，边沿上升立即更新、否则按时间衰减），不用新文件。
* 显示：`Views/ScopeGraticuleView.swift` 里画两条横向游标线 —— 复用现有 `y(forIRE:)` 换算，和 IRE 刻度天然对齐。
* 设置：`AppSettings` 加 `peakHoldEnabled` / `peakHoldSeconds`；控制栏加一个开关 chip。

## 2. 斑马纹 Zebra

**目标**：超过设定 IRE 阈值的区域直接斜纹标出，比读数字直观。

* 落点：`Shaders/DisplayShaders.metal` 的 **`fsDisplay`**（显示通道那一步）。
* 做法：用 R'G'B' 算 luma，超阈值就按屏幕坐标做 45° 斜纹 `frac((x + y) / period) < 0.5` 叠加；阈值 + 开关塞进 `VSRenderUniforms` 里现在还空着的字段（`flags.y` / `flags.z`），不用改结构体大小。
* ⚠️ 关键约束：**只能放在 `fsDisplay`，绝不能放进 `fsApplyLUTAndGrade`** —— 否则斑马纹会被示波器统计进去，读数全错。
* 建议阈值档位：70 / 90 / 100 IRE，另加一个「低于 0 IRE」的反向斑马（查黑位切割）。

## 3. 超标报警

**目标**：长时间录制看守时，出问题立刻看得见。

* 判定逻辑已经齐了：`SignalMeasurement.warnings`（超白 / 超黑 / 白电平偏高 / 黑位被压缩 / 黑位抬高 / 色度超范围 / 整帧全黑）。
* 做法：加一层**边沿触发**（连续 N 次命中才算一次事件，避免逐帧闪烁），命中时：
  * 监视器区域外框描红（`Views/ContentView.swift` 的 ZStack 上加 SwiftUI overlay 描边）；
  * 可选振动提醒 `UIFeedbackGenerator`（只在事件边沿触发，不要每帧震）。
* 需要一个小的状态机（`WarningLatch`），放 `SignalMeasurement.swift` 里即可。

---

## 4. 更远的 backlog（没确认过，只是备着）

* HDR / HLG / PQ：`fsVideoBiPlanar` 之后插一段 HLG/PQ → 线性 → tone map，示波器统计放在 tone map 之后就是标准 SDR 读数。
* 10bit P010 输入管线（需要确认 iPad 侧 UVC 是否给出 P010 格式）。
* 录音（HDMI 内嵌音频）→ 需要加 `AVCaptureAudioDataOutput` + 音频表。
* 干净画面外送（把无 UI 的画面送到外接 HDMI / AirPlay）。
* 直方图（RGB 直方图面板）与 YCbCr Parade。
