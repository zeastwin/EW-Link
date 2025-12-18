# AGENTS
## 语言约定
- 所有代码注释、技术文档、提交说明一律使用**简体中文**。
- 代码中的标识符、API 名称、JSON 字段保持英文；用户可见字符串按 UI 设计需求呈现。
- 面向开发者的说明、分析、总结全部用简体中文输出。

## 目标与技术栈
- ASP.NET Core Razor Pages 应用（net10.0），提供 `/resources` 文件门户，分永久区与临时区。
- 前端基于 Bootstrap 样式，核心页面为 `Pages/Resources/Index.cshtml`，使用 Fetch 局部刷新增强导航。

## 存储模型与限制
- 资源根目录默认 `/Data/resources`，可通过环境变量 `RESOURCES_ROOT` 或配置键 `Resources:Root` 覆盖（本地 `appsettings.json` 指向 `D:\Data\resources`）。
- 子目录名为 `永久文件夹`（永久区）与 `临时文件夹`（临时区），可在 `ResourceOptions` 配置。
- 上传限制 10GB，由 Kestrel 请求限制与 `ResourceStore` 双重校验，nginx `client_max_body_size` 亦同步为 10GB；请求头超时时间 2 分钟。
- 数据保护密钥持久化路径由环境变量 `DATA_PROTECTION_KEYS_ROOT` 控制，默认 `/var/aspnet-keys`（Docker 挂载到宿主 `resources_keys` 卷）。
- 分享直链可配置外部基址 `Share:PublicBaseUrl`，生成的下载链接会优先使用该域名/端口，未配置时回落当前请求 Host。

## 关键服务与安全措施
- `ResourceStore`（`Services/ResourceStore.cs`）：列目录、上传、删除、单文件读取；校验文件/文件夹名；同名冲突追加 ` (n)`；删除改为移入回收站（`.trash`，含元数据），支持重命名、批量移动（同区内，阻止自包含移动与同名覆盖）、回收站列举/还原/彻底删除；批量打包时可递归收集文件夹（含空目录占位），去重并阻止直接打包资源根。
- `ResourcePathHelper`：归一化子根路径，拒绝根路径、`..`、无效字符或驱动器标记，阻止越界访问。
- `ZipStreamService`：将多文件流式写入 ZIP，归一化条目名称，不关闭 HTTP 响应流；页面处理下载时使用 `Response.BodyWriter.AsStream` 适配禁用同步 IO。
- `TemporaryCleanupService`：每小时清理临时目录，删除 24 小时前的文件并移除空目录。
- `TrashCleanupService`：每小时清理回收站中过期（默认 30 天）条目。
- 分享直链：支持配置 `Share:ForcePort` 强制输出指定端口（例如 8080），未配置则跟随当前请求的 Host/端口。

## UI 行为（`Pages/Resources/Index`）
- 查询参数：`tab`（`permanent`/`temporary`）、`path`、`q`（名称过滤）、`sort`（`name`/`time`/`size`）、`dir`（`asc`/`desc`）。
- 功能：带进度的 AJAX 上传、弹窗创建文件夹、选中文件/文件夹批量 ZIP（目录递归打包，空目录占位）、批量删除（入回收站）、批量移动、文件/文件夹重命名、分享直链（默认 7 天有效，保护 token）、回收站浏览/批量还原/彻底删除、文件预览（文本/图片/音频/视频/PDF/ZIP 列表，20MB 内）、面包屑/返回、双击进入目录、客户端计数已选项。
- 统计信息（目录/文件数、总大小）服务器端计算。
- 排序/升降按钮位于操作条右侧，与“新建文件夹”同行；搜索框回车触发，无单独搜索按钮。

## 运行与部署
- 本地：`dotnet run --project EW-Link.csproj`（需 .NET 10 预览 SDK），启动时自动确保资源根目录存在。
- Docker：`docker compose up --build`（应用监听 8080，nginx 反代到宿主 8080），挂载资源目录到 `D:/Data/resources`，密钥使用 named volume。

## 重要文件索引
- `Program.cs`：DI 注册、Kestrel/请求限制、资源配置、数据保护、后台服务。
- `Services/*`：资源操作、路径校验、ZIP 流、临时清理。
- `Pages/Resources/Index.cshtml*`：列表展示与上传/下载/删除/打包/建目录处理。
- `Dockerfile`、`docker-compose.yml`、`nginx.conf`：容器与反向代理配置。
