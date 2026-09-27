# 用 GitHub 云端 Mac 编译出 IPA（Windows 全流程详解）

这份文档只讲一件事：**你手上只有 Windows，怎么把这份代码变成能装进 iPad 的 App。**

一句话原理：iOS/iPadOS 应用必须用 Apple 的 SDK 和 Metal 着色器编译器构建，这些东西只在 macOS 上跑；
GitHub 给每个仓库提供**免费的 macOS 虚拟机**，我们把构建脚本交给它跑，产出未签名 IPA 下载回来，
再用你自己的 Apple ID 在 Windows 上签名安装。

---

## 0. 先看这个：预期、时间、费用

| 项目 | 实际情况 |
| --- | --- |
| 首次总耗时 | 约 15–25 分钟（注册 + 推代码 + 第一次跑 CI） |
| 之后每次构建 | 4–6 分钟 |
| 费用 | **公开仓库：完全免费且不限量**；私有仓库：每月免费 2000 分钟，但 macOS 按 **10 倍**计费，等于约 200 分钟/月 ≈ 30–40 次构建 |
| 产出物 | 一个未签名的 `VideoScopePad-unsigned.ipa`（约 3–6 MB） |
| 是否需要 Mac | 不需要 |
| 是否需要 Apple 开发者账号 | 不需要。免费 Apple ID 即可，代价是签名 7 天过期 |
| 你的 Apple ID 会不会交给 GitHub | **不会**。GitHub 只负责编译，签名完全在你自己的 Windows 电脑上做 |

---

## 1. 前置：GitHub 账号与仓库

1. 有 GitHub 账号就登录，没有就注册（免费）：<https://github.com/signup>
2. 右上角 **+** → **New repository**
3. 填写：
   * **Repository name**：`VideoScopePad`
   * **Public / Private**：看上面的费用表。只是自己用、不介意代码公开 → 选 **Public**（省额度）
   * **不要**勾 Add a README / .gitignore / license（我们本地已经有了，勾了会冲突）
4. 点 **Create repository**，页面上会给你一个地址，形如
   `https://github.com/你的用户名/VideoScopePad.git` ← 第 2 步要用

---

## 2. 把代码推上去（三种方式，选一种）

### 方式 A：命令行（推荐，你机器上已经装了 git 2.55）

打开 PowerShell，逐行执行：

```powershell
cd "E:\harness\iPad OS Software Waform"

git init
git add .
git commit -m "VideoScopePad: iPad UVC 监视器 + 示波器 + LUT"
git branch -M main
git remote add origin https://github.com/你的用户名/VideoScopePad.git
git push -u origin main
```

**关于登录**：第一次 `git push` 会弹出一个浏览器窗口让你登录 GitHub（Git for Windows 自带的
Credential Manager 负责这件事），登录一次之后就不用再登了。

如果没弹窗、而是直接在命令行要账号密码：**密码栏不能填 GitHub 登录密码**，要填
Personal Access Token：
GitHub → 右上角头像 → **Settings** → 左下 **Developer settings** → **Personal access tokens**
→ **Tokens (classic)** → **Generate new token (classic)** → 勾 **repo** → 生成 → 复制那串 `ghp_...`
→ 粘到密码栏。

推送成功后，命令行应该显示 `Branch 'main' set up to track remote branch 'main'`。

### 方式 B：GitHub Desktop（不想碰命令行）

1. 下载安装 <https://desktop.github.com/>
2. 登录 GitHub 账号
3. 菜单 **File → Add local repository**，选 `E:\harness\iPad OS Software Waform`
   （如果提示这不是个仓库，就点 "create a repository"，路径不变）
4. 左下角填提交信息 → **Commit to main** → 右上 **Publish repository**
   （不想公开就把 "Keep this code private" 勾上）

### 方式 C：网页拖拽上传（最省事，但有个坑）

1. 在新建好的仓库页面点 **uploading an existing file**
2. 把 `README.md`、`CLOUD-BUILD.md`、`.gitignore` 和 **整个 `VideoScopePad` 文件夹**拖进去
3. 填提交信息 → **Commit changes**

