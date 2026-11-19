# Clarry

一座 agent 自己演戏的房子。没有人指挥，也不需要观众在场。

角色在里面说话、开店、打牌、占卜、闹别扭。它们各自有记忆、对彼此有称呼、性格会随着发生的事情慢慢挪动。你打开页面能看见它们正在做什么，也能用许愿把镜头挪一下——但你没有台词要念。

## 三条不变量

这三条决定了整个设计，改起来比改代码难。

**六十四是同时在场的主角人数上限**，不是登记人数。店员、路人、临时角色不进这个数，因为它们不占舞台座位，也不参与幕的配对。

**一个座位由 agent 操盘，不是一个模型实例。** 六十四席共用一套模型服务是正常部署，不是降级。所以真正的负载杠杆是「这一轮有几个人需要开口」，而不是并发数——每轮先问所有人，之后只问被点到名的、在等回应的、能应请求的、以及持有发言权的。实测下来第二轮通常只剩一两个人有理由说话。

**第四面墙是结构，不是一个彩蛋。** 请求写在一块具体的地方（祈愿点），应它的是某一位有名有姓的神。角色知道神庙，不知道观众。任何一条代码路径都写不出「观众做的这件事」。

## 从零到看见舞台

需要 .NET 10 SDK 和 PowerShell。

```powershell
git clone git@github.com:Nesarf/Clarry.git D:\Clarry
cd D:\Clarry

# 1. 编译。会先停掉正在跑的服务——它锁着 Clarry.Core.dll。
& .\rebuild.ps1 -Start

# 2. 打开舞台
start http://127.0.0.1:3090
```

服务端默认把 `/` 重定向到 `/stage`，舞台页面就是 `stage/index.html`。它通过公开的 HTTP 接口读世界，和任何其他客户端（Unity、curl）走同一条路。

**克隆到别的位置时要设 `CLARRY_ROOT`。** 服务端默认把这个工程的根钉在 `D:\Clarry`，挪到别处就找不到 `stage/` 和 `assets/`。世界存档与记忆库落在 `CLARRY_ROOT\data`，可以用 `CLARRY_DATA` 另指。

```powershell
$env:CLARRY_ROOT = 'C:\code\Clarry'
& .\rebuild.ps1 -Start
```

世界刚起来是空的。要有人，先建几个人：

```powershell
curl http://127.0.0.1:3090/api/world
```

七十六条路由，除了 `/`、`/stage` 和 `/api/save`，其余都在 `core/src/Clarry.Server/Endpoints/` 的十一个组里，一个组一个文件。想让它自己动起来，得接一个能应答 HTTP 的模型端点：`/api/driver/attach` 挂上驱动器，`/api/loop/start` 之后它自己一拍一拍往下走。

## Unity 舞台

`unity/ClarryStage` 是第二个客户端，只画不判。它读 `/api/cast` 与 `/api/history`，把台上每个人的立绘与最后十句台词摆出来，每 1.5 秒问一次。

**哪一张画用哪一张由服务端算。** 服务端给的是候选链（特写、线稿、像素表情、常态），客户端只把 `face` 字段那一条下载下来。分镜逻辑各写一份之后，每改一次镜头就要改两处。

打开方式：Unity Hub 里添加 `unity/ClarryStage`，打开，按 Play。工程里**没有场景文件**——整个舞台由 `StageBootstrap` 在运行时搭出来，所以第一次打开不需要有人先摆 Canvas 或 Prefab。这也是这个目录能完整留在版本控制里的原因：没有二进制场景资产。

它和浏览器舞台可以同时开着，互不打扰。想要真跑起来的人，先 `& .\scripts\scene-kivotos.ps1 -NoRun` 把四个人放上台。

两件事值得先知道：

- 它用 `Font.CreateDynamicFontFromOSFont` 取系统字体。Unity 内置字体没有汉字，不这么做的话满屏方块。
- 它不共享 `Clarry.Core` 的模型类。那边是 C# 13（`required`、`file` 命名空间），Unity 2022.3 停在 C# 9，所以 `Assets/Scripts/ClarryApi.cs` 里那些 DTO 是抄的，字段名就是 JSON 的字段名。

## 改完怎么确认没坏

```powershell
& .\scripts\test-all.ps1
```

22 套验收，二十秒出头跑完，最后只说一句结论。它按依赖顺序排——先属性与记忆这类不依赖别的东西的，再回合、再幕、再多幕，最后才是建在多幕之上的场所、班次与视线。所以某套失败时，前面那些已经证明是好的，不用从头猜。

只跑一套：

```powershell
& .\scripts\test-all.ps1 -Only test-wish
```

服务没起时它们会直接说不通，而不是给出一个看着像通过的结论。

## 目录

| 路径 | 是什么 |
| --- | --- |
| `core/src/Clarry.Core` | 领域模型：角色、属性、幕、回合、请求、许愿、占卜、小游戏 |
| `core/src/Clarry.Server` | HTTP 表面。`Program.cs` 只有启动与十一个端点组的挂载，业务在各组自己的文件里 |
| `stage/index.html` | 舞台页面 |
| `assets/portraits` | 像素立绘与表情差分 |
| `scripts` | 22 套验收，加上出图、音频、体积测量几个工具 |
| `design/PLAN.json` | 工程地图。45 个目标，每个都记着碰哪些文件、由哪套验收判 |
| `docs` | 机制、风格、技术栈、记忆、美术、音频、仓库 |

## 现在到哪

45 个目标，43 个完成，2 个待办：`fourth-wall`（极低概率的察觉，刻意等语气稳住）与 `unity-stage`（Unity 客户端，等客户端与服务端的分工定下来）。

```powershell
# 更新工程地图之后核对它读得动
& .\scripts\plan-check.ps1
```

已知的债记在 `design/PLAN.json` 的 `notes` 里。

## 文档

- `docs/MECHANICS.md` —— 怎么运转的，677 行，先读这篇
- `docs/STYLE.md` —— 观众看到的东西长什么样
- `docs/MEMORY.md` —— 记忆怎么存、怎么衰减
- `docs/ART.md` —— 立绘的规格与降级链
- `docs/AUDIO.md` —— 从创作到打包的音频管线
- `docs/STACK.md` —— 哪种语言放在哪一层
- `docs/REPO.md` —— 三个体积数字，以及什么进版本控制
