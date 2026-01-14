# ============================================
# WebCodeCli Docker 镜像构建文件
# 内置 Claude Code CLI 和 Codex CLI
# ============================================

# 阶段1: 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 安装 Node.js（用于 TailwindCSS 构建）
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

# 构建 TailwindCSS
WORKDIR /src/WebCodeCli
RUN npm install && npm run build:css

# 构建 .NET 应用
RUN dotnet build "WebCodeCli.csproj" -c Release -o /app/build

# 发布应用
FROM build AS publish
RUN dotnet publish "WebCodeCli.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ============================================
# 阶段2: 运行时镜像（包含 CLI 工具）
# ============================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
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

# 安装 Node.js 20.x
RUN curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
    && apt-get install -y nodejs \
    && rm -rf /var/lib/apt/lists/*

# 安装 Rust（Codex 需要）
ENV RUSTUP_HOME=/usr/local/rustup
ENV CARGO_HOME=/usr/local/cargo
ENV PATH=/usr/local/cargo/bin:$PATH
RUN curl https://sh.rustup.rs -sSf | sh -s -- -y --default-toolchain stable

# ============================================
# 安装 Claude Code CLI
# ============================================
RUN npm install -g @anthropic-ai/claude-code

# ============================================
# 安装 Codex CLI
# ============================================
# 方式1: 使用 npm 安装（推荐）
RUN npm install -g @openai/codex

# 创建 Codex 配置目录
RUN mkdir -p /root/.codex

# 复制 Codex 配置模板（运行时会被覆盖）
COPY docker/codex-config.toml /root/.codex/config.toml

# ============================================
# 验证 CLI 工具安装
# ============================================
RUN claude --version || echo "Claude CLI installed" \
    && codex --version || echo "Codex CLI installed" \
    && node --version \
    && python3 --version \
    && git --version

# 创建数据和工作区目录
RUN mkdir -p /app/data /app/workspaces /app/logs

# 复制发布文件
COPY --from=publish /app/publish .

# 复制 Docker 启动脚本
COPY docker/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh

# ============================================
# 创建非 root 用户以提高安全性
# ============================================
RUN groupadd -r appuser && useradd -r -g appuser -u 1000 appuser \
    && chown -R appuser:appuser /app

# 切换到非 root 用户
USER appuser

# 暴露端口
EXPOSE 5000

# 健康检查
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD curl -f http://localhost:5000/health || exit 1

# 启动入口
ENTRYPOINT ["/docker-entrypoint.sh"]
CMD ["dotnet", "WebCodeCli.dll"]
