# 架构说明

目标：保持「一个 exe、双击即用、.NET Framework 4.8、WinForms」的交付方式，同时把功能拆成可注册的模块，后续加页签或数据库类型时尽量不改主窗口。

## 目录结构

```text
Curl2UrlApp.cs                  主程序：命令行入口、cURL/HAR/日志/OCR 页面、主窗口
src/Core/
  AppFeature.cs                 功能页接口、AppFeatureContext、AppFeatures 注册表
  AppTheme.cs                   配色、字体、通用控件工厂
  AsyncUi.cs                    后台线程 + UI 线程回调
  Dpapi.cs                      当前用户 DPAPI 加解密
  EmbeddedAssemblies.cs         从 exe 资源加载内嵌程序集
src/Features/Database/
  MainForm.Database.cs          主窗口的功能页挂载（partial MainForm）
  DatabasePage.cs               数据库页框架 + DatabaseFeature 注册
  DatabaseModels.cs             连接配置、查询结果、表/列/Key 等模型
  DatabaseDrivers.cs            IDatabaseProvider / IDatabaseConnection / IDatabaseWorkspace 与注册表
  DatabaseConnectionDialog.cs   连接配置对话框
  DatabaseConnectionStore.cs    连接配置持久化（DPAPI）
  MySqlSession.cs               MySQL 会话 + MySqlProvider
  MySqlWorkspace.cs             MySQL 库表树、SQL、表数据、表结构
  SqlGuard.cs                   MySQL 语句闸门
  RedisClient.cs                Redis RESP2 客户端、SCAN、Cluster 路由
  RedisCommandGuard.cs          Redis 命令白名单 / 黑名单、命令解析
  RedisWorkspace.cs             Redis 工作区 + RedisProvider
  DatabaseAi.cs                 SQL / Redis 结果的 AI 分析提示词
  DataGridExport.cs             DataGridView → DataTable → CSV
  DatabaseSelftest.cs           无网络自检
vendor/                         MySqlConnector 及其托管依赖（编译时引用并嵌入 exe）
```

## 功能页扩展

主窗口固定保留 URL、请求详情、失败请求、性能、响应、截图识别、日志翻译七个内置页；之后的功能页统一走注册表：

1. 实现 `IAppFeature`：

   ```csharp
   internal sealed class FooFeature : IAppFeature
   {
       public string Id { get { return "foo"; } }
       public string Title { get { return "新功能"; } }
       public bool UsesFullHeight { get { return true; } }
       public Control CreatePage(AppFeatureContext context) { return new FooPage(context); }
   }
   ```

2. 在 `src/Core/AppFeature.cs` 的 `AppFeatures.CreateAll()` 里注册：

   ```csharp
   features.Add(new FooFeature());
   ```

3. 页面需要快捷键或关闭确认时，可选实现 `IAppFeaturePage`（`HandleShortcut` / `ConfirmClose` / `Shutdown`）。主窗口只把快捷键转给**当前页**，不会串到其他页。

`UsesFullHeight = true` 的页签会隐藏上方的 cURL 输入区，占满整个窗口高度；标签页位置、标题和初始化异常兜底都由 `AppendFeatureTabs` 统一处理，不需要改 `MainForm` 构造函数。

`AppFeatureContext` 提供主窗口能力：`Owner`、状态栏 `SetStatus`、当前日志文本 `GetLogText`、AI 配置 `LoadAiSettings` / `ShowAiSettings`。功能页不要直接引用 `MainForm` 的内部控件。

## 数据库层

数据库页分四层：

```text
DatabasePage                 连接配置、选择连接、连接/断开、生命周期
    └── IDatabaseProvider    按 Kind 创建连接和工作区（DatabaseProviders 注册）
            ├── IDatabaseConnection   协议会话：查询、写操作、断开
            └── IDatabaseWorkspace    该数据库类型的浏览/编辑 UI
```

