# Clarry 的 DSH 按钮（可选的一层壳）

Clarry 自己是独立软件：`start.ps1` 开一个窗口，或者 `core/src/Clarry.Server` 起 HTTP 表面。
这里放的是**可选的一层** —— 在 DeepSeek Harness 的窗口右上角加一个按钮，点开就能看舞台。

## 它不做什么

- **不持有世界。** 人、幕、记忆、账本都在 C# 那边，这里只发 HTTP，读到什么画什么。
- **不是运行 Clarry 的必要条件。** 不开 DSH，`start.ps1` 一样跑；舞台页面也一样在
  `http://127.0.0.1:3090/stage`。

## 装上

DSH 的 profile 里两处：

```jsonc
// profiles/<name>/package.json
"dependencies": { "clarry": "link:D:/Clarry/integrations/dsh" },
"dsh": { "profile": { "bundles": [ "…", "clarry" ] } }
```

`node_modules\clarry` 指向 `D:\Clarry\integrations\dsh`。**DSH 重启之后**按钮才会出现 ——
插件是启动时装载的，改完不重启看不到。

## 两个文件

| 文件 | 是什么 |
| --- | --- |
| `host/index.js` | 宿主半边：注册插件名，不挂系统提示、不改全局策略 |
| `client/index.js` | 浏览器半边：右上角一个按钮，点开用 iframe 打开舞台页 |

按钮上的 `x/64` 是 `/api/health` 报的台上人数与容量，每十五秒问一次。

## 一处写死的地址

`client/index.js` 里的 `SERVER` 是 `http://127.0.0.1:3090/`。浏览器那段读不到宿主的环境变量，
所以换了端口（`start.ps1 -Port 3091`）之后按钮会指向一个没人应答的地址 —— 此时用窗口或浏览器
直接打开新端口即可，按钮那份要改这一行。

## 撤除这一层

决定不要这个按钮时，五处要一起动。少动一处，下一次启动或下一次 `plan-check` 就会说话。

1. **profile 的依赖** —— `profiles/<name>/package.json` 里删两项：`dependencies` 的
   `"clarry": "link:D:/Clarry/integrations/dsh"`，以及 `dsh.profile.bundles` 里的 `"clarry"`。
2. **node_modules 的链接** —— 删 `profiles/<name>/node_modules/clarry`。它是个 junction，删链接不动目标。
3. **这个目录** —— 删 `integrations/dsh`。
4. **工程地图** —— `design/PLAN.json` 里 `dsh-header-button` 与 `decouple-dsh` 两条目标是这一层的记录，
   一起删；`stage-view` 的 `after` 里引用了 `dsh-header-button`，也要去掉那一项。
   改完跑 `scripts/plan-check.ps1`，有悬空引用它会直接说。
5. **根 README** —— 目录表里 `integrations/dsh` 那一行，「现在到哪」里对 `decouple-dsh` 的提法。

DSH 重启之后按钮才真的消失。清完之后 Clarry 一个字节都不少：窗口、服务、舞台、Unity 客户端
本来就不经过这里。

