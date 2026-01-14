# WebCodeCli

<p align="center">
  <strong>🚀 随时随地，云端编程 | Code Anywhere, Anytime</strong>
</p>

<p align="center">
  <em>远程驱动 AI 编程助手，支持手机、平板、电脑全平台编程体验</em>
</p>

---

## 🌐 在线试用

想要快速体验？无需安装，直接访问在线演示版：

| 试用地址 | 账号 | 密码 |
|----------|------|------|
| **[https://webcode.tree456.com/](https://webcode.tree456.com/)** | `treechat` | `treechat@123` |

> ⚠️ **注意**：试用环境为演示用途，请勿存储敏感信息

---

## 💬 交流群

扫码加入微信交流群，获取最新动态、反馈问题、交流使用心得：

<p align="center">
  <img src="images/qrcode.jpg" alt="微信群二维码" width="200" />
</p>

---

## ✨ 核心特色

WebCodeCli 是一个**在线 AI 编程平台**，让你可以通过 Web 浏览器远程控制各种 AI CLI 编程助手，实现真正的**随时随地编程**——无论你在地铁上、咖啡馆里，还是躺在沙发上，只要有浏览器就能写代码！

### 🎯 主要功能

- **📱 手机编程** - 完整的移动端适配，触摸优化，手机上也能流畅编程
- **🤖 多 AI 助手支持** - 集成 Claude Code CLI、Codex CLI、GitHub Copilot CLI 等主流 AI 编程工具
- **⚡ 实时流式输出** - 即时看到 AI 的思考和编码过程，打字机效果展示
- **📂 会话工作区** - 每个会话独立工作目录，文件隔离，安全可靠
- **🎨 代码高亮预览** - Monaco Editor 代码高亮，Markdown 渲染，多视图切换
- **🔐 安全执行** - 沙箱环境，命令白名单，防注入保护

## 🖥️ 支持的 AI CLI 工具

### ✅ 已完整支持（流式JSON解析）

| 工具 | 命令 | 特点 | 状态 |
|------|------|------|------|
| **Claude Code CLI** | `claude` | MCP 服务器、会话恢复、stream-json 输出、代理系统 | 🟢 已启用 |
| **Codex CLI** | `codex` | 沙箱执行、网络搜索、Git 集成、JSONL 输出 | 🟢 已启用 |

### 🔧 待扩展支持

| 工具 | 命令 | 特点 | 状态 |
|------|------|------|------|
| **GitHub Copilot CLI** | `copilot` | GitHub 集成、细粒度权限 | 🟡 已配置，待适配 |
| **Qwen CLI** | `qwen` | YOLO 模式、检查点、扩展系统 | 🟡 已配置，待适配 |
| **Gemini CLI** | `gemini` | Google AI、简洁配置 | 🟡 已配置，待适配 |

> 📚 详细的 CLI 工具使用说明请查看 [cli/README.md](./cli/README.md)
> 
> 💡 **扩展支持**：如需添加新的 CLI 工具适配器，请参考 `WebCodeCli.Domain/Domain/Service/Adapters/` 目录下的现有实现

## 📱 移动端支持

WebCodeCli 针对移动设备进行了全面优化：

- **响应式布局** - 自适应手机、平板、桌面各种屏幕
- **触摸优化** - 44px 触摸目标，手势支持，按压反馈
- **iOS 适配** - 解决 Safari 100vh 问题，适配刘海屏
- **横竖屏切换** - 无缝切换，内容不丢失
- **虚拟键盘适配** - 输入时自动调整视口

### 测试设备支持

- ✅ iPhone SE / iPhone 12-14 / iPhone Pro Max
- ✅ iPad Mini / iPad Pro
- ✅ Android 手机（各尺寸）
- ✅ Chrome / Safari / Firefox / Edge 移动版

## 🚀 快速开始

### 环境要求

- .NET 10.0 SDK
- 已安装的 AI CLI 工具（如 Claude Code CLI、Codex CLI）

### 安装运行

```bash
# 克隆项目
git clone https://github.com/your-repo/WebCodeCli.git
cd WebCodeCli

# 恢复依赖
dotnet restore

# 运行应用
dotnet run --project WebCodeCli
```

应用将在 `http://localhost:5000` 启动，访问 `/code-assistant` 开始编程！

### 配置 CLI 工具

在 `appsettings.json` 中配置你的 AI CLI 工具：

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
        "Name": "OpenAI Codex",
        "Command": "codex",
        "ArgumentTemplate": "exec \"{prompt}\"",
        "Enabled": true
      }
    ]
  }
}
```

## 🏗️ 技术架构

```
WebCodeCli/
├── WebCodeCli/              # 主项目 (Blazor Server)
│   ├── Components/          # Blazor 组件
│   ├── Pages/               # 页面
│   │   └── CodeAssistant/   # 编程助手页面
│   ├── wwwroot/             # 静态资源
│   └── Program.cs           # 应用入口
├── WebCodeCli.Domain/       # 领域层 (DDD)
│   ├── Domain/
│   │   ├── Model/           # 领域模型
│   │   └── Service/         # 领域服务
│   │       └── Adapters/    # CLI 适配器
│   └── Repositories/        # 数据仓储
└── cli/                     # CLI 工具文档
```

### 技术栈

| 类别 | 技术 |
|------|------|
| **前端框架** | Blazor Server + Tailwind CSS |
| **UI 组件库** | Ant Design Blazor |
| **代码编辑器** | Monaco Editor |
| **AI 功能** | Microsoft Semantic Kernel |
| **数据访问** | SqlSugar ORM (Sqlite/PostgreSQL) |
| **实时通信** | Server-Sent Events (SSE) |
| **进程管理** | System.Diagnostics.Process |

## 📋 功能特性

### 聊天与交互
- ✅ 左右分栏布局（移动端上下布局）
- ✅ 消息历史记录
- ✅ 流式输出（打字机效果）
- ✅ 快捷键发送 (Ctrl+Enter)
- ✅ 清空会话

### 预览与展示
- ✅ 代码高亮预览 (Monaco Editor)
- ✅ Markdown 渲染
- ✅ HTML 实时预览
- ✅ 原始输出查看
- ✅ 多 Tab 切换

### 工作区管理
- ✅ 会话隔离工作区
- ✅ 文件上传/下载
- ✅ 文件树浏览
- ✅ 自动清理过期工作区

### 安全特性
- ✅ 命令白名单验证
- ✅ 输入转义（防注入）
- ✅ 并发限制
- ✅ 超时控制

## 📚 文档

- [快速启动指南](./docs/QUICKSTART_CodeAssistant.md)
- [编程助手使用说明](./docs/README_CodeAssistant.md)
- [CLI 工具配置说明](./docs/CLI工具配置说明.md)
- [移动端兼容性说明](./docs/移动端兼容性优化说明.md)
- [Codex 配置说明](./docs/Codex配置说明.md)
- [环境变量配置](./docs/环境变量配置功能说明.md)

## 🔧 使用场景

### 1. 移动办公
在手机上通过浏览器远程控制服务器上的 AI 编程助手，随时处理代码任务。

### 2. 远程开发
通过 Web 界面远程驱动 Claude Code 或 Codex 进行代码开发，无需本地安装开发环境。

### 3. 代码审查
利用 AI 助手快速审查代码、生成测试用例、重构代码。

### 4. 学习编程
初学者可以通过 AI 助手学习编程，获得即时反馈和建议。

## 🛠️ 高级配置

### 工作区配置

```json
"CliTools": {
  "TempWorkspaceRoot": "D:\\Temp\\WebCodeCli\\Workspaces",
  "WorkspaceExpirationHours": 24,
  "MaxConcurrentExecutions": 3,
  "DefaultTimeoutSeconds": 300
}
```

## 📦 作为 .NET 模板使用

本项目可作为 .NET CLI 模板快速创建新项目：

```bash
# 安装模板
dotnet new install ./

# 创建新项目
dotnet new webcodecli -n YourProjectName

# 卸载模板
dotnet new uninstall WebCodeCli
```

## 🤝 贡献

欢迎提交 Issue 和 Pull Request！

## 📄 许可证

[MIT License](LICENSE)

---

<p align="center">
  <strong>🌟 让 AI 成为你的编程伙伴，随时随地，代码随行 🌟</strong>
</p>
