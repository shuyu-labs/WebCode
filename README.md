[![MseeP.ai Security Assessment Badge](https://mseep.net/pr/xuzeyu91-webcode-badge.png)](https://mseep.ai/app/xuzeyu91-webcode)

# WebCode

<p align="center">
  <a href="README.md">简体中文</a> | <a href="README_EN.md">English</a>
</p>

<p align="center">
  <strong>一个把 AI CLI、Web 会话、多用户权限和飞书机器人串起来的工作平台</strong>
</p>

<p align="center">
  通过浏览器或飞书卡片管理 AI 会话、工作区、项目和命令执行，支持桌面和移动端。
</p>

---

## 项目简介

WebCode 是一个基于 `Blazor Server + .NET 10` 的 AI 工作平台，目标不是单纯做聊天界面，而是把本地/服务器上的 AI CLI 工具包装成一个可管理、可协作、可远程访问的工作系统。

它当前覆盖的核心场景包括：

- 在 Web 端创建和管理 AI 会话
- 为不同会话绑定独立工作区
- 在飞书中通过机器人和卡片完成会话、项目、目录等操作
- 为不同用户配置各自的 CLI 环境、飞书机器人、可访问目录和工具权限
- 在手机、平板和桌面浏览器中持续使用同一套工作流

如果你需要一个“可部署的 AI CLI 控制台”，而不是单机本地的命令行包装器，这个项目就是为这个目标设计的。

## 核心能力

### 1. 多 AI CLI 接入

当前仓库内已经围绕以下工具做了适配或运行支持：

| 工具 | 说明 | 状态 |
|------|------|------|
| `Claude Code` | 会话管理、流式输出、适配器解析 | 可用 |
| `Codex CLI` | JSONL 输出、沙箱/审批模式、会话执行 | 可用 |
| `OpenCode` | 多模型工作流集成 | 可用 |
| 其他 CLI | 可按适配器模式继续扩展 | 可扩展 |

相关实现主要位于 [WebCodeCli.Domain/Domain/Service/Adapters](./WebCodeCli.Domain/Domain/Service/Adapters)。

### 2. Web 会话与工作区管理

- 每个会话可绑定独立工作区
- 支持默认目录、已有目录、项目目录、自定义目录
- 支持文件浏览、预览、上传、复制路径等工作区操作
- 支持历史会话切换、关闭、隔离和清理
- 可与项目管理联动，从项目直接创建会话

### 3. 多用户与权限控制

当前系统已经具备多用户基础能力：

- 用户启用/禁用
- 用户可用 CLI 工具限制
- 用户白名单目录策略
- 用户级 CLI 环境变量配置
- 用户级飞书机器人配置
- 默认共享配置 + 用户覆盖配置

这意味着系统已经不再是单用户本地玩具，而是可以作为团队内的 AI 工作平台运行。

### 4. 飞书机器人与卡片工作流

飞书侧不是简单消息转发，而是一套完整的工作入口：

- 用户绑定自己的飞书机器人
- 会话管理卡片
- 项目管理卡片
- 白名单目录浏览
- 项目克隆 / 拉取 / 分支切换
- 会话完成后的普通文本提醒

适配代码主要位于：

- [WebCodeCli.Domain/Domain/Service/Channels](./WebCodeCli.Domain/Domain/Service/Channels)

### 5. 桌面与移动端统一体验

- 响应式布局
- 移动端导航与输入区域优化
- 适配手机和平板
- 同一套会话、文件、预览、设置工作流

移动端界面示意：

![移动端界面](images/mobile.png)

## 产品截图

> 以下图片来自仓库内置素材，用于说明典型界面和使用方式。

![代码编程助手](images/coding.png)
![PPT/文档辅助](images/ppt.png)
![Skills/工作流](images/skill.png)
![游戏/创意示例](images/games.png)

## 快速开始

### 方式一：Docker 部署

这是最直接的启动方式，适合试用、内网部署和小团队使用。

```bash
git clone https://github.com/lusile2024/WebCode.git
cd WebCode
docker compose up -d
```

启动后访问：

- `http://localhost:5000`

首次访问会进入初始化流程。

更多部署细节可查看：

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/Docker-CLI-集成部署指南.md](./docs/Docker-CLI-集成部署指南.md)

### 方式二：本地开发运行

适合调试、二次开发和本地联调。

#### 环境要求

- `.NET 10 SDK`
- 已安装目标 AI CLI，例如 `claude`、`codex`、`opencode`

#### 启动命令

```bash
git clone https://github.com/lusile2024/WebCode.git
cd WebCode
dotnet restore
dotnet run --project WebCodeCli
```

默认访问地址：

- `http://localhost:5000`

## 首次初始化建议

首次启动建议按下面顺序完成配置：

1. 创建管理员账户
2. 确认是否开启登录认证
3. 配置至少一个可用 CLI 工具的环境变量
4. 验证工作区根目录和存储目录
5. 如需飞书集成，再配置飞书机器人参数
6. 如需多用户，进入管理界面配置用户、目录白名单和工具权限

初始化向导界面示意：

![设置向导 - 第一步](images/setup1.png)
![设置向导 - 第二步](images/setup2.png)
![设置向导 - 第三步](images/setup3.png)

## 配置说明

### CLI 配置

CLI 工具配置支持通过界面和配置文件两种方式维护。

典型配置结构如下：

```json
{
  "CliTools": {
    "Tools": [
      {
        "Id": "claude-code",
        "Name": "Claude Code",
        "Command": "claude",
        "ArgumentTemplate": "-p \"{prompt}\"",
        "Enabled": true
      },
      {
        "Id": "codex",
        "Name": "Codex",
        "Command": "codex",
        "ArgumentTemplate": "exec \"{prompt}\"",
        "Enabled": true
      }
    ]
  }
}
```

更详细的配置与说明可参考：

- [cli/README.md](./cli/README.md)
- [docs/CLI工具配置说明.md](./docs/CLI工具配置说明.md)
- [docs/Codex配置说明.md](./docs/Codex配置说明.md)

### Docker 数据目录

`docker-compose.yml` 默认会挂载以下目录：

- `./webcodecli-data`：数据库与运行数据
- `./webcodecli-workspaces`：工作区目录
- `./webcodecli-logs`：日志

### 数据库

默认使用 SQLite：

- `WebCodeCli.db`

本地开发默认连接字符串来自根目录 [appsettings.json](./appsettings.json)。

## 多用户与飞书建议

如果你准备把它作为团队服务运行，建议优先关注这几项：

- 为每个用户设置独立的白名单目录
- 为每个用户限制可用 CLI 工具
- 为每个用户配置自己的飞书机器人
- 避免把数据库文件提交到 Git
- 明确区分“共享默认配置”和“用户覆盖配置”

飞书和多用户能力相关代码分布在：

- [WebCodeCli/Controllers](./WebCodeCli/Controllers)
- [WebCodeCli/Components](./WebCodeCli/Components)
- [WebCodeCli.Domain/Domain/Service/Channels](./WebCodeCli.Domain/Domain/Service/Channels)
- [WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig](./WebCodeCli.Domain/Repositories/Base/UserFeishuBotConfig)

## 项目结构

当前仓库的主要结构如下：

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

和旧版文档相比，当前实际项目名已经是：

- `WebCodeCli`
- `WebCodeCli.Domain`

如果你在二次开发时看到 `WebCode` / `WebCode.Domain` 这类旧路径描述，请以仓库真实目录为准。

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
| AI CLI 集成 | Claude Code / Codex / OpenCode 等 |

## 常用文档

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/QUICKSTART_CodeAssistant.md](./docs/QUICKSTART_CodeAssistant.md)
- [docs/README_CodeAssistant.md](./docs/README_CodeAssistant.md)
- [docs/workspace-management-guide.md](./docs/workspace-management-guide.md)
- [docs/workspace-management-deployment-guide.md](./docs/workspace-management-deployment-guide.md)

## 在线试用与交流

如果你保留当前公开体验入口，可以继续使用下面的信息：

| 地址 | 用户名 | 密码 |
|------|--------|------|
| [https://webcode.tree456.com/](https://webcode.tree456.com/) | `treechat` | `treechat@123` |

> 这类公开演示环境仅适合体验，不适合存放敏感数据。

交流群二维码：

<p align="center">
  <img src="images/qrcode.jpg" alt="微信群二维码" width="200" />
</p>

## 许可证

本项目采用 [AGPLv3](LICENSE)。

- 开源使用：遵循 AGPLv3
- 商业授权：请联系仓库维护者或相关商务渠道

---

<p align="center">
  <strong>让 AI CLI 从“本地命令”升级为“可管理的工作平台”</strong>
</p>
