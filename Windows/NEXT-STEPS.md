
---

## 校准工具与测试文件（用户提供的基准）

**测试文件**：`E:\Files&Document\Sync-it test Video\Sync-it 50fps 1080i Apple Pro Res PCM audio.mov`
- 252.8 MB，`ftypqt` 容器 + `icpf`（**Apple ProRes**）+ PCM 音频
- 用途：**声画延时校准的基准**（用户正式测试时会用这个文件）

**解码工具：ffmpeg（便携版，不进发行包）**
- 位置：`Windows\tools\ffmpeg\`（工作区内，不写系统 PATH）
- **明确决定：打包时**不带**（用户确认）** —— `dist\` 只放应用 exe，体积不变
- 获取：`https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip` 解压到上面目录
- 用法：
  - `ffprobe -hide_banner <文件>` 看真实参数（帧率/场序/音频格式）
  - `ffmpeg -i <文件> -vf fps=50 frames\%05d.png` 导帧，用来定位"画面跳变帧"
  - `ffmpeg -i <文件> -vn -acodec pcm_f32le -f f32le audio.raw` 导音频，用来定位"1 kHz 千周起始样本"

**校准闭环（下一步）**
1. 用 ffmpeg 从该文件解出"画面跳变"与"1 kHz 起始"的样本级真值（**基准**）
2. 应用内加"视频闪变检测"，与 `AvSyncAnalyser`（音频起音）合成实时"彩条 + 千周"一键测量
3. 用基准校验实时测量 —— 偏差即算法误差