- **MySQL**：`MySqlProvider` → `MySqlSession` + `MySqlWorkspace`。驱动是 MySqlConnector 1.3.14（net45），编译时作为引用、运行时从 exe 资源加载。
- **Redis**：`RedisProvider` → `RedisClient` + `RedisWorkspace`。客户端是项目内实现的 RESP2，不依赖 StackExchange.Redis；支持 AUTH、TLS、SCAN、Cluster 槽位路由、`MOVED` / `ASK`。
- **连接配置**：`DatabaseConnectionProfile` 是纯数据对象；`DatabaseConnectionStore` 写 `%APPDATA%\curl2url\db-connections.json`，密码字段用 DPAPI 加密。可用 `CURL2URL_DB_CONFIG` 改路径。
- **AI**：`DatabaseAi` 复用现有的 `AiOcrSettings` 和 `AiOcrRunner.TryChat`，只发送连接环境、SQL / 命令、错误、表结构和打码后的结果预览；必须用户显式点击并二次确认。
- **自检**：`--selftest` 会执行 `DatabaseSelftest.Run()`，覆盖 SQL 闸门、Redis 命令闸门、CRC16、CSV、DPAPI、Provider 注册和驱动加载。

### 新增数据库类型

以 PostgreSQL 为例：

1. 新增 `PostgresSession : IDatabaseConnection` 和 `PostgresWorkspace : UserControl, IDatabaseWorkspace`。
2. 新增 `PostgresProvider : IDatabaseProvider`，实现 `Open` 和 `CreateWorkspace`。
3. 注册到 `DatabaseProviders.Providers`。
4. 在 `DatabaseKinds` 增加 Kind 和显示名；如果连接字段与 MySQL/Redis 差异较大，再扩展 `DatabaseConnectionDialog` 的字段区。

`DatabasePage`、`MainForm` 都不用改。查询结果建议统一返回 `DataTable` / `DbQueryResult`，这样可以直接复用 `DataGridExport` 和 AI 分析。

### 新增功能页（非数据库）

按「功能页扩展」三步注册即可。页签会自动追加到日志翻译之后；需要独立布局的页面把 `UsesFullHeight` 设为 `true`。

## 安全边界

- 连接默认只读。MySQL 只读模式有两层：客户端 `SqlGuard` 单语句/只读白名单，以及会话级 `SET SESSION TRANSACTION READ ONLY`。
- 写权限按连接开启；生产环境开启写权限要输入「生产」二次确认。每次写 SQL / 表保存 / Redis 写命令都会再次确认。
- MySQL 写模式仍禁止服务器管理语句（`SET GLOBAL`、`SET PASSWORD`、`GRANT`、`KILL`、`SHUTDOWN` 等），并拒绝 `INTO OUTFILE` 和多语句。
- Redis 写模式也始终禁用 `FLUSHALL`、`FLUSHDB`、`CONFIG`、`EVAL`、`SCRIPT`、`SHUTDOWN`、`MONITOR`、`SUBSCRIBE`、`MULTI`、`KEYS` 等危险或会阻塞连接的命令。
- 这些是客户端兜底，不是数据库权限系统；生产账号仍应使用最小权限。

## 线程与资源

- 所有网络操作通过 `AsyncUi.Run` 放到后台线程，完成后 `BeginInvoke` 回 UI 线程；控件已释放时回调直接丢弃。
- MySQL 会话内部串行化，同一连接不会并发执行；查询有超时和取消入口。
- Redis 客户端用 `lock` 串行化命令，避免 RESP2 流交错；断线不会自动重连，错误会直接显示。
- win32 形式的 `curl2url.exe` 在 `Main` 第一步调用 `EmbeddedAssemblies.Install()`，确保任何 MySqlConnector 类型被 JIT 之前已经挂上 `AssemblyResolve`。

## 已知限制

- 查询结果默认最多 500 行；超大字段和 Redis 大 Key 会截断显示。
- Redis 支持单机 / Cluster / TLS，不包含 Sentinel 自动发现；Redis 命令控制台不提供事务、订阅、阻塞命令。
- MySQL 不支持 SSH 隧道、多结果集和存储过程结果集展示。
- 单 exe 交付意味着新增第三方驱动时也要把依赖嵌入 `vendor/` 并更新 `build.cmd` / `EmbeddedAssemblies`。