⚠️ **坑**：网页上传有时会漏掉以点开头的目录，也就是 `.github`。
传完后到仓库首页确认有没有 `.github/workflows/build-ipa.yml` 这个文件。**没有的话**：
仓库首页 → **Add file** → **Create new file** → 文件名框里直接输入
`.github/workflows/build-ipa.yml`（斜杠会自动建目录）→ 把本仓库里那个文件的内容整段粘进去 → Commit。

### 传完自检

仓库首页应该能看到这些：

```
.github/workflows/build-ipa.yml     ← 没有它就没法云端编译
.gitignore
CLOUD-BUILD.md
README.md
VideoScopePad/                      ← 【必须有】里面是 VideoScopePad.xcodeproj 和源码子目录
```

⚠️ **最常见的翻车点**：只把工作流文件建好了，源码没传上来。这时 CI 会报一个很难懂的错：

```
An error occurred trying to start process '/bin/bash' with working directory
'/Users/runner/work/你的仓库/你的仓库/VideoScopePad'. No such file or directory
```

这句话的真实含义是：**检出下来的仓库里没有 `VideoScopePad` 目录**，GitHub 连启动 shell 的工作目录都找不到，
跟代码质量、Xcode 版本都没关系。解决办法就是把源码推上来（见上面三种方式）。

> 现在的工作流已经不依赖固定目录名了，它会在仓库里自己搜 `VideoScopePad.xcodeproj`：
> 工程在根目录、在 `VideoScopePad/` 子目录、甚至多套一层目录都能编。
> 真的一个 `.xcodeproj` 都找不到时，它会在日志最前面打印出仓库的真实文件树，并给出明确提示。
> 想提前验证这套定位逻辑，可以在本地跑：`node VideoScopePad/tools/ci-locate-selftest.mjs`

---

## 3. 确认 Actions（云端构建）是开着的

1. 点仓库顶部 **Actions** 标签
2. 三种可能：
   * 左侧列表里已经出现 **Build unsigned IPA** → 好的，跳到第 4 步
   * 出现一个绿色横幅 "Workflows aren't being run on this repository" → 点 **Enable** / "I understand my workflows, go ahead and enable them"
   * 左侧列表是空的 → 检查第 2 步的自检：`.github/workflows/build-ipa.yml` 必须在**默认分支（main）**上，
     而且文件必须是合法 YAML（文件里用了 Tab 缩进就会直接失效，本仓库用的是空格，正常）

> 组织（Organization）下的仓库如果被管理员禁用了 Actions，需要管理员去
> Settings → Actions → General 里放开；个人仓库默认就是开的。

---

## 4. 触发一次构建

1. **Actions** → 左侧点 **Build unsigned IPA**
2. 右侧有个 **Run workflow** 下拉按钮 → 点开：
   * **Use workflow from**：`main`
   * **构建配置**：`Release`（想排查编译问题可以选 `Debug`，只影响优化等级，产物一样能装）
3. 点绿色的 **Run workflow**
4. 刷新页面，几秒后会出现一条新的运行记录，状态是黄色小圆点（进行中）

> 除了手动触发，往 main 分支推代码也会自动触发（只改 `README.md` 这类文档不会触发，
> 因为工作流里配了 `paths` 过滤）。

---

## 5. 看日志：每一步在干什么

点进那条运行记录 → 左侧点 job **Build (Release, unsigned)**，你会看到这些步骤：

| 步骤 | 作用 | 正常耗时 |
| --- | --- | --- |
| **Checkout** | 把仓库代码拉到虚拟机 | 2–5 秒 |
| **显示编译工具链** | 打印 macOS / Xcode / iOS SDK 版本；出错时这几个数字很有用 | 5 秒 |
| **编译（未签名）** | 真正的 `xcodebuild`，24 个 Swift/Metal 源文件 + 资源 | **2–4 分钟** |
| **编译失败时提取错误摘要** | 只在失败时运行，把 `error:` 行汇总到页面顶部的 Summary | — |
| **打包未签名 IPA** | 校验 `Info.plist`（含相机权限说明，缺了会补上），打成 `Payload/VideoScopePad.app` → zip 成 IPA | 10 秒 |
| **上传 IPA** | 把 IPA 存成可下载的 artifact | 5–10 秒 |
| **失败时上传完整日志** | 只在失败时运行，附上完整 `build.log` | — |

全部绿色 ✅ = 成功。任何一步红色 ❌ = 失败，跳第 8 节。

---

## 6. 下载 IPA

1. 回到这次运行的**汇总页面**（点运行记录标题，或者 Breadcrumb 里的运行号）
2. 拉到最底部 **Artifacts** 区域
3. 点 **VideoScopePad-unsigned-ipa** 下载 → 得到一个 zip
4. 解压，里面就是 **`VideoScopePad-unsigned.ipa`**

注意：
* 必须**登录 GitHub** 才能下载 artifact
* artifact **30 天后自动过期**（工作流里设的 `retention-days: 30`），过期了重跑一次就有
* 如果构建失败，这里还会有个 `build-log` 工件，是完整编译日志，排查用

---

## 7. 在 Windows 上把这个 IPA 装进 iPad

### 7.1 装依赖

1. **iTunes**：从 <https://www.apple.com/itunes/download/standalone> 下载安装
   （**必须用官网这个版本，不要用 Microsoft Store 版** —— 商店版不带 Apple 设备驱动，Sideloadly 认不到 iPad）
2. **Sideloadly**：从 <https://sideloadly.io/> 下载安装

### 7.2 连接与信任

1. iPad 用 USB-C 数据线连电脑（**注意是数据线，不是只充电的线**）
2. iPad 解锁后弹出「信任此电脑？」→ 点信任并输入锁屏密码
3. 打开 Sideloadly，左上角应该显示你的设备名。没显示就先解决驱动问题（重装官网 iTunes）

### 7.3 打开开发者模式（iPadOS 16 及以后**必须做**）

很多人卡在这里：iPad 上 **设置 → 隐私与安全性 → 开发者模式 → 打开 → 重启 iPad**，
重启后会要求再输一次密码确认。
（这个开关只有在你第一次侧载 App 之后才会出现在设置里。）

### 7.4 签名安装

1. Sideloadly 里 **IPA** 一栏拖入（或点选）`VideoScopePad-unsigned.ipa`
2. **Apple ID** 填你自己的 Apple ID 邮箱（免费账号就行，不用开发者账号）
3. 点 **Start**
4. 会要求输入 Apple ID 密码；开了双重认证的话还要输 6 位验证码
5. 出现 `Done.` 就装好了

### 7.5 首次运行

iPad：**设置 → 通用 → VPN 与设备管理 → 开发者 App → 你的 Apple ID → 信任**

然后回到桌面点开 VideoScopePad。第一次启动会弹相机权限，允许即可。

### 7.6 免费 Apple ID 的限制（很重要）

* 签名**7 天过期**，过期后 App 点开就闪退 → 重新用 Sideloadly 装一次即可（IPA 不用重新编译）
* 同时最多 **3 个**自签 App
* 每 7 天最多注册 10 个 App ID
* 想让它在后台自动续签：装 **AltStore**（<https://altstore.io/>）+ 在 Windows 上常驻
  **AltServer**，同一 Wi-Fi 下会自动帮你续；或者买 99 美元/年的开发者账号，签名有效期 1 年

---

## 8. 常见失败速查表

| 现象 | 原因 | 怎么办 |
| --- | --- | --- |
| **`An error occurred trying to start process '/bin/bash' with working directory '.../VideoScopePad'. No such file or directory`** | **仓库里没有源码**（通常只建了工作流文件），所以那个目录不存在 | 把源码推上来，见第 2 步；确认仓库首页能看到 `VideoScopePad/` 目录 |
| 报 `xcodebuild: error: The project ... does not contain a scheme named "VideoScopePad"` | 仓库里有工程但目录层级异常 / 工程文件不全 | 跑 `node VideoScopePad/tools/ci-locate-selftest.mjs` 自检；看日志「仓库内容自检」那步打印的文件树 |
| 日志里出现 `::error title=仓库里没有源码::` | 整个仓库搜不到 `VideoScopePad.xcodeproj` | 同第一条：源码没推全 |
| `Node.js 20 is deprecated ... actions/checkout@v4, actions/upload-artifact@v4` | 官方把内置 Node 升到 24 后给的弃用提醒 | **只是警告，不影响构建**，可以不管；想消掉就把这两个 action 的 `@v4` 换成 `@v5` |
| Actions 左侧看不到这个工作流 | 文件不在默认分支；或 YAML 语法错；或路径写错 | 确认 `main` 分支上有 `.github/workflows/build-ipa.yml`；文件里不能用 Tab 缩进 |
| 提示 "Workflows aren't being run on this repository" | 私有仓库需要手动启用 | 点那个绿色按钮启用 |
| 报 `No runner matching the specified labels` | runner 标签不可用 | 本仓库用的是 `macos-latest`，正常不会；若被组织策略限制需联系管理员 |
| 私有仓库提示额度用完 | macOS 计费 ×10 | 把仓库改成 Public，或等下个月 |
| **编译（未签名）** 步骤红叉 | Swift/Metal 编译错误 | 看页面顶部 Summary 里的「编译错误摘要」，把它发给我 |
| 打包步骤报"没有找到构建产物" | 编译实际没成功，或产物路径不符 | 下载 `build-log` 工件，看结尾的 `** BUILD FAILED **` 上下文 |
| 下载的 artifact 里没有 ipa | 上传步骤会 `if-no-files-found: error` 直接报错 | 属于异常，把运行记录链接发我 |
| Sideloadly 报 `Provisioning profile doesn't include the application-identifier` | Bundle ID 冲突 | 在 Sideloadly 的 "Bundle ID" 栏改一个唯一的，比如 `com.你的名字.videoscopepad` |
| Sideloadly 报 `Unable to install` | 设备未信任 / 开发者模式没开 / 线不行 | 按 7.2、7.3 重来一遍，换根线 |
| 装好了但点开闪退 | 没信任开发者描述文件；或 7 天签名过期 | 第 7.5 步信任；或重新侧载 |
| 打开后提示相机权限但没有权限开关 | 极少数情况下 `Info.plist` 没生成相机说明 | 本工作流已自动兜底补上；真遇到就把运行日志发我 |

---

## 9. 编译失败时，怎么把信息发给我

不用把几万行日志全贴过来，只要这三样：

1. 页面顶部 **Summary** 里那段 `编译错误摘要`（工作流自动生成的）
2. **显示编译工具链** 那一步的输出（macOS / Xcode / SDK 版本）
3. 失败步骤日志的**最后 30 行左右**（通常包含 `** BUILD FAILED **` 和真正的报错上下文）

有这三样，我基本能直接定位并改代码。

---

## 10. 进阶玩法

* **每次打 tag 自动发 Release**：在打包后加一步 `softprops/action-gh-release`，
  这样 IPA 就变成一个长期可下载的附件，不怕 artifact 30 天过期。
* **固定 runner / Xcode 版本**：把 `runs-on: macos-latest` 改成 `macos-15`，
  或在编译前加 `sudo xcode-select -s /Applications/Xcode_16.1.app`，构建结果更可复现。
* **用 Debug 配置排查**：手动触发时把「构建配置」选成 Debug，
  优化关掉后某些编译错误的定位会更直白。
* **只做"语法体检"不打包**：把编译命令的 `-sdk iphoneos` 换成 `-sdk iphonesimulator`，
  几十秒就能知道代码有没有编译错误，适合频繁改代码时先跑一轮。
* **出已签名的 IPA**（需要 99 美元/年的开发者账号）：把 `.p12` 证书和
  `.mobileprovision` 描述文件做成 Base64 存进仓库 Secrets，
  在 CI 里导入临时钥匙串、去掉 `CODE_SIGNING_ALLOWED=NO`、改用
  `xcodebuild -exportArchive` 导出 —— 这样产出的 IPA 装上就是 1 年有效期。
  需要的话我可以把这一段工作流写出来。

---

## 11. 安全与隐私

* **绝对不要**把 Apple ID 密码、`.p12` 证书、`.mobileprovision`、任何 token 提交进仓库
  （公开仓库等于全网可见；就算删掉，历史记录里还在）
* 你的 **Apple ID 只交给本机的 Sideloadly**，不会经过 GitHub，也不经过我
* Sideloadly 会拿你的 Apple ID 向 Apple 换取签名证书，属于正常流程；
  但**务必从官网下载**，不要用第三方渠道的"破解版"
* 公开仓库意味着代码公开可见。介意的话：
  建私有仓库（消耗免费额度），或者把 `PRODUCT_BUNDLE_IDENTIFIER`、应用名、作者信息改掉再公开
