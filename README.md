# curl2url

把开发者工具里「Copy as cURL」或 HAR 导出内容，变成干净的 URL；顺带把请求参数、请求头、请求体、失败响应拆开看。

专治微信开发者工具 / Chrome 网络面板：右键只能复制 cURL，粘出来一堆 header、cookie、body；或者 HAR 文件太大没法看。两种都直接粘进来即可。

还带一个**截图转文字**：报错截图直接 `Ctrl+V`，默认用 Windows 自带 OCR 在本机识别，也可以配置豆包 / 通义千问 / OpenAI 等视觉模型识别，一键复制给 AI，不用再发图片。

以及一个**日志人话翻译助手**：把 Java / Spring / K8s / SQL 日志粘进来，自动去掉重复心跳和框架刷屏，翻译成「一句话 / 发生了什么 / 关键信息 / 有没有报错 / 建议下一步」的中文报告，不用先看懂英文堆栈。

## 下载安装

- **Windows（推荐）**：直接下载 [curl2url.exe](https://github.com/nihaocss/curl2url/releases/latest/download/curl2url.exe)，免安装，双击运行。需要 Windows 10 / 11（.NET Framework 4.8 自带）。本地截图识别用的是系统自带 OCR，中文 Windows 默认已装 `zh-Hans-CN` 语言包，无需联网；想更准可以再配 AI 识别（可选，联网）。
- **macOS / Linux**：没有 GUI 安装包，可安装 Node.js 后使用仓库里的 `curl2url.js` 命令行。
- **iOS / Android**：不支持。本项目是 Windows 桌面工具，没有移动端版本。

> 程序未做数字签名，Windows 首次运行时可能出现 SmartScreen 提示，选择「更多信息 → 仍要运行」即可。

## 平台支持

| 平台 | 支持情况 |
|---|---|
| Windows | ✅ 提供 exe，免安装 |
| macOS / Linux | ⚠️ 仅 Node 命令行 |
| iOS / Android | ❌ 无移动端版本 |

## 快速开始（桌面版）

**双击桌面的 `curl2url` 快捷方式**（或直接运行 `curl2url.exe`）。

- 打开时如果剪贴板里是 cURL 或 HAR，会自动载入
- **粘贴后自动解析**，不需要点按钮；`Ctrl+Enter` 可立即刷新
- 界面六个页签：
  - **URL**：提取出的地址（勾选「隐藏长参数 / Token」时，token 会打码）
  - **请求详情**：每个请求的 URL、方法、状态、查询参数、请求头、请求体（JSON 自动格式化）
  - **失败请求**：HAR 里状态码 ≥ 400、被拦截或无响应的请求，保留 URL、请求参数、响应头和响应体
  - **响应**：仍可手动粘贴单条返回值，一键格式化 JSON
  - **截图识别**：微信 / QQ 截图后直接 `Ctrl+V`（或点「粘贴图片」「打开图片」，也可以把图片文件拖进窗口），自动识别报错文字；识别结果可一键复制给 AI。左侧缩略图**双击可放大**，支持缩放、拖动和 `Esc` 关闭
  - **日志人话**：粘贴或拖入 `.log` / `.txt`，自动去噪并生成中文诊断报告；支持 `只看错误`、`按 traceId 分组`，也可选 `AI 解读（联网）`
- 右上角「**使用教程**」解释开发者工具里各种 Copy 格式（Copy link address / request headers / response / as cURL / all as HAR 等）分别会复制到什么，以及该选哪个
- 勾选「**保留响应**」后，「请求详情」会在每个请求下面显示响应头和响应体；不勾选则只看请求侧信息。cURL 本身不含响应，只有 HAR / 请求列表才有
- 「识别方式」可选**本地 OCR（离线，默认）**或**AI 识别（联网，更准）**；点「AI 设置」配置服务商和 Key。AI 请求失败时会自动回退到本地 OCR，并在状态栏标明
- 本地识别完全在本机完成，不上传图片；小字号截图会自动放大后再识别。中文 OCR 引擎本身也能识别英文报错，纯英文行会把误判的全角标点转回半角（`https：//` → `https://`）；个别字母可能混淆（`l` / `1`、`O` / `0`），贴给 AI 前扫一眼即可
- HAR 支持：点「打开 HAR 文件」、把 `.har` 文件拖进窗口，或直接粘贴 JSON
- 默认按 URL 去重；支持 `http://`、`https://`、`ws://`、`wss://`
- 默认隐藏超长参数值和 token / sign / session / cookie / authorization 一类字段
- 请求详情 / 失败请求里自动隐藏 `User-Agent`、`Accept-Language`、`Sec-Fetch-*`、`Vary`、`X-Powered-By`、缓存控制等浏览器 / 网关通用头，末尾提示隐藏了几个；只保留 `Origin`、`Referer`、`Content-Type`、`Cookie`、自定义头和 `X-Request-Id` / `X-Trace-Id` 这类追踪头
- 勾选「保留响应」时，响应头里的 CORS（`Access-Control-*`）、跨域策略、缓存 / 安全策略等通用头也会隐藏，完全重复的头会合并；保留 `Content-Type`、`Set-Cookie`、`Location`、`X-Request-Id` / `X-Trace-Id` 等有用字段
- `openid` / `openId` 不参与打码，始终完整显示
- 下方结果框（URL / 请求详情 / 失败请求 / 日志人话）可直接 `Ctrl+A` 全选、`Ctrl+C` 复制（没选区时复制整页）；也可以右键选「全选 / 复制 / 全选并复制」

> 注意：cURL 只包含**请求**，不包含响应，所以无法判断失败请求；要识别错误请求请用 HAR（Network 面板右键 → Save all as HAR），或带 `statusCode` / `responseBody` 的请求列表 JSON。

## AI 识别（可选）

系统 OCR 对字号很小的代码截图、深色背景、被弹窗遮挡的报错容易认错（`l` / `1`、`rn` / `m`），这时可以换成视觉大模型，整段还原度会高很多。

1. 在「截图识别」页把「识别方式」切到 **AI 识别（联网，更准）**；未配置时会自动弹出设置窗口
2. 选服务商，填入 API Key（模型 ID 以服务商控制台为准，也可以填火山方舟的「接入点 ID」）：

   | 服务商 | 接口地址 | 默认模型 |
   |---|---|---|
   | 豆包（火山方舟） | `https://ark.cn-beijing.volces.com/api/v3/chat/completions` | `doubao-seed-2-0-lite-260215` |
   | 通义千问（阿里云） | `https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions` | `qwen-vl-max` |
   | OpenAI | `https://api.openai.com/v1/chat/completions` | `gpt-4o-mini` |
   | 自定义 | 任意 OpenAI 兼容 `/chat/completions` 地址 | 自行填写 |

3. 点「测试连接」确认接口、模型和 Key 可用，再「保存」

说明：

- **隐私**：AI 识别会把截图发送到你配置的服务商，不便外发的截图请切回「本地 OCR（离线）」
- **Key 存储**：只保存在本机，用 Windows DPAPI（当前用户）加密，文件位于 `%APPDATA%\curl2url\ai-ocr.json`
- **自动回退**：AI 超时、Key 失效、余额不足等失败时会自动改用本地 OCR，状态栏会提示原因
- 请求参数固定 `temperature: 0`、`stream: false`；图片会先缩到最长边 2400 像素再上传，避免超出体积限制
- 高级用法：设置环境变量 `CURL2URL_AI_URL` / `CURL2URL_AI_MODEL` / `CURL2URL_AI_KEY` 可临时覆盖配置（不写入磁盘）；`CURL2URL_AI_CONFIG` 可指定配置文件路径

## 日志人话翻译助手（Log Explainer）

「日志人话」页签把一大段 Java / Spring / K8s / SQL 日志整理成固定结构的中文报告：

- **一句话**：这段日志到底发生了什么
- **发生了什么**：时间范围、traceId 数量、接口调用次数、关键动作和噪音折叠情况
- **关键信息**：服务 / 模块、Controller、接口、traceId、手机号（打码）、业务标识、SQL 及参数示例
- **有没有报错**：异常中文含义、`Caused by` 根因、业务堆栈、针对性建议
- **建议下一步**：按优先级给出排查动作

默认开启「过滤噪音」：重复心跳、健康检查、`size: 0` 定时任务、MyBatis SqlSession 等框架刷屏、连续重复 INFO 会折叠，并在末尾说明折叠了多少行。另外两个开关：

- **只看错误**：只保留 ERROR / WARN / Exception 及其前后 5 行上下文（包括相关的请求、HTTP 状态和 K8s 事件）
- **按 traceId 分组**：多个 traceId 时先列目录，再分别讲「这一次请求的故事」；单 traceId 直接给出完整经过

识别范围：

- `[UHLOG]开始请求URI[...]`、HTTP 方法 / 路径、Controller 日志
- `Exception` / `Caused by`、Spring 单行 `nested exception is ...` 异常链（逐层拆开并标出根因）、业务堆栈（自动过滤 Spring / JDK 框架帧）；同一异常链重复出现会合并并计数
- JDK 原生反序列化失败（`SerializationFailedException` / `DefaultDeserializer` / `NestedIOException` / `ClassNotFoundException`）：会点明“不是 JSON 字段不匹配”，并按「Redis 旧缓存 / 旧消息 / DTO 被删除改名 / jar 漏打包」给出排查顺序
- AppKey / 签名校验失败
- MyBatis `Preparing:` + `Parameters:`（SQL 与参数示例都会打码）
- K8s `Ready 1/1`、`Restarts`、`CrashLoopBackOff`、`OOMKilled`、探针失败
- 手机号、业务单号、HTTP 4xx / 5xx

大日志：支持一次粘贴 1 万字以上；超长时优先保留错误段和关键行，并在报告里提示截断。

隐私：**本地分析完全离线**，不会上传日志。点「AI 解读（联网）」时，才会把本机报告和过滤后的日志摘录（手机号、Authorization、Cookie、token / password / secret 等已打码）发送到「截图识别 → AI 设置」里配置的服务商；AI 失败不会清空本地报告。

AI 解读的提示词按资深 Java 后端 / 中间件 / SRE 的口径约束：先列事实，再写推断，推断必须带「很可能」和依据；区分 JDK 序列化与 JSON 解析问题；对 `ClassNotFound` 优先考虑 Redis 旧缓存 / 旧消息 / DTO 删除改名或 jar 缺类，而不是笼统归因于“字段不匹配”；最后给出可直接执行的排查命令。

## 支持的格式

- 一次粘贴**多条** curl（批量复制所有请求）
- 自动识别 **HAR**（Chrome / 微信开发者工具 / Charles 导出的 JSON）
- HAR 轻微损坏也能尽量打开：在线 HAR 查看器有时会把嵌套 JSON 直接塞进 `postData.text` / `content.text`，不转义换行和引号，甚至把文件尾部截断。遇到这种情况会保留损坏点之前的完整请求继续解析，并在状态栏和「请求详情」顶部提示「已忽略损坏位置之后的内容」，不再整份文件报解析失败
- 自动识别第三方抓包插件导出的**请求列表 JSON**（顶层数组，元素含 `url`、`requestHeaders`、`requestBody`、`statusCode`、`responseBody`、`curlRequest` 等字段），同样支持提取 URL 和识别失败请求
- bash `\`、cmd `^`、PowerShell 反引号续行
- 单引号 / 双引号 / 无引号 URL
- `--url` 参数形式
- JSON 转义：`\u0026` → `&`、`\/` → `/`
- `-X/--request`、`-H/--header`、`-d/--data*` 参数解析
- `Referer`、`Origin` 等 header 里的 URL 不会被误提取（只取 curl 的目标地址）
- 直接粘贴纯 URL 也能识别

## 命令行

```powershell
# 从标准输入提取 URL（只打印，不碰剪贴板；加 --mask 隐藏长参数 / Token）
Get-Content curl.txt | .\curl2url.exe --extract --mask

# 输出请求详情（--mask 隐藏长 Token / 敏感参数；--responses 同时输出响应头和响应体）
Get-Content curl.txt | .\curl2url.exe --details --mask
Get-Content network.har | .\curl2url.exe --details --mask --responses

# 只看失败请求（需要 HAR 或带 statusCode 的请求列表 JSON，cURL 不含响应状态）
Get-Content network.har | .\curl2url.exe --failures --mask

# 截图转文字（Windows 自带 OCR，识别结果打印到标准输出）
.\curl2url.exe --ocr error.png

# 用已配置的 AI 识别（需先在图形界面「AI 设置」里配置，或设置 CURL2URL_AI_* 环境变量）
.\curl2url.exe --ocr-ai error.png

# 自检（不会弹窗口）
.\curl2url.exe --selftest
```

旧版 Node 命令行仍然保留：

```powershell
# 剪贴板 -> 剪贴板
.\curl2url.cmd

# 从文件读，结果写回剪贴板
.\curl2url.cmd -f curl.txt

# 只打印，不碰剪贴板
Get-Clipboard | node .\curl2url.js -s -n
```

| 选项 | 说明 |
|---|---|
| `-f, --file <路径>` | 从文件读取（Node 版） |
| `-s, --stdin` | 从标准输入读取（Node 版） |
| `-k, --keep-duplicates` | 保留重复 URL（默认去重，Node 版） |
| `-1, --first` | 只输出第一个 URL（Node 版） |
| `-n, --no-clipboard` | 不写回剪贴板（Node 版） |
| `-h, --help` | 显示帮助（Node 版） |

## 重新编译（可选）

不需要安装任何东西，用的是 Windows 自带的 .NET Framework 编译器：

```bat
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
%CSC% -target:exe -out:MakeIcon.exe -reference:System.Drawing.dll MakeIcon.cs
MakeIcon.exe curl2url.ico
%CSC% -target:winexe -codepage:65001 -optimize+ -out:curl2url.exe ^
  -reference:System.Windows.Forms.dll -reference:System.Drawing.dll ^
  -reference:System.Security.dll ^
  -reference:"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll" ^
  -win32icon:curl2url.ico -win32manifest:curl2url.manifest Curl2UrlApp.cs
```

## 注意

- 输出顺序与 curl 出现顺序一致，默认对完全相同的 URL 去重
- 查询参数按原始编码展示，不做 URL 解码
- `curl2url.exe` 是本地编译的，不依赖 Node；Node 版命令行仍然需要 Node.js
- 仓库里的 `curl2url.exe` 已编译好，可直接运行；修改源码后按上面的命令重新编译

## 开源协议

MIT License，见 [LICENSE](LICENSE)。
