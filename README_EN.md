# WebCode

<p align="center">
  <a href="README.md">简体中文</a> | <a href="README_EN.md">English</a>
</p>

<p align="center">
  <strong>One control plane for AI CLIs, web sessions, mobile usage, and Feishu workflows</strong>
</p>

<p align="center">
  Manage Claude Code, Codex, and OpenCode sessions, workspaces, projects, and command execution from the browser or Feishu cards.
</p>

---

## Overview

WebCode is an AI CLI workspace platform built on `Blazor Server + .NET 10`. It is not just a chat shell. The goal is to turn local or server-side AI CLIs into a manageable, deployable, collaborative, remotely accessible working system.

The repository already covers these common scenarios:

- create, switch, close, and resume AI sessions from the web UI
- bind an isolated workspace or project directory to each session
- import existing local `Claude Code`, `Codex`, and `OpenCode` sessions from the current OS account
- restore history from original CLI transcripts instead of relying only on WebCode-owned chat records
- use the same sessions from desktop web, mobile web, and Feishu cards
- configure multi-user permissions, tool access, workspace allowlists, and Feishu bots
- treat `cc-switch` as the only provider authority for managed assistants

If you need a deployable control plane for AI CLIs rather than a single-user wrapper, this repository is designed for that job.

## Current Highlights

### Multiple AI CLI integrations

The project already supports:

| Tool | Notes | Status |
|------|-------|--------|
| `Claude Code` | Session management, streaming output, transcript recovery | Available |
| `Codex CLI` | JSONL output, sandbox/approval modes, session execution | Available |
| `OpenCode` | Multi-model workflows, session import, execution | Available |
| Custom CLIs | Can be added with the adapter pattern | Extensible |

The relevant implementations live mainly under [WebCodeCli.Domain/Domain/Service/Adapters](./WebCodeCli.Domain/Domain/Service/Adapters).

### Shared session flow across web, mobile, and Feishu

- each session can use its own workspace
- default directories, project directories, existing folders, and custom paths are supported
- file browse, preview, upload, and copy-path flows are built in
- session history, switching, closing, isolation, and cleanup are supported
- existing local CLI sessions can be imported and continued inside WebCode
- mobile session drawers and Feishu session cards can manage the same session set
- supports attachment-aware messages across desktop Web, mobile, and Feishu, with native CLI attachment input when available and workspace-reference fallback otherwise
- supports top-level Feishu `image/file` messages through a staged "submit attachment" card before forwarding the final prompt to the CLI
- supports Feishu rich-text `post` messages where pasted images and text are submitted together to the CLI

#### Feishu workflow coverage

Feishu is not limited to notifications. It already supports a usable card-driven session workflow:

- bind Feishu chats to WebCode users and local/server-side CLI sessions
- create, switch, close, and continue sessions directly from Feishu cards
- browse and import external local CLI sessions from Feishu
- trigger `/goal`, `Superpowers`, and other quick actions from cards
- stream output back into Feishu cards during execution
- stage top-level image/file messages into workspace-backed attachment submission cards, then send them to the CLI with extra instructions
- send pasted inline images from Feishu rich-text `post` messages directly to the CLI together with the accompanying text

### Multi-user controls

- enable or disable users
- restrict which CLI tools each user may use
- define per-user workspace allowlists
- configure per-user Feishu bot settings
- combine shared defaults with user-specific overrides

### External session import and recovery

External session import currently follows these rules:

- it only scans sessions accessible to the current operating-system account
- it only shows sessions whose workspaces are inside allowed directories
- the same `(ToolId, CliThreadId)` pair can only be claimed by one WebCode user
- both the web UI and Feishu cards support browsing, importing, and switching to those sessions

### Windows packaging

The repository already includes a Windows packaging script that builds:

- installer `WebCode-Setup-vX.Y.Z-win-x64.exe`
- portable archive `WebCode-vX.Y.Z-win-x64-portable.zip`
- checksum file `SHA256SUMS.txt`
- release notes `RELEASE_NOTES.md`
- release package page: `https://github.com/lusile2024/WebCode/releases`

## `cc-switch` Managed Assistant Model

From the current implementation onward, `Claude Code`, `Codex`, and `OpenCode` all follow the same rules:

1. `cc-switch` is the only provider authority.
2. WebCode no longer allows manual env-var entry, provider editing, or profile switching for these managed tools.
3. Provider activation and switching must happen in `cc-switch`.
4. WebCode only reads `cc-switch` state and the live config files generated by `cc-switch`.

WebCode reads these sources:

- `~/.cc-switch/settings.json`
- `~/.cc-switch/cc-switch.db`
- the live config files written by `cc-switch`

### Session-pinned provider snapshots

Managed sessions now behave like real terminal windows:

- a new session copies the currently active provider live config on first execution
- an existing session keeps using that pinned snapshot even after `cc-switch` switches to another provider
- an old session only moves to the current `cc-switch` provider when the user explicitly clicks sync

That explicit sync entry point is available in:

- the desktop web session list
- the mobile web session drawer
- Feishu session management cards

If a pinned snapshot is missing or broken, WebCode blocks execution and asks for an explicit sync instead of silently falling back to the current machine-global live config.

### Enabling OpenCode later

`OpenCode` is managed exactly like `Claude Code` and `Codex`.

- it can be selected during initialization
- it can also be enabled later from assistant management
- actual launch readiness still depends on `cc-switch` having a valid active provider and live config for OpenCode

## Quick Start

### Option 1: Docker deployment

This is the fastest way to get started for trials, internal deployment, or a small team setup.

```bash
git clone https://github.com/lusile2024/WebCode.git
cd WebCode
docker compose up -d
```

Then open:

- `http://localhost:5000`

