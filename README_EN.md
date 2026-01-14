# WebCodeCli

<p align="center">
  <a href="README.md">简体中文</a> | <a href="README_EN.md">English</a>
</p>

<p align="center">
  <strong>🚀 Code Anywhere, Anytime | 随时随地，云端编程</strong>
</p>

<p align="center">
  <em>Remote AI Programming Assistant supporting full-platform coding experience on phones, tablets, and computers</em>
</p>

---

## 🌐 Online Demo

Want to try it quickly? No installation required, just visit the online demo:

| Demo URL | Username | Password |
|----------|----------|----------|
| **[https://webcode.tree456.com/](https://webcode.tree456.com/)** | `treechat` | `treechat@123` |

> ⚠️ **Note**: This demo environment is for demonstration purposes only. Please do not store sensitive information.

---

## 💬 Community

Join our WeChat group to get the latest updates, report issues, and share your experiences:

<p align="center">
  <img src="images/qrcode.jpg" alt="WeChat Group QR Code" width="200" />
</p>

---

## ✨ Core Features

WebCodeCli is an **online AI programming platform** that allows you to remotely control various AI CLI programming assistants through a web browser, enabling **true code anywhere, anytime** - whether you're on the subway, in a coffee shop, or lounging on the sofa, you can code as long as you have a browser!

### 🎯 Main Features

- **📱 Mobile Programming** - Full mobile adaptation, touch optimized, smooth coding experience on phones
- **🤖 Multiple AI Assistant Support** - Integration with mainstream AI programming tools like Claude Code CLI, Codex CLI, GitHub Copilot CLI, etc.
- **⚡ Real-time Streaming Output** - Instantly see AI's thinking and coding process with typewriter effect
- **📂 Session Workspace** - Each session has an independent working directory with file isolation for security
- **🎨 Code Highlighting Preview** - Monaco Editor code highlighting, Markdown rendering, multi-view switching
- **🔐 Secure Execution** - Sandbox environment, command whitelist, injection protection

## 🖥️ Supported AI CLI Tools

### ✅ Fully Supported (Streaming JSON Parsing)

| Tool | Command | Features | Status |
|------|---------|----------|--------|
| **Claude Code CLI** | `claude` | MCP server, session recovery, stream-json output, proxy system | 🟢 Enabled |
| **Codex CLI** | `codex` | Sandbox execution, web search, Git integration, JSONL output | 🟢 Enabled |

### 🔧 To Be Extended

| Tool | Command | Features | Status |
|------|---------|----------|--------|
| **GitHub Copilot CLI** | `copilot` | GitHub integration, fine-grained permissions | 🟡 Configured, pending adaptation |
| **Qwen CLI** | `qwen` | YOLO mode, checkpoints, extension system | 🟡 Configured, pending adaptation |
| **Gemini CLI** | `gemini` | Google AI, simple configuration | 🟡 Configured, pending adaptation |

> 📚 For detailed CLI tool usage instructions, please refer to [cli/README.md](./cli/README.md)
> 
> 💡 **Extension Support**: To add new CLI tool adapters, please refer to the existing implementations in the `WebCodeCli.Domain/Domain/Service/Adapters/` directory

## 📱 Mobile Support

WebCodeCli is fully optimized for mobile devices:

- **Responsive Layout** - Adapts to phones, tablets, and desktop screens
- **Touch Optimization** - 44px touch targets, gesture support, press feedback
- **iOS Adaptation** - Solves Safari 100vh issue, adapts to notch screens
- **Portrait/Landscape Switching** - Seamless switching without content loss
- **Virtual Keyboard Adaptation** - Auto-adjusts viewport during input

### Tested Device Support

- ✅ iPhone SE / iPhone 12-14 / iPhone Pro Max
- ✅ iPad Mini / iPad Pro
- ✅ Android phones (various sizes)
- ✅ Chrome / Safari / Firefox / Edge mobile versions

## 🚀 Quick Start

### Requirements

- .NET 10.0 SDK
- Installed AI CLI tools (such as Claude Code CLI, Codex CLI)

### Installation and Running

```bash
# Clone the project
git clone https://github.com/xuzeyu91/WebCodeCli.git
cd WebCodeCli

# Restore dependencies
dotnet restore

# Run the application
dotnet run --project WebCodeCli
```

The application will start at `http://localhost:5000`, visit `/code-assistant` to start coding!

### Configure CLI Tools

Configure your AI CLI tools in `appsettings.json`:

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

## 🏗️ Technical Architecture

```
WebCodeCli/
├── WebCodeCli/              # Main project (Blazor Server)
│   ├── Components/          # Blazor components
│   ├── Pages/               # Pages
│   │   └── CodeAssistant/   # Programming assistant page
│   ├── wwwroot/             # Static resources
│   └── Program.cs           # Application entry
├── WebCodeCli.Domain/       # Domain layer (DDD)
│   ├── Domain/
│   │   ├── Model/           # Domain models
│   │   └── Service/         # Domain services
│   │       └── Adapters/    # CLI adapters
│   └── Repositories/        # Data repositories
└── cli/                     # CLI tools documentation
```

### Tech Stack

| Category | Technology |
|----------|------------|
| **Frontend Framework** | Blazor Server + Tailwind CSS |
| **UI Component Library** | Ant Design Blazor |
| **Code Editor** | Monaco Editor |
| **AI Features** | Microsoft Semantic Kernel |
| **Data Access** | SqlSugar ORM (Sqlite/PostgreSQL) |
| **Real-time Communication** | Server-Sent Events (SSE) |
| **Process Management** | System.Diagnostics.Process |

## 📋 Features

### Chat & Interaction
- ✅ Left-right split layout (top-bottom on mobile)
- ✅ Message history
- ✅ Streaming output (typewriter effect)
- ✅ Shortcut send (Ctrl+Enter)
- ✅ Clear session

### Preview & Display
- ✅ Code highlighting preview (Monaco Editor)
- ✅ Markdown rendering
- ✅ HTML live preview
- ✅ Raw output view
- ✅ Multi-tab switching

### Workspace Management
- ✅ Session-isolated workspace
- ✅ File upload/download
- ✅ File tree browsing
- ✅ Auto-cleanup of expired workspaces

### Security Features
- ✅ Command whitelist validation
- ✅ Input escaping (injection prevention)
- ✅ Concurrency limits
- ✅ Timeout control

## 📚 Documentation

- [Quick Start Guide](./docs/QUICKSTART_CodeAssistant.md)
- [Code Assistant Usage Guide](./docs/README_CodeAssistant.md)
- [CLI Tool Configuration Guide](./docs/CLI工具配置说明.md)
- [Mobile Compatibility Guide](./docs/移动端兼容性优化说明.md)
- [Codex Configuration Guide](./docs/Codex配置说明.md)
- [Environment Variables Configuration](./docs/环境变量配置功能说明.md)

## 💡 Recommended Skills

Excellent Skills resources to enhance AI programming assistant capabilities:

- [**planning-with-files**](https://github.com/OthmanAdi/planning-with-files) - File-based project planning and task management skill
- [**Anthropic Skills**](https://github.com/anthropics/skills) - Official Anthropic Skills collection providing various Claude enhancement capabilities
- [**UI/UX Pro Max Skill**](https://github.com/nextlevelbuilder/ui-ux-pro-max-skill) - Professional UI/UX design and development skill

> 💡 **Tip**: These Skills can be used with AI assistants like Claude Code CLI to enhance code generation, project planning, UI design capabilities, and more.

## 🔧 Use Cases

### 1. Mobile Office
Control AI programming assistants on the server remotely through a browser on your phone, handling code tasks anytime.

### 2. Remote Development
Remotely drive Claude Code or Codex for code development through a web interface, no local development environment installation required.

### 3. Code Review
Use AI assistants to quickly review code, generate test cases, and refactor code.

### 4. Learning Programming
Beginners can learn programming through AI assistants and get instant feedback and suggestions.

## 🛠️ Advanced Configuration

### Workspace Configuration

```json
"CliTools": {
  "TempWorkspaceRoot": "D:\\Temp\\WebCodeCli\\Workspaces",
  "WorkspaceExpirationHours": 24,
  "NpmGlobalPath": "",
  "MaxConcurrentExecutions": 3,
  "DefaultTimeoutSeconds": 300
}
```

| Configuration | Description | Example Value |
|---------------|-------------|---------------|
| `TempWorkspaceRoot` | Temporary workspace root directory for storing session-isolated working files | `D:\\Temp\\WebCodeCli\\Workspaces` |
| `WorkspaceExpirationHours` | Workspace expiration time (hours), automatically cleaned after expiration | `24` |
| `NpmGlobalPath` | NPM global installation path (optional, leave empty for auto-detection) | `C:\\Users\\YourUsername\\AppData\\Roaming\\npm\\` or leave empty `""` |
| `MaxConcurrentExecutions` | Maximum concurrent executions | `3` |
| `DefaultTimeoutSeconds` | Default timeout (seconds) | `300` |

> 💡 **Tips**:
> - **Windows Users**: NPM global path is typically `C:\Users\{username}\AppData\Roaming\npm\`
> - **Linux/Mac Users**: NPM global path is typically `/usr/local/bin/` or `~/.npm-global/bin/`
> - Workspace directory should use absolute path with sufficient disk space


## 🤝 Contributing

Issues and Pull Requests are welcome!

## 📄 License

[MIT License](LICENSE)

---

<p align="center">
  <strong>🌟 Let AI be your coding companion, anytime, anywhere 🌟</strong>
</p>
