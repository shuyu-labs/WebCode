# WebCode

<p align="center">
  <a href="README.md">简体中文</a> | <a href="README_EN.md">English</a>
</p>

<p align="center">
  <strong>把 AI CLI、Web 会话、移动端和飞书工作流接到同一个控制面板里</strong>
</p>

<p align="center">
  通过浏览器或飞书卡片管理 Claude Code、Codex、OpenCode 会话、工作区、项目和命令执行。
</p>

---

## 功能目录

- [项目简介](#项目简介)
- [当前重点能力](#当前重点能力)
- [多 AI CLI 接入](#多-ai-cli-接入)
- [Web、移动端、飞书统一会话流](#web移动端飞书统一会话流)
- [多用户与权限控制](#多用户与权限控制)
- [外部会话导入与恢复](#外部会话导入与恢复)
- [Codex `/goal` 快速目标能力](#codex-goal-快速目标能力)
- [Superpowers 工作流](#superpowers-工作流)
- [Windows 安装包发布](#windows-安装包发布)
- [`cc-switch` 托管模型](#cc-switch-托管模型)
- [快速开始](#快速开始)
- [配置说明](#配置说明)
- [Windows 发布维护](#windows-发布维护)
- [项目结构](#项目结构)
- [技术栈](#技术栈)
- [常用文档](#常用文档)

## 项目简介

WebCode 是一个基于 `Blazor Server + .NET 10` 的 AI CLI 工作平台。它不是单机聊天壳，而是把本地或服务器上的 AI CLI 包装成一个可管理、可部署、可协作、可远程访问的工作系统。

当前仓库已经覆盖这些核心场景：

- 在 Web 端创建、切换、关闭和恢复 AI 会话
- 为每个会话绑定独立工作区和项目目录
- 导入当前系统账户下已有的 `Claude Code`、`Codex`、`OpenCode` 本地会话
- 从原始 CLI transcript 恢复历史消息，而不只依赖 WebCode 自己的消息存档
- 在桌面端、移动端和飞书卡片里使用同一批会话
- 为团队用户配置可用工具、目录白名单、飞书机器人和权限
- 把 `cc-switch` 作为受管 CLI 的唯一 Provider 权威源

如果你需要的是“可部署的 AI CLI 控制台”，而不是单机本地包装器，这个仓库就是为这个目标设计的。

## 当前重点能力

### 多 AI CLI 接入

当前已适配或直接支持：

| 工具 | 说明 | 状态 |
|------|------|------|
| `Claude Code` | 会话管理、流式输出、Transcript 恢复 | 可用 |
| `Codex CLI` | JSONL 输出、沙箱/审批模式、会话执行 | 可用 |
| `OpenCode` | 多模型工作流、会话导入与执行 | 可用 |
| 自定义 CLI | 可继续按适配器模式扩展 | 可扩展 |

相关实现主要位于 [WebCodeCli.Domain/Domain/Service/Adapters](./WebCodeCli.Domain/Domain/Service/Adapters)。

### Web、移动端、飞书统一会话流

- 每个会话可绑定独立工作区
- 支持默认目录、已有目录、项目目录和自定义目录
- 支持文件浏览、预览、上传、复制路径等工作区操作
- 支持历史会话切换、关闭、隔离和清理
- 支持导入本地 CLI 会话并继续在 WebCode 中使用
- 支持在移动端抽屉和飞书会话卡片里直接管理会话

#### 功能入口矩阵

下表用于快速说明三类主要入口当前各自覆盖的能力范围：

| 能力 | 桌面 Web | 移动端 | 飞书 |
|------|----------|--------|------|
| 创建、切换、关闭会话 | 支持 | 支持 | 支持 |
| 导入本地 CLI 会话 | 支持 | 支持 | 支持 |
| 会话级 Provider 同步 | 支持 | 支持 | 支持 |
| 工作区浏览与文件操作 | 支持最完整 | 支持常用操作 | 以会话/卡片操作为主 |
| 流式输出查看 | 支持最完整 | 支持 | 支持卡片流式回显 |
| `/goal` 快捷目标 | 支持 | 支持 | 支持 |
| `Superpowers` 快捷工作流 | 支持 | 支持 | 支持 |
| 多用户管理与系统配置 | 支持最完整 | 适合轻量查看/操作 | 不作为主要入口 |
| 安装与部署维护 | 主要依赖文档和本地脚本 | 不作为主要入口 | 不作为主要入口 |

补充说明：

- 桌面 Web 是能力最完整的主控制台，适合管理、调试、配置和重度会话操作
- 移动端保留高频会话操作，重点是随时查看、切换、继续和触发快捷动作
- 飞书更偏消息驱动和卡片驱动，适合在聊天上下文里直接继续会话、执行快捷动作和接收流式结果

#### 飞书能力清单

飞书当前不是“只做消息通知”，而是已经覆盖一条可直接工作的会话与卡片交互链路：

- 支持把飞书会话绑定到 WebCode 用户和本地/服务器侧 CLI 会话
- 支持在飞书卡片中创建、切换、关闭、继续会话
- 支持在飞书端浏览并导入外部 CLI 会话
- 支持在飞书卡片中触发 `/goal`、`Superpowers` 等快捷动作
- 支持流式卡片更新，在执行过程中持续回显输出内容
- 支持会话级 Provider 同步，确保飞书侧也遵循 `cc-switch` 当前激活状态
- 支持 Reply TTS 相关能力，用于把回复内容进一步转换为语音或语音服务调用链
- 支持帮助卡片、快捷入口卡片、会话管理卡片等多种交互载体

适合的飞书使用场景包括：

- 在群聊或私聊里直接继续一个已有 AI CLI 会话
- 在移动办公场景下用卡片完成高频操作，而不打开完整 Web 控制台
- 用机器人卡片承接流式输出、快捷动作、目标设定和会话切换

### 多用户与权限控制

- 用户启用/禁用
- 用户可用 CLI 工具限制
- 用户白名单目录策略
- 用户级飞书机器人配置
- 默认共享配置 + 用户覆盖配置

### 外部会话导入与恢复

外部 CLI 会话导入当前具备这些约束：

- 只扫描当前操作系统账户下可访问的本地会话
- 只显示工作区落在允许目录或白名单中的会话
- 同一个 `(ToolId, CliThreadId)` 只能被一个 WebCode 用户占用
- Web 端与飞书端都支持分页浏览、导入并切换到这些会话

### Codex `/goal` 快速目标能力

当前版本已经补齐 Codex `/goal` 相关能力，覆盖 Web 端、移动端和飞书卡片场景：

- WebCode 会先探测当前 Codex CLI 版本与 `goals` feature 是否可用
- 当探测结果可用时，会在会话级 `.codex/config.toml` 中自动注入 `goals = true`
- 用户可以通过快速输入或快捷动作把普通文本自动补成 `/goal ...`
- `/goal` 能力按会话生效，不要求手工长期维护全局 `config.toml`

说明：

- 当前要求 Codex CLI 版本不低于 `0.128.0`
- WebCode 在 Windows 下会自动解析 `codex.exe`、`codex.cmd`、`codex.bat`、`codex.ps1` 等入口，避免因包装脚本导致版本探测失败
- 如果 Codex 本身未提供 `goals` feature，WebCode 不会强行注入该配置

### Superpowers 工作流

当前仓库已经接入 `Superpowers` 工作流增强能力，用来把普通 CLI 会话提升为“带策略和快捷动作的执行流”：

- 支持在 Web 端、移动端和飞书卡片中触发工作流型快捷动作
- 支持把用户输入快速包装成结构化工作流提示，而不只是原样转发给 CLI
- 支持能力探测，按当前环境决定是否展示或启用对应的工作流入口
- 支持把 `Superpowers` 与现有 `Claude Code`、`Codex`、`OpenCode` 会话流结合使用

当前文档和实现已经覆盖的典型工作流包括：

- 计划类工作流，例如 `plan`、`ralplan`
- 持续执行类工作流，例如 `ralph`
- 深度澄清类工作流，例如 `deep-interview`
- 协作/并行类工作流，例如 `team`、`ultrawork`
- 快捷动作类封装，例如面向会话的一键注入提示词和工作流命令

说明：

- `Superpowers` 不是单独的 Web 页面，而是叠加在现有会话、快捷动作、飞书卡片交互之上的工作流层
- 哪些工作流可用，取决于当前工具、环境能力探测结果以及会话上下文
- 相关实现可参考：
  - [WebCodeCli.Domain/Domain/Service/SuperpowersCapabilityService.cs](./WebCodeCli.Domain/Domain/Service/SuperpowersCapabilityService.cs)
  - [WebCodeCli.Domain/Domain/Service/SuperpowersPromptBuilder.cs](./WebCodeCli.Domain/Domain/Service/SuperpowersPromptBuilder.cs)
  - [WebCodeCli.Domain/Domain/Service/Channels/SuperpowersQuickActionCardHelper.cs](./WebCodeCli.Domain/Domain/Service/Channels/SuperpowersQuickActionCardHelper.cs)
  - [WebCodeCli/Pages/CodeAssistant.razor.cs](./WebCodeCli/Pages/CodeAssistant.razor.cs)
  - [WebCodeCli/Pages/CodeAssistantMobile.razor.cs](./WebCodeCli/Pages/CodeAssistantMobile.razor.cs)

### Windows 安装包发布

仓库已经内置 Windows 安装包构建脚本，可直接生成：

- 安装版 `WebCode-Setup-vX.Y.Z-win-x64.exe`
- 便携版 `WebCode-vX.Y.Z-win-x64-portable.zip`
- 校验文件 `SHA256SUMS.txt`
- Release 说明 `RELEASE_NOTES.md`
- 包含 Reply TTS 服务与运行所需资源的 `tts-bundle/`

## `cc-switch` 托管模型

从当前版本开始，`Claude Code`、`Codex`、`OpenCode` 这三类受管助手遵循同一套规则：

1. `cc-switch` 是唯一 Provider 权威源。
2. WebCode 不再允许为这三类工具手动填写环境变量、编辑 Provider 或切换 Profile。
3. Provider 的激活与切换必须在 `cc-switch` 中完成。
4. WebCode 只读取 `cc-switch` 的当前状态和 live config。

WebCode 会读取这些来源：

- `~/.cc-switch/settings.json`
- `~/.cc-switch/cc-switch.db`
- `cc-switch` 写出的各工具 live 配置文件

### 会话级 Provider 快照

WebCode 现在遵循“像终端窗口一样”的会话语义：

- 新会话在第一次运行时，会复制当前激活 Provider 的 live config 作为该会话的快照
- 已经在使用中的旧会话，不会因为你在 `cc-switch` 里切换了 Provider 而自动跟着变
- 只有当你显式点击“同步 Provider”时，旧会话才会切换到 `cc-switch` 当前激活的 Provider

这个同步入口当前已经补齐到：

- 桌面 Web 会话列表
- 移动端会话抽屉
- 飞书会话管理卡片

如果会话快照丢失或损坏，WebCode 会阻止继续执行，并提示你显式同步，而不是悄悄回退到机器当前 live 配置。

### OpenCode 的启用方式

`OpenCode` 和 `Claude Code`、`Codex` 一样受 `cc-switch` 管理。

- 初始化时可以选择启用哪些受管助手
- 如果初始化时没勾选，后续也可以在设置里的助手管理中重新启用
- 是否真正可用，取决于 `cc-switch` 当前是否已经为该工具准备好可启动的 Provider 和 live config

## 快速开始

### 方式一：Docker 部署

适合试用、内网部署和小团队使用。

```bash
git clone https://github.com/shuyu-labs/WebCode.git
cd WebCode
docker compose up -d
```

启动后访问：

- `http://localhost:5000`

更多部署细节可查看：

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/Docker-CLI-集成部署指南.md](./docs/Docker-CLI-集成部署指南.md)

### 方式二：Windows Release 包

适合直接在 Windows 机器上运行 WebCode，不需要预装 `.NET SDK`。

下载入口：

- [GitHub Releases](https://github.com/shuyu-labs/WebCode/releases/latest)

当前 Windows 发布资产包括：

- `WebCode-Setup-vX.Y.Z-win-x64.exe`
- `WebCode-vX.Y.Z-win-x64-portable.zip`
- `SHA256SUMS.txt`
- `RELEASE_NOTES.md`

安装版与便携版都使用 `win-x64` 自包含运行时。默认首次启动后访问：

- `http://localhost:6021`

程序目录下默认会准备：

- `data/`
- `logs/`
- `workspaces/`

安装版升级时会保留已有的 `appsettings.json`。

### 方式三：本地开发运行

适合调试、二次开发和本地联调。

#### 环境要求

- `.NET 10 SDK`
- `cc-switch` 已正确安装并已为目标受管工具完成配置
- 已安装 `claude`、`codex`、`opencode` 等目标 CLI

#### 启动命令

```bash
git clone https://github.com/shuyu-labs/WebCode.git
cd WebCode
dotnet restore
dotnet run --project WebCodeCli
```

默认访问地址：

- `http://localhost:6021`

如果你修改了根目录 [appsettings.json](./appsettings.json) 或 [WebCodeCli/appsettings.json](./WebCodeCli/appsettings.json) 中的 `urls` 配置，请以实际监听端口为准。

## 首次初始化建议

当前初始化流程建议按这个顺序完成：

1. 创建管理员账户
2. 确认是否开启登录认证
3. 选择希望在 WebCode 中启用的受管助手
4. 在向导里检查 `cc-switch` 就绪状态，并确保所选助手都已经可启动
5. 验证工作区根目录和存储目录
6. 如需飞书集成，再配置飞书机器人参数
7. 如需多用户，进入管理界面配置用户、目录白名单和工具权限
8. 如需恢复已有上下文，可导入本地外部 CLI 会话

初始化向导不再承担这些职责：

- 手动录入受管工具环境变量
- 手动切换受管工具 Provider
- 手动编辑受管工具 live config

这些动作现在都必须在 `cc-switch` 中完成。

## 配置说明

### 受管助手配置

对 `Claude Code`、`Codex`、`OpenCode` 来说：

- Provider 切换只能在 `cc-switch` 里做
- WebCode 设置页只能查看 `cc-switch` 状态，不能手动编辑环境变量
- 助手管理页可以启用或停用助手，但不会替代 `cc-switch` 管理 Provider
- 会话级“同步 Provider”只会把当前 `cc-switch` 激活状态复制到指定会话，不会改写 `cc-switch`

### 自定义 CLI 配置

如果你要接入非 `cc-switch` 受管的自定义 CLI，仍然可以通过 `CliTools.Tools` 扩展工具定义。相关文档可参考：

- [cli/README.md](./cli/README.md)
- [docs/CLI工具配置说明.md](./docs/CLI工具配置说明.md)
- [docs/Codex配置说明.md](./docs/Codex配置说明.md)

### Docker 数据目录

`docker-compose.yml` 默认会挂载这些目录：

- `./webcodecli-data`：数据库与运行数据
- `./webcodecli-workspaces`：工作区目录
- `./webcodecli-logs`：日志

### 数据库

默认数据库为 SQLite：

- `WebCodeCli.db`

本地开发默认连接字符串来自根目录 [appsettings.json](./appsettings.json)。

## Windows 发布维护

仓库内置安装包构建脚本：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-windows-installer.ps1
```

如果你要生成“包含 Reply TTS / Kokoro 能力”的本地 Windows 安装包，应该优先走这条脚本，而不是单独 `dotnet publish`。原因是安装包脚本除了发布主程序外，还会额外处理这些内容：

- 调整发布目录里的 `appsettings.json`
- 拷贝 `tools/sherpa-kokoro-service`
- 组装 `tts-bundle`
- 生成 Inno Setup 安装包与 portable zip

脚本会读取 [Directory.Build.props](./Directory.Build.props) 中的版本号，并在 `artifacts/windows-installer/vX.Y.Z/` 下生成：

- `publish/`
- `WebCode-vX.Y.Z-win-x64-portable.zip`
- `installer/WebCode-Setup-vX.Y.Z-win-x64.exe`
- `SHA256SUMS.txt`
- `RELEASE_NOTES.md`
- `tts-bundle/`

在 Windows 机器上，如果默认输出目录存在旧文件锁定，或者 Inno Setup 遇到长路径问题，可以显式指定一个较短的输出目录，例如：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-windows-installer.ps1 -OutputRoot D:\wci
```

这种方式同样会生成完整的安装版、便携版和 TTS 资源目录，适合本地快速出包。

构建机要求：

- Windows
- `.NET 10 SDK`
- `Inno Setup 6`

## 项目结构

```text
WebCode/
├── WebCodeCli/                # Web 应用（Blazor Server）
├── WebCodeCli.Domain/         # 领域服务、仓储、CLI/飞书适配
├── WebCodeCli.Domain.Tests/   # 领域层测试
├── tests/WebCodeCli.Tests/    # Web / 集成相关测试
├── cli/                       # CLI 使用说明
├── docs/                      # 额外文档
├── docker-compose.yml         # Docker 部署入口
└── README.md
```

当前实际项目名为：

- `WebCodeCli`
- `WebCodeCli.Domain`

如果你在旧文档或历史笔记里看到 `WebCode` / `WebCode.Domain` 这样的旧路径描述，请以仓库真实目录为准。

## 技术栈

| 类别 | 技术 |
|------|------|
| Web 框架 | Blazor Server |
| 运行时 | .NET 10 |
| 编辑器 | Monaco Editor |
| 数据访问 | SqlSugar |
| 默认数据库 | SQLite |
| 反向代理 | YARP |
| Markdown | Markdig |
| AI CLI 集成 | Claude Code / Codex / OpenCode |

## 常用文档

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/QUICKSTART_CodeAssistant.md](./docs/QUICKSTART_CodeAssistant.md)
- [docs/README_CodeAssistant.md](./docs/README_CodeAssistant.md)
- [docs/superpowers/plans/2026-04-30-superpowers-plan-quick-actions-implementation.md](./docs/superpowers/plans/2026-04-30-superpowers-plan-quick-actions-implementation.md)
- [docs/superpowers/specs/2026-04-30-superpowers-plan-quick-actions-design.md](./docs/superpowers/specs/2026-04-30-superpowers-plan-quick-actions-design.md)
- [docs/superpowers/plans/2026-04-24-ralph-runtime-implementation.md](./docs/superpowers/plans/2026-04-24-ralph-runtime-implementation.md)
- [docs/superpowers/specs/2026-04-24-ralph-runtime-design.md](./docs/superpowers/specs/2026-04-24-ralph-runtime-design.md)
- [docs/workspace-management-guide.md](./docs/workspace-management-guide.md)
- [docs/workspace-management-deployment-guide.md](./docs/workspace-management-deployment-guide.md)

## 许可证

本项目采用 [AGPLv3](LICENSE)。

- 开源使用：遵循 AGPLv3
- 商业授权：请联系仓库维护者或相关商务渠道

---

<p align="center">
  <strong>让 AI CLI 从“本地命令”升级为“可管理、可部署、可协作的工作平台”</strong>
</p>
