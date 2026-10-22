
---

## 声画延时的基准：彩条前后沿脉冲（用户决定）

**决定（取代之前那套）**：`Sync-it` 素材与 ffmpeg 导帧那套**不再使用**。
以后一律以 **「彩条前后沿脉冲」** 为准 —— 不需要外部基准文件、不需要便携 ffmpeg。

**怎么算**
- **前沿**：彩条出现的那一刻 ↔ 1 kHz 千周声的**起始**（起音）
- **后沿**：彩条消失的那一刻 ↔ 1 kHz 千周声的**结束**（收音）
- 两次测量应当**一致**；不一致说明链路有抖动、或用时基不同源，或配对错了轮次。
  这比单个事件更能暴露问题：任何"系统性提前/滞后"都会同时出现在前后沿上，
  而"配对错一个周期"只会让其中一次偏离整整一个脉冲周期。

**现场用法**
1. 播「彩条 + 前后各一声千周」的测试信号（信号源自带，或用本机合成信号）
2. 选音频输入（优先与画面同源的采集卡/内嵌音频）
3. 读「延时」值；用「固定补偿」把本程序的固有滞后归零（见 `SyncOffsetMs` 的注释：
   实测约 9 帧 @40fps ≈ 227 ms，属实现固有延迟，不是链路延时）
4. 前后沿各读一次，两次之差就是当前测量的**可信度**

**已废弃**（保留说明，避免以后又绕回来）
- `E:\Files&Document\Sync-it test Video\...Apple ProRes...mov` 之类的 Sync-it 基准文件
- 便携 ffmpeg（`Windows\tools\ffmpeg\`）导帧/导音频求样本级真值的流程
- 代码里 `SyncTestMode.SyncIt` / `SyncTestMode.Auto` 两个枚举值（已删除，只剩 `BarsTone`）

**代码位置**
- 视频侧事件：`VideoScopePad.App\LiveSession.cs` 的 `DetectFlash`（彩条脉冲判定）与 `PairSync`（就近配对 + 中位数）
- 音频侧事件：`VideoScopePad.Win\Audio\AvSyncAnalyser.cs`（包络 → 起音）
- 自检：`dotnet run --project tools\mf-capture -- avsync`（构造脉冲，逐轨分别测算，免硬件）
