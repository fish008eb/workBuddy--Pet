# WorkBuddy 用量宠物 · wb-usage-pet

给 WorkBuddy 桌面版加一个**会说人话的用量看板**：屏幕右下角蹲着一只小角色，每轮对话结束它跳一下、弹个气泡，告诉你这一轮烧了多少 token、缓存命中了多少、花了多少积分。

> WorkBuddy 桌面端底部只显示「共消耗 ◇x.xx 积分」，看不到 token 用量和缓存命中。本项目就是为了把这两个数字搬到眼前。

*English: a desktop pet for WorkBuddy that shows per-turn tokens, cache-hit rate and credits in a floating speech bubble. Hooks-based, Windows only, zero runtime dependencies for the data pipeline.*

```
        ┌─────────────────────────────┐
        │ WorkBuddy              13:32│
        │                             │
        │ ↑ 146.4万        ↓ 1.2万    │
        │   输入             输出     │
        │                             │
        │ ⚡ 99.5%          ◈ 1.21    │
        │   缓存命中         积分     │
        │ ─────────────────────────── │
        │     今日 80.6 积分 · 4 会话  │
        └──────────────┬──────────────┘
                       ▼
                    ( 角色 )
```

---

## 一、为什么要用悬浮窗

桌面版把常用的几条「显示用量」的路都堵死了，本项目是把四条路全试过之后的结论：

| 方案 | 结果 | 原因 |
|---|---|---|
| `settings.json` 里开 `showTokensCounter` | ❌ 无效 | 只作用于 CLI 的 TUI，桌面 GUI 不读 |
| Stop hook 输出 `systemMessage` | ❌ 不渲染 | 桌面端收到但界面不显示 |
| Windows toast 通知 | ⚠️ 不可靠 | 系统通知总开关关着（`ToastEnabled=0`）就完全静音 |
| 终端 `statusLine` | ❌ 无效 | statusLine 是 CLI 功能，GUI 不渲染 |
| **独立进程 + 读本地会话日志** | ✅ **可行** | 绕开所有 UI 渲染限制 |

所以：**用一个独立的小窗口自己画**。

## 二、功能

- **每轮结束自动播报**：Stop hook 精确卡在「回答刚结束」，气泡弹出 6 秒，角色跳一下
- **悬停查看**：平时只蹲着一个角色，鼠标移上去才显示气泡，不挡屏幕
- **四个核心指标**：输入 token、输出 token、缓存命中率、本轮积分
- **今日汇总**：跨会话、跨工作空间，0 点自动归零
- **动图形象**：支持 APNG 逐帧播放（默认 36 帧循环）
- **情绪表达**：7 种情绪、3 套人格、3 档唠叨，按用量与时间自动开口
- **完成提示音**：每轮回答结束响一声（两音合成，可关，告警情绪换低沉音）
- **12 套形象可换**：右键菜单即点即换，本地缓存秒切；也能换成自己的图片
- **生命周期跟随**：打开 WorkBuddy 自动出现，完全退出后自动消失
- **原生宿主可选**：编译成 `WbPet.exe` 后启动更快、无 PowerShell 进程（情绪功能也依赖它）

## 三、环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 / 11 |
| WorkBuddy | 桌面版（已在 5.9.x 验证） |
| Node.js | 18 或更高（hook 脚本与拆帧工具都用它） |
| PowerShell | 5.1（系统自带即可） |

## 四、安装

```powershell
git clone https://github.com/<your-name>/wb-usage-pet.git
cd wb-usage-pet
powershell -ExecutionPolicy Bypass -File install.ps1
```

安装脚本会：

1. 把 `src\` 的脚本和 `tools\` 的工具复制到 `%USERPROFILE%\.workbuddy\hooks`
2. 下载并拆分默认形象（可用 `-Skin` 指定，`-SkipSprite` 跳过）
3. 备份 `settings.json` 后注册两个 hook（`SessionStart` / `Stop`）
4. 立即拉起面板

可选参数：

```powershell
# 指定初始形象（id 见下方「换形象」表）
powershell -ExecutionPolicy Bypass -File install.ps1 -Skin quantum