More deployment details:

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/Docker-CLI-集成部署指南.md](./docs/Docker-CLI-集成部署指南.md)

### Option 2: Windows release package

Use this when you want to run WebCode directly on Windows without preinstalling the `.NET SDK`.

Download:

- [GitHub Releases](https://github.com/lusile2024/WebCode/releases)
- release package page: `https://github.com/lusile2024/WebCode/releases`

The Windows release assets include:

- `WebCode-Setup-vX.Y.Z-win-x64.exe`
- `WebCode-vX.Y.Z-win-x64-portable.zip`
- `SHA256SUMS.txt`
- `RELEASE_NOTES.md`

Both the installer and portable archive use a self-contained `win-x64` runtime. The default first-launch URL is:

- `http://localhost:6021`

The application directory is prepared with:

- `data/`
- `logs/`
- `workspaces/`

The installer keeps an existing `appsettings.json` during upgrades.

### Option 3: Local development

Use this for debugging, customization, and local integration work.

#### Requirements

- `.NET 10 SDK`
- `cc-switch` correctly installed and configured for the managed assistants you want to use
- target CLIs such as `claude`, `codex`, and `opencode`

#### Start commands

```bash
git clone https://github.com/lusile2024/WebCode.git
cd WebCode
dotnet restore
dotnet run --project WebCodeCli
```

Default local URL:

- `http://localhost:6021`

If you changed the `urls` setting in [appsettings.json](./appsettings.json) or [WebCodeCli/appsettings.json](./WebCodeCli/appsettings.json), use the actual configured port.

## Recommended First-Time Setup

The current initialization flow is best completed in this order:

1. create the administrator account
2. decide whether login/authentication should be enabled
3. select which managed assistants you want WebCode to expose
4. review `cc-switch` readiness and make sure every selected assistant is launch-ready
5. verify the workspace root and storage directories
6. configure Feishu bot settings if Feishu integration is needed
7. configure users, tool permissions, and workspace allowlists if multi-user mode is needed
8. import local external CLI sessions if you want to continue existing context

The setup wizard no longer handles these tasks:

- manual env-var entry for managed assistants
- manual provider switching for managed assistants
- manual editing of managed live config files

Those actions now belong exclusively to `cc-switch`.

## Configuration Notes

### Managed assistant configuration

For `Claude Code`, `Codex`, and `OpenCode`:

- provider switching must happen in `cc-switch`
- the WebCode settings UI only shows `cc-switch` status for managed tools
- assistant management can enable or disable assistants but does not replace `cc-switch`
- session-level sync only copies the current `cc-switch` active state into a specific session snapshot

### Custom CLI configuration

For non-managed tools, you can still extend `CliTools.Tools` with your own CLI definitions. See:

- [cli/README.md](./cli/README.md)
- [docs/CLI工具配置说明.md](./docs/CLI工具配置说明.md)
- [docs/Codex配置说明.md](./docs/Codex配置说明.md)

### Docker data directories

By default, `docker-compose.yml` mounts:

- `./webcodecli-data` for database and runtime data
- `./webcodecli-workspaces` for workspace storage
- `./webcodecli-logs` for logs

### Database

The default database is SQLite:

- `WebCodeCli.db`

For local development, the default connection string comes from [appsettings.json](./appsettings.json).

## Windows Packaging Maintenance

The repository ships with this packaging script:

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-windows-installer.ps1
```

It reads the version from [Directory.Build.props](./Directory.Build.props) and writes the release bundle under `artifacts/windows-installer/vX.Y.Z/`:

- `publish/`
- `WebCode-vX.Y.Z-win-x64-portable.zip`
- `installer/WebCode-Setup-vX.Y.Z-win-x64.exe`
- `SHA256SUMS.txt`
- `RELEASE_NOTES.md`

Build machine requirements:

- Windows
- `.NET 10 SDK`
- `Inno Setup 6`

## Project Structure

```text
WebCode/
├── WebCodeCli/                # Web app (Blazor Server)
├── WebCodeCli.Domain/         # Domain services, repositories, CLI/Feishu adapters
├── WebCodeCli.Domain.Tests/   # Domain-layer tests
├── tests/WebCodeCli.Tests/    # Web and integration-related tests
├── cli/                       # CLI usage docs
├── docs/                      # Additional documentation
├── docker-compose.yml         # Docker entrypoint
└── README.md
```

The current real project names are:

- `WebCodeCli`
- `WebCodeCli.Domain`

If you still see older names such as `WebCode` or `WebCode.Domain` in legacy notes, follow the actual repository layout instead.

## Tech Stack

| Category | Technology |
|----------|------------|
| Web framework | Blazor Server |
| Runtime | .NET 10 |
| Editor | Monaco Editor |
| Data access | SqlSugar |
| Default database | SQLite |
| Reverse proxy | YARP |
| Markdown | Markdig |
| AI CLI integration | Claude Code / Codex / OpenCode |

## Useful Documentation

- [QUICKSTART.md](./QUICKSTART.md)
- [DEPLOY_DOCKER.md](./DEPLOY_DOCKER.md)
- [docs/QUICKSTART_CodeAssistant.md](./docs/QUICKSTART_CodeAssistant.md)
- [docs/README_CodeAssistant.md](./docs/README_CodeAssistant.md)
- [docs/workspace-management-guide.md](./docs/workspace-management-guide.md)
- [docs/workspace-management-deployment-guide.md](./docs/workspace-management-deployment-guide.md)

## License

This project is licensed under [AGPLv3](LICENSE).

- Open-source use: follow AGPLv3
- Commercial licensing: contact the repository maintainer or business channel

---

<p align="center">
  <strong>Turn AI CLIs from local commands into a manageable, deployable, collaborative working platform</strong>
</p>
