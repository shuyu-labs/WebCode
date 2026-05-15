# AI CLI 工具帮助文档总览

本目录包含多个 AI 编码助手 CLI 工具的帮助文档，方便快速查阅和使用。

## 文档列表

### 1. [Claude CLI](./claude-help.md)
- **命令**: `claude`
- **默认模型**: claude-sonnet-4-5-20250929
- **特点**: 
  - 强大的 MCP 服务器支持
  - 灵活的权限管理
  - 支持会话恢复和分叉
  - 多种输出格式（text、json、stream-json）
  - 代理（Agents）系统支持

### 2. [Codex CLI](./codex-help.md)
- **命令**: `codex`
- **特点**:
  - 沙箱执行环境
  - 灵活的权限批准策略
  - 支持网络搜索
  - Git 集成（apply 命令）
  - Cloud 任务集成
  - 开源模型支持（--oss）

### 3. [Qwen CLI](./qwen-help.md)
- **命令**: `qwen`
- **默认模型**: qwen3-coder-plus
- **特点**:
  - 简洁的命令行界面
  - YOLO 模式自动化
  - 检查点功能
  - 扩展系统
  - OpenAI API 兼容
  - 实验性 ACP 模式

### 4. [GitHub Copilot CLI](./copilot-help.md)
- **命令**: `copilot`
- **支持模型**: claude-sonnet-4.5、claude-sonnet-4、claude-haiku-4.5、gpt-5
- **特点**:
  - GitHub 集成
  - MCP 服务器支持
  - 细粒度工具权限控制
  - 会话管理
  - 屏幕阅读器优化

### 5. [Gemini CLI](./gemini-help.md)
- **命令**: `gemini`
- **默认模型**: gemini-2.5-pro
- **特点**:
  - Google AI 支持
  - 简洁的选项集
  - YOLO 模式
  - 遥测配置
  - 检查点功能

### 6. [OpenCode CLI](./opencode-help.md)
- **命令**: `opencode`
- **特点**:
  - 多模型支持（通过 models.dev 集成）
  - TUI 终端用户界面
  - MCP 服务器支持
  - Agent 系统
  - 会话管理
  - JSON 格式输出
  - 服务器模式 (serve/web)

## 快速对比

| 功能 | Claude | Codex | Qwen | Copilot | Gemini | OpenCode |
|------|--------|-------|------|---------|--------|----------|
| 交互模式 | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ (TUI) |
| 非交互模式 | ✅ (-p) | ✅ (exec) | ✅ (-p) | ✅ (-p) | ✅ (-p) | ✅ (run) |
| MCP 支持 | ✅ | ✅ | ✅ | ✅ | ❌ | ✅ |
| 沙箱模式 | ❌ | ✅ | ✅ | ❌ | ✅ | ✅ |
| 会话恢复 | ✅ | ✅ | ❌ | ✅ | ❌ | ✅ |
| 网络搜索 | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| Git 集成 | ❌ | ✅ | ❌ | ❌ | ❌ | ❌ |
| 扩展系统 | ✅ (plugin) | ❌ | ✅ | ❌ | ❌ | ✅ (agent) |
| 自定义指令 | ✅ | ❌ | ❌ | ✅ (AGENTS.md) | ❌ | ✅ (agent) |
| 服务器模式 | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |
| Web 界面 | ❌ | ❌ | ❌ | ❌ | ❌ | ✅ |

## 常用场景推荐

### 代码审查和重构
```bash
# Claude - 使用代理系统
claude --agents '{"reviewer": {"description": "Reviews code", "prompt": "You are a code reviewer"}}'

# Codex - 使用完整自动模式
codex --full-auto "Review and refactor this code"

# Copilot - 使用特定模型
copilot --model gpt-5 -p "Review this code for best practices"
```

### 非交互式批处理
```bash
# Claude - JSON 输出
claude --print --output-format json "Analyze this file" > output.json

# Codex - 执行模式
codex exec "Generate unit tests for all functions"

# Qwen - 简单提示
qwen -p "Add documentation to all functions"

# OpenCode - run 命令
opencode run "Add comprehensive tests" --format json
```

### 自动化脚本
```bash
# Qwen - YOLO 模式
qwen -y -p "Optimize all database queries"

# Codex - 完全自动
codex --full-auto "Update all dependencies"

# Copilot - 允许所有工具
copilot --allow-all-tools -p "Run linter and fix all issues"
```

### 会话管理
```bash
# Claude - 继续最近会话
claude --continue

# Claude - 恢复并分叉
claude --resume --fork-session

# Copilot - 恢复会话
copilot --resume

# Codex - 恢复上一个会话
codex resume --last

# WebCode 约束：恢复 thread 时不要额外补发消息
codex exec resume --json <thread_id>

# OpenCode - 继续上次会话
opencode --continue
opencode -c

# OpenCode - 恢复指定会话
opencode --session <session_id>
opencode run -s <session_id> "继续任务"
```

## 安装位置
所有 CLI 工具应该已安装在系统 PATH 中。如果遇到问题，请检查：
- Windows: 环境变量 PATH 设置
- 工具安装目录
- npm 全局包路径（如适用）

## 配置文件位置

- **Claude**: `~/.claude/` 或项目根目录
- **Codex**: `~/.codex/config.toml`
- **Qwen**: 项目配置文件
- **Copilot**: `~/.copilot/`
- **Gemini**: 项目配置文件
- **OpenCode**: `~/.local/share/opencode/` (数据和会话), `~/.opencode/` (配置)

## 更新日期
2026年1月17日

## 相关资源
- 各工具的官方文档
- GitHub 仓库
- 社区论坛和讨论