# 不下载形象（自己准备素材时用）
powershell -ExecutionPolicy Bypass -File install.ps1 -SkipSprite

# 顺手编译原生宿主（需要 .NET Framework 4.x，编译后可删掉 PowerShell 依赖）
powershell -ExecutionPolicy Bypass -File install.ps1 -BuildNative
```

安装完成后，屏幕右下角应该出现角色。**如果没出现，重启一次 WorkBuddy 再发一条消息**（桌面端的 `SessionStart` 是在会话真正跑起来时才触发的，不是窗口一打开就触发）。

## 五、使用

| 操作 | 效果 |
|---|---|
| 鼠标移到角色上 | 显示用量气泡 |
| 移开鼠标 | 0.45 秒后自动收起 |
| 左键拖动 | 移动到任意位置（会记住） |
| 右键 → `换形象` | 12 个预设形象任选，切换即时生效 |
| 右键 → `人格` | 可爱治愈 / 毒舌吐槽 / 克制工程师 |
| 右键 → `唠叨程度` | 克制 / 话痨 / 极简 |
| 右键 → `提示音` | 开 / 关 / 试听 |
| 右键 → `静音 1 小时` | 期间不弹任何情绪气泡 |
| 右键 → `Refresh` | 立即刷新数据 |
| 右键 → `Hide for 10 min` | 藏起来 10 分钟 |
| 右键 → `Exit` | 彻底退出 |
| 每轮对话结束 | 气泡自动弹出 6 秒 + 角色动一下 + 一句情绪反馈（视触发规则） |
| 深夜 / 久坐 / 长时间无操作 | 自己开口提醒，或进入打盹状态 |

## 六、数据是怎么算的

数据来自本机会话日志 `%USERPROFILE%\.workbuddy\projects\<工作区>\<会话>.jsonl`，每条模型调用都带 `providerData.rawUsage`。

| 指标 | 口径 |
|---|---|
| **本轮输入 / 输出** | 最后一条用户消息之后，**所有** API 请求的 `prompt_tokens` / `completion_tokens` 累加（含每次工具调用，反映真实的 token 雪球） |
| **缓存命中率** | `缓存命中 / (命中 + 未命中)`，按 token 加权，不是按次数平均 |
| **积分** | 累加 `rawUsage.credit`，与界面「共消耗」同口径 |
| **会话** | 当前会话文件的全量累加 |
| **今日** | 所有工作空间、当天 0 点之后的所有请求 |

### 三个必须知道的口径细节

1. **「本轮输入 269 万」不等于你打了 269 万字**。一轮里每次工具调用都是一次独立请求，每次都把完整历史重发一遍，所以数字会滚雪球。
2. **缓存命中 99% 是好消息**。说明只有约 1% 的输入是全价，其余都是便宜的重发。
3. **这些数字不是官方账单**。读的是本地落盘日志，对账请以积分记录为准；中途手动打断的长思考可能不落盘。

## 七、换形象（换肤）

右键角色 → `换形象`，12 个预设形象任选，点完 1 秒内切换，不用重启。

| id | 名称 | id | 名称 |
|---|---|---|---|
| `quantum` | 量子喵 | `academy` | 学院喵 |
| `sweet` | 甜心喵 | `archive` | 档案喵 |
| `pixel` | 像素喵 | `design` | 设计喵 |
| `latte` | 拿铁喵 | `elite` | 和平精英喵 |
| `floral` | 花艺喵 | `rich` | 暴富喵 |
| `astro` | 星际喵 | `dragon` | 龙焰喵（默认） |

命令行同样可以切（调用的是同一套逻辑）：

```bash
node tools/switch-skin.js list          # 列出可用形象
node tools/switch-skin.js quantum       # 按 id 切换
node tools/switch-skin.js "D:\pic\a.png" --force   # 换自定义图片
```

**缓存机制**：每个形象第一次切换时下载并拆帧，存到 `assets/skins/<id>/`，之后切换是本地读取，秒切。想强制重新下载加 `--force`。

### 换成自己的形象

```bash
# 任意 APNG 动图或静态 PNG（URL 或本地路径都行）
node tools/switch-skin.js "https://example.com/my-pet.png"
node tools/switch-skin.js "D:\pics\my-pet.png" --height 240

