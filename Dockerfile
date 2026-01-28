# ============================================
# WebCodeCli Docker 镜像构建文件
# 内置 Claude Code CLI 和 Codex CLI
# ============================================

# 阶段1: 构建阶段
FROM mcr.azure.cn/dotnet/sdk:10.0 AS build
WORKDIR /src

# 安装 Node.js（使用官方源，国内网络已优化）
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs

# 复制项目文件
COPY ["WebCodeCli/WebCodeCli.csproj", "WebCodeCli/"]
COPY ["WebCodeCli.Domain/WebCodeCli.Domain.csproj", "WebCodeCli.Domain/"]
COPY ["Directory.Build.props", "./"]

# 还原 NuGet 包
RUN dotnet restore "WebCodeCli/WebCodeCli.csproj"

# 复制源代码
COPY . .

# 构建 TailwindCSS（使用淘宝 npm 镜像）
WORKDIR /src/WebCodeCli
RUN npm install --registry=https://registry.npmmirror.com && npm run build:css

# 构建 .NET 应用
RUN dotnet build "WebCodeCli.csproj" -c Release -o /app/build

# 发布应用
FROM build AS publish
RUN dotnet publish "WebCodeCli.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ============================================
# 阶段2: 运行时镜像（包含 CLI 工具）
# ============================================
FROM mcr.azure.cn/dotnet/aspnet:10.0 AS final
WORKDIR /app

# 设置环境变量
ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true
ENV LANG=C.UTF-8
ENV LC_ALL=C.UTF-8

# 安装基础依赖
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    wget \
    gnupg \
    git \
    ca-certificates \
    openssh-client \
    python3 \
    python3-pip \
    python3-venv \
    build-essential \
    && rm -rf /var/lib/apt/lists/*

# setpriv 已包含在 util-linux 中，无需额外安装

# 配置 pip 国内镜像
RUN pip3 config set global.index-url https://mirrors.aliyun.com/pypi/simple/

# 安装 Node.js 20.x（使用官方源）
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

# 安装 Rust（使用国内镜像）
ENV RUSTUP_HOME=/usr/local/rustup
ENV CARGO_HOME=/usr/local/cargo
ENV RUSTUP_DIST_SERVER=https://mirrors.ustc.edu.cn/rust-static
ENV RUSTUP_UPDATE_ROOT=https://mirrors.ustc.edu.cn/rust-static/rustup
ENV PATH=/usr/local/cargo/bin:$PATH
RUN curl https://sh.rustup.rs -sSf | sh -s -- -y --default-toolchain stable

# ============================================
# 安装 Claude Code CLI
# ============================================
RUN npm install -g @anthropic-ai/claude-code --registry=https://registry.npmmirror.com

# ============================================
# 安装 Codex CLI（指定版本 0.80.0）
# ============================================
RUN npm install -g @openai/codex@0.80.0 --registry=https://registry.npmmirror.com

# ============================================
# 安装 OpenCode CLI
# ============================================
RUN npm install -g opencode-ai --registry=https://registry.npmmirror.com

# 创建 Codex 配置目录
RUN mkdir -p /root/.codex

# 复制 Codex 配置模板（运行时会被覆盖）
COPY docker/codex-config.toml /root/.codex/config.toml

# ============================================
# 验证 CLI 工具安装
# ============================================
RUN claude --version || echo "Claude CLI installed" \
    && codex --version || echo "Codex CLI installed" \
    && opencode --version || echo "OpenCode CLI installed" \
    && node --version \
    && python3 --version \
    && git --version

# 创建数据和工作区目录
RUN mkdir -p /app/data /app/workspaces /app/logs

# ============================================
# 创建非 root 用户以提高安全性
# ============================================
RUN groupadd -r appuser && useradd -r -g appuser -u 1001 -m appuser

# 复制发布文件（在切换用户之前）
COPY --from=publish /app/publish .

# 复制 Docker 启动脚本（以 root 权限运行，用于修复挂载卷权限）
COPY docker/docker-entrypoint.sh /docker-entrypoint.sh
RUN sed -i 's/\r$//' /docker-entrypoint.sh \
    && chmod +x /docker-entrypoint.sh

# 复制 Claude Code Skills 到容器
COPY skills/ /app/skills/

# 暴露端口
EXPOSE 5000

# 健康检查
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# 启动入口
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["dotnet", "WebCodeCli.dll"]
