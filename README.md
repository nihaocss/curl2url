# curl2url

把开发者工具里「Copy as cURL」或 HAR 导出内容，变成干净的 URL；顺带把请求参数、请求头、请求体、失败响应拆开看。

专治微信开发者工具 / Chrome 网络面板：右键只能复制 cURL，粘出来一堆 header、cookie、body；或者 HAR 文件太大没法看。两种都直接粘进来即可。

还带一个**截图转文字**：报错截图直接 `Ctrl+V`，用 Windows 自带 OCR 在本机识别出文字，一键复制给 AI，不用再发图片。

## 下载安装

- **Windows（推荐）**：直接下载 [curl2url.exe](https://github.com/nihaocss/curl2url/releases/latest/download/curl2url.exe)，免安装，双击运行。需要 Windows 10 / 11（.NET Framework 4.8 自带）。截图识别用的是系统自带 OCR，中文 Windows 默认已装 `zh-Hans-CN` 语言包，无需联网。
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
- 界面五个页签：
  - **URL**：提取出的地址（勾选「隐藏长参数 / Token」时，token 会打码）
  - **请求详情**：每个请求的 URL、方法、状态、查询参数、请求头、请求体（JSON 自动格式化）
  - **失败请求**：HAR 里状态码 ≥ 400、被拦截或无响应的请求，保留 URL、请求参数、响应头和响应体
  - **响应**：仍可手动粘贴单条返回值，一键格式化 JSON
  - **截图识别**：微信 / QQ 截图后直接 `Ctrl+V`（或点「粘贴图片」「打开图片」，也可以把图片文件拖进窗口），自动识别报错文字；识别结果可一键复制给 AI
- 截图识别完全在本机完成，不上传图片；小字号截图会自动放大后再识别。中文 OCR 引擎本身也能识别英文报错，纯英文行会把误判的全角标点转回半角（`https：//` → `https://`）；个别字母可能混淆（`l` / `1`、`O` / `0`），贴给 AI 前扫一眼即可
- HAR 支持：点「打开 HAR 文件」、把 `.har` 文件拖进窗口，或直接粘贴 JSON
- 默认按 URL 去重；支持 `http://`、`https://`、`ws://`、`wss://`
- 默认隐藏超长参数值和 token / sign / session / cookie / authorization 一类字段
- 请求详情 / 失败请求里自动隐藏 `User-Agent`、`Accept-Language`、`Sec-Fetch-*`、`Vary`、`X-Powered-By`、缓存控制等浏览器 / 网关通用头，末尾提示隐藏了几个；只保留 `Origin`、`Referer`、`Content-Type`、`Cookie`、自定义头和 `X-Request-Id` / `X-Trace-Id` 这类追踪头
- `openid` / `openId` 不参与打码，始终完整显示
- 下方结果框（URL / 请求详情 / 失败请求）可直接 `Ctrl+A` 全选、`Ctrl+C` 复制（没选区时复制整页）；也可以右键选「全选 / 复制 / 全选并复制」

> 注意：cURL 只包含**请求**，不包含响应，所以无法判断失败请求；要识别错误请求请用 HAR（Network 面板右键 → Save all as HAR），或带 `statusCode` / `responseBody` 的请求列表 JSON。

## 支持的格式

- 一次粘贴**多条** curl（批量复制所有请求）
- 自动识别 **HAR**（Chrome / 微信开发者工具 / Charles 导出的 JSON）
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

# 输出请求详情（--mask 隐藏长 Token / 敏感参数）
Get-Content curl.txt | .\curl2url.exe --details --mask

# 只看失败请求（需要 HAR 或带 statusCode 的请求列表 JSON，cURL 不含响应状态）
Get-Content network.har | .\curl2url.exe --failures --mask

# 截图转文字（Windows 自带 OCR，识别结果打印到标准输出）
.\curl2url.exe --ocr error.png

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
