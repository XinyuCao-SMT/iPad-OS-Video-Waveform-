# 分工规定（两个并行会话）

本仓库同时有两个会话在推进，**职责严格分开**，避免互相踩：

| 会话 | 负责范围 | 禁止范围 |
|---|---|---|
| **Windows 会话** | `Windows/` 下的一切：`VideoScopePad.Win/`、`VideoScopePad.App/`、`tools/`、`dist/win-*`、`Windows/*.md`、`Windows/release.ps1` | **不改** `VideoScopePad/`（iPad Swift 源码）、`dist/VideoScopePad-v*.ipa`、`ROLLBACK.md`、`CLOUD-BUILD.md`、`.github/workflows/build-ipa.yml` |
| **iPad 会话** | `VideoScopePad/`（Swift）、`dist/VideoScopePad-v*.ipa`、`ROLLBACK.md`、`CLOUD-BUILD.md`、`.github/workflows/build-ipa.yml` | **不改** `Windows/` 下任何文件 |

## 版本号规则

- Windows：`win-v0.x.y-代号`，产物 `dist/win-v0.x.y-代号/`，回滚表 `Windows/ROLLBACK-Windows.md`
- iPad：`v1.x.y-代号`，产物 `dist/VideoScopePad-v1.x.y-代号-unsigned.ipa`，回滚表 `ROLLBACK.md`

## 发版前必须做的检查（两个会话都一样）

1. **先看有没有别人的更新**：`git log --oneline -10` + `git ls-remote --heads origin main`，
   确认没有未合入的提交、没有同号的版本
2. **版本必须递增**：本次版本要大于各自已发布的最大版本
   （Windows 侧已强制：`Windows/release.ps1` 第 0 步会拒绝倒挂版本并发提示）
3. **验证通过才发版**：Windows 走独立自检闸门；iPad 走 GitHub Actions `build-ipa.yml` 编译
4. **每版留痕**：独立文件夹 + 清单（SHA256/大小/源码提交/自检或 CI 运行号）+ tag + 回滚表条目

## 为什么定这个规矩

2026-10-21：两个会话同时改同一分支，一边发到 0.14.3、另一边又发了 0.13.2，
出现**版本号倒挂**（最新提交号更小），清单与回滚表无法对齐 —— 靠事后改号才修好。