# 只想拆帧、不切换
node tools/fetch-sprite.js "D:\pics\my-pet.png" --out .\assets\frames
```

**支持的输入**：

- APNG 动图（自动按 `fcTL`/`fdAT` 拆帧、按 `blend`/`dispose` 规则合成、统一裁剪对齐）
- 普通静态 PNG（按单帧处理）

**小提示**：素材最好是**透明背景**。白底图请先抠图，否则方块底会留在桌面上；尺寸不必纠结，脚本会统一缩放到 220px 高。

## 八、情绪系统（仅 WbPet.exe 生效）

宠物不只是报数字，它会根据用量、时间和节奏自己表达情绪。详细设计见 [docs/emotion-design.md](docs/emotion-design.md)。

### 7 种情绪

| 情绪 | 什么时候 | 动作 |
|---|---|---|
| 开心 | 缓存好、打盹醒来、跨天 | 弹跳 |
| 得意 | 缓存破纪录、积分/轮次里程碑 | 三连跳 |
| 担忧 | 烧太多、缓存崩、数据算不出来 | 横向抖动 |
| 心疼 | 连续工作 2h/4h、久坐 90 分钟 | 缓慢呼吸 |
| 困倦 | 深夜、15 分钟无活动 | 降帧 + 半透明 |
| 惊讶 | 单轮 token 或调用数异常 | 挤压回弹 |
| 平静 | 默认，不说话 | 无 |

### 3 套人格 × 3 档唠叨

右键角色即可切换，写入 `pet-state.txt`，重启后保留：

| 人格 | 风格 |
|---|---|
| 可爱治愈 | `缓存 99.8%，新纪录！我记小本本上了 😻` |
| 毒舌吐槽 | `269万 token，你是把整个仓库喂进去了？🙄` |
| 克制工程师 | `缓存 99.8%，刷新历史最好。🏆` |

| 档位 | 说什么 |
|---|---|
| **克制（默认）** | 只在异常、里程碑、深夜、久坐时开口 |
| 话痨 | 每轮都说一句，带本轮数据 |
| 极简 | 只在数据降级和里程碑开口 |

### 自己改词

所有文案在 `assets/personas.txt`，格式 `line|人格|触发key|变体|文案|emoji`，改完重启面板即可。占位符支持 `{in} {out} {cache} {credit} {calls} {today} {n} {h} {name}`。

### 防刷屏

每轮最多 1 条；非紧急事件间隔 ≥45 秒；10 分钟内 ≤3 条；同一情绪 3 分钟内不重复；鼠标悬停时挂起、移开补播。

> **注意**：情绪功能只在编译后的 `WbPet.exe` 里生效（脚本宿主版保留用量显示与换肤，不读情绪文件）。

## 九、编译原生宿主（可选，但情绪功能必需）

两套宿主共用同一份数据和皮肤，区别在功能完整度与依赖：

| 项 | 脚本宿主（`panel-wpf.ps1`） | 原生宿主（`WbPet.exe`） |
|---|---|---|
| 用量气泡 + 换肤 | ✅ | ✅ |
| 情绪系统（人格/唠叨/动作/打盹） | ❌ | ✅ |
| 内存（实测） | ~200MB | ~195MB |
| 启动 | 慢（要先起 PowerShell 运行时） | 快 |
| 后台进程 | powershell.exe | 只有一个 exe |

> 内存两版接近——大头是 WPF 渲染层和 36 帧位图，不是脚本运行时。真正换来的是**启动速度**和**没有 PowerShell 弹窗**。

```bat
:: 双击或在 cmd 里执行（需要 .NET Framework 4.x，Win10/11 自带）
build-pet.cmd
```

脚本会依次：定位编译器 → 校验 7 个程序集 → 校验源码是 UTF-8 with BOM → 用响应文件编译 → 编译成功才重启面板并确认进程存活。任一步失败都会打印具体是哪个文件/哪一步，失败时保留 `build.rsp` 便于排查。

编译完成后 `start-panel.js` 会自动优先使用 `WbPet.exe`，不需要改配置。

脚本用系统自带的 `csc.exe`（.NET Framework 4.x）编译 `src\WbPet.cs`，产出 `WbPet.exe` 并重启面板。

`start-panel.js` 的启动优先级是 **WbPet.exe → run-panel.vbs（PowerShell 兜底）**，编译完不用改任何配置。

## 十、工作原理

```
┌─────────────────┐   SessionStart   ┌──────────────────┐  WMI 创建  ┌─────────────┐
│  WorkBuddy 桌面  │ ───────────────► │ start-panel.js   │ ─────────► │  面板窗口    │
└─────────────────┘                  └──────────────────┘            │ (WPF/exe)   │
        │                                                           └──────┬──────┘
        │ Stop（每轮回答结束）                                             │ 轮询 2s
        ▼                                                                 │
┌─────────────────┐   读 transcript    ┌──────────────────┐               │
│ turn-usage.js   │ ─────────────────► │ panel-data.txt   │ ──────────────┘
└─────────────────┘                    └──────────────────┘
```

开发过程中踩到的三个坑，都写进代码里了：

1. **hook 进程会被 Job Object 连带清理**
   WorkBuddy 把 hook 放进 Windows Job Object，hook 结束后整棵进程树（含 `detached` 子进程）都会被干掉。所以启动器不能用普通 `spawn`，必须通过 **WMI `Win32_Process.Create`** 间接创建，让面板的父进程变成 `WmiPrvSE.exe`，彻底脱离 Job。

2. **WinForms 做不出干净的透明窗**
   `TransparencyKey` 是色键，半透明边缘会和键色混出一圈彩色描边；而且没声明 DPI 感知时整窗会被位图拉伸变糊。改用 **WPF `AllowsTransparency`** 的逐像素 alpha 才干净。

3. **PowerShell 5.1 按 ANSI 读取无 BOM 的 .ps1**
   脚本里写中文会直接解析失败。所以 `panel-wpf.ps1`、`install.ps1`、`uninstall.ps1` 都**必须存成 UTF-8 with BOM**，改文件时别把 BOM 弄丢。

## 十一、目录结构

```
wb-usage-pet/
├── package.json            项目元信息与 npm 快捷脚本
├── install.ps1              安装：复制脚本 + 注册 hooks + 下载形象
├── uninstall.ps1            卸载：关进程 + 撤 hooks + 删文件
├── src/
│   ├── turn-usage.js        Stop hook：解析会话日志，算用量，写面板数据与情绪事件
│   ├── pet-emotion.js       情绪判定与文案渲染
│   ├── pet-ledger.js        跨轮统计记忆（EMA / 里程碑 / 阈值）
│   ├── pet-event-writer.js  情绪事件追加日志（seq + 轮转）
│   ├── start-panel.js       SessionStart hook：WMI 拉起面板（单实例）
│   ├── panel-wpf.ps1        WPF 面板（PowerShell 宿主，兜底方案）
│   ├── WbPet.cs             WPF 面板（C# 原生宿主，可选）
│   ├── build-pet.cmd        编译 WbPet.exe
│   ├── run-panel.vbs        无控制台窗口的启动器
│   └── toast.ps1            Windows 通知通道（默认关闭时静默失败）
├── tools/
│   ├── fetch-sprite.js      APNG 拆帧工具（零依赖，可当模块用）
│   └── switch-skin.js       形象切换：下载 / 缓存 / 改 current.txt
├── assets/
│   ├── skins.json           形象清单（只存 id、名称、下载地址）
│   ├── personas.txt         3 套人格文案（可直接改词）
│   ├── sounds/              提示音 turn-done.wav / turn-alert.wav
│   └── skins/<id>/          运行时生成：该形象的序列帧与 current.txt（不入库）
├── docs/
│   └── emotion-design.md    情绪系统设计文档
└── README.md
```

npm 快捷脚本（可选，装了 Node 就能用）：

```bash
npm run skin:list        # 列出形象
npm run skin -- quantum  # 切换形象
npm run sprite -- "D:\pic\a.png"   # 拆帧
```

## 十二、常见问题

**面板没出现**
先看 `%USERPROFILE%\.workbuddy\hooks\panel.log` 与 `panel-err.log`。最常见是形象没下载好，执行 `node tools/switch-skin.js dragon` 重试。

**换形象后一直是旧形象**
面板靠轮询 `assets\skins\current.txt` 感知变化。如果手改过文件，右键 `Refresh`；仍无效则检查 `assets\skins\<id>\manifest.json` 是否存在。

**换形象很慢 / 失败**
首次切换要下载 3–9MB 并拆帧，取决于网络。失败会写进 `assets\skins\status.txt`，可加 `--force` 重试；公司网络限制 `download.codebuddy.cn` 时，用 `node tools/switch-skin.js "本地图片.png"` 换自定义素材。

**数字一直不动**
说明 Stop hook 没触发。检查 `settings.json` 里 `hooks.Stop` 是否指向 `turn-usage.js`；再看 `turn-usage.log` 有没有新记录。注意 CLI 侧手写的 hook 需要在 `/hooks` 面板审核，桌面版不需要。

**面板出现后又消失**
面板每 3 秒检查 WorkBuddy **主窗口**是否存在。Electron 关窗口后进程还留在托盘，所以判断依据是 `MainWindowHandle` 而不是进程数。如果你用多显示器把窗口移到了副屏，属于正常行为。

**每次启动闪一下黑框**
说明在用兜底路径。编译 `WbPet.exe` 后彻底消失（`winexe` 子系统本身没有控制台）。

**中文变成乱码**
`.ps1` 丢了 BOM。用编辑器另存为「UTF-8 with BOM」。

**杀毒软件报警**
面板要写 `settings.json`、启动后台进程，行为特征和脚本病毒相似。源码全在 `src/`，可自行审计或改成本地签名。

**没有提示音**
三个可能：右键 `提示音` 里被关掉了；处于「静音 1 小时」期间（静音同时也静音效）；`assets\sounds\turn-done.wav` 缺失（重新运行安装脚本或 `node tools/make-sound.js`）。想换音色就改这个 wav 文件，任意 16bit PCM WAV 都可以。

## 十三、卸载

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

会关闭面板、移除 hooks、删除已安装文件，并把配置备份成 `settings.json.bak-<时间戳>`。想回滚就把备份改名成 `settings.json`。

## 十四、已知限制

- 仅支持 **Windows**（依赖 WPF、WMI、Job Object 行为）
- 读的是本地日志，**不是官方账单**；换机器不同步
- 长上下文压缩（compact）后，历史轮次的 token 记录仍留在 jsonl 里，所以「会话」数字只会增不会减
- 形象素材版权不属于本项目，仓库不附带
- 情绪系统只在 C# 版（`WbPet.exe`）生效，脚本宿主版仅显示用量

## 十五、许可

代码：MIT，见 [LICENSE](LICENSE)。

形象素材：版权归 WorkBuddy / 腾讯所有，本仓库**不包含**任何官方美术资源，仅在安装时由使用者本地抓取，仅供个人本地展示使用。商用或再分发请自行取得授权。
