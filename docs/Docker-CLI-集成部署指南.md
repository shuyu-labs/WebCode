# WebCodeCli Docker 部署完整指南

本文档详细说明如何使用 Docker 部署 WebCodeCli，包含内置的 Claude Code CLI 和 Codex CLI。

## 目录

- [前置要求](#前置要求)
- [快速开始](#快速开始)
- [环境变量配置](#环境变量配置)
- [Claude Code 配置详解](#claude-code-配置详解)
- [Codex 配置详解](#codex-配置详解)
- [构建和部署](#构建和部署)
- [验证安装](#验证安装)
- [高级配置](#高级配置)
- [故障排查](#故障排查)
- [常用命令](#常用命令)

---

## 前置要求

### 服务器要求

- **操作系统**: Linux (推荐 Ubuntu 22.04+) / Windows Server 2019+
- **Docker**: 20.10+
- **Docker Compose**: v2.0+
- **内存**: 最低 2GB，推荐 4GB+
- **磁盘**: 最低 10GB 可用空间

### 安装 Docker

**Ubuntu/Debian:**
```bash
# 安装 Docker
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh

# 安装 Docker Compose
sudo apt-get install docker-compose-plugin

# 将当前用户添加到 docker 组（需重新登录生效）
sudo usermod -aG docker $USER
```

**CentOS/RHEL:**
```bash
sudo yum install -y yum-utils
sudo yum-config-manager --add-repo https://download.docker.com/linux/centos/docker-ce.repo
sudo yum install docker-ce docker-ce-cli containerd.io docker-compose-plugin
sudo systemctl start docker
sudo systemctl enable docker
```

---

## 快速开始

### 1. 克隆项目

```bash
git clone <your-repo-url> webcodecli
cd webcodecli
```

### 2. 配置环境变量

```bash
# 复制环境变量模板
cp .env.example .env

# 编辑 .env 文件，填入你的 API 密钥
nano .env  # 或使用 vim、vi
```

**必须配置的环境变量：**

```bash
# Claude Code 认证令牌
ANTHROPIC_AUTH_TOKEN=your_anthropic_auth_token_here

# Codex API 密钥
NEW_API_KEY=your_codex_api_key_here
```

### 3. 构建并启动

```bash
# 构建镜像（首次需要较长时间）
docker compose build

# 启动服务（后台运行）
docker compose up -d

# 查看日志
docker compose logs -f
```

### 4. 访问应用

浏览器打开: http://your-server-ip:5000

---

## 环境变量配置

### 完整环境变量列表

| 变量名 | 必填 | 默认值 | 说明 |
|--------|------|--------|------|
| **Claude Code 配置** ||||
| `ANTHROPIC_BASE_URL` | 否 | `https://api.antsk.cn/` | Anthropic API 基础 URL |
| `ANTHROPIC_AUTH_TOKEN` | **是** | - | Anthropic 认证令牌 |
| `ANTHROPIC_MODEL` | 否 | - | 指定使用的模型 |
| `ANTHROPIC_SMALL_FAST_MODEL` | 否 | - | 小型快速模型 |
| **Codex 配置** ||||
| `NEW_API_KEY` | **是** | - | Codex API 密钥 |
| `CODEX_MODEL` | 否 | `glm-4.7` | Codex 使用的模型 |
| `CODEX_MODEL_REASONING_EFFORT` | 否 | `medium` | 推理强度 (low/medium/high) |
| `CODEX_PROFILE` | 否 | `ipsa` | 配置文件名称 |
| `CODEX_BASE_URL` | 否 | `https://api.antsk.cn/v1` | Codex API 基础 URL |
| `CODEX_PROVIDER_NAME` | 否 | `azure codex-mini` | 提供商名称 |
| `CODEX_APPROVAL_POLICY` | 否 | `never` | 审批策略 (never/suggest/always) |
| `CODEX_SANDBOX_MODE` | 否 | `danger-full-access` | 沙箱模式 |
| **应用配置** ||||
| `APP_PORT` | 否 | `5000` | 应用暴露端口 |
| `DB_TYPE` | 否 | `Sqlite` | 数据库类型 |
| `DB_CONNECTION` | 否 | `Data Source=/app/data/webcodecli.db` | 数据库连接字符串 |

---

## Claude Code 配置详解

Claude Code CLI 通过环境变量进行配置，容器启动时自动读取。

### 环境变量说明

```bash
# API 基础地址（使用自定义代理）
ANTHROPIC_BASE_URL="https://api.antsk.cn/"

# 认证令牌（必填）
ANTHROPIC_AUTH_TOKEN="your_token_here"

# 主模型（可选，留空使用默认）
ANTHROPIC_MODEL="claude-sonnet-4-20250514"

# 小型快速模型（可选）
ANTHROPIC_SMALL_FAST_MODEL="claude-haiku"
```

### 在 .env 文件中配置

```bash
ANTHROPIC_BASE_URL=https://api.antsk.cn/
ANTHROPIC_AUTH_TOKEN=sk-xxxxxxxxxxxx
ANTHROPIC_MODEL=
ANTHROPIC_SMALL_FAST_MODEL=
```

---

## Codex 配置详解

Codex CLI 需要配置 `~/.codex/config.toml` 文件。Docker 容器启动时会根据环境变量自动生成此配置文件。

### 自动生成的配置文件格式

容器启动脚本会根据环境变量生成如下配置：

```toml
# Codex CLI 配置文件

model = "glm-4.7"
model_reasoning_effort = "medium"

profile = "ipsa"
windows_wsl_setup_acknowledged = true

[model_providers.ipsa]
name = "azure codex-mini"
base_url = "https://api.antsk.cn/v1"
env_key = "NEW_API_KEY"
wire_api = "chat"


[profiles.ipsa]
# 深度模型
model = "glm-4.7"
# provider id
model_provider = "ipsa"
# 审批策略
approval_policy = "never"
# 推理强度
model_reasoning_effort = "medium"
# 推理总结粒度
model_reasoning_summary = "detailed"
# 是否强制开启推理总结
model_supports_reasoning_summaries = true
model_reasoning_summary_format = "experimental"
sandbox_mode = "danger-full-access"
```

### 配置参数说明

| 参数 | 环境变量 | 说明 |
|------|----------|------|
| `model` | `CODEX_MODEL` | 使用的模型名称 |
| `model_reasoning_effort` | `CODEX_MODEL_REASONING_EFFORT` | 推理强度：low/medium/high |
| `profile` | `CODEX_PROFILE` | 使用的配置文件名 |
| `base_url` | `CODEX_BASE_URL` | API 基础 URL |
| `env_key` | 固定为 `NEW_API_KEY` | 读取 API 密钥的环境变量名 |
| `approval_policy` | `CODEX_APPROVAL_POLICY` | 审批策略 |
| `sandbox_mode` | `CODEX_SANDBOX_MODE` | 沙箱模式 |

---

## 构建和部署

### 方式一：使用 Docker Compose（推荐）

```bash
# 构建镜像
docker compose build

# 启动服务
docker compose up -d

# 查看状态
docker compose ps

# 查看日志
docker compose logs -f webcodecli
```

### 方式二：单独使用 Docker

```bash
# 构建镜像
docker build -t webcodecli:latest .

# 运行容器
docker run -d \
  --name webcodecli \
  -p 5000:5000 \
  -e ANTHROPIC_BASE_URL="https://api.antsk.cn/" \
  -e ANTHROPIC_AUTH_TOKEN="your_token" \
  -e NEW_API_KEY="your_codex_key" \
  -e CODEX_MODEL="glm-4.7" \
  -v webcodecli-data:/app/data \
  -v webcodecli-workspaces:/app/workspaces \
  webcodecli:latest
```

### 生产环境部署

```bash
# 拉取代码
git pull origin main

# 停止旧容器
docker compose down

# 重新构建（使用缓存）
docker compose build

# 启动新容器
docker compose up -d

# 验证部署
docker compose ps
docker compose logs --tail=100 webcodecli
```

---

## 验证安装

### 检查容器状态

```bash
# 查看容器运行状态
docker compose ps

# 应该看到类似输出：
# NAME          IMAGE               STATUS          PORTS
# webcodecli    webcodecli:latest   Up 2 minutes    0.0.0.0:5000->5000/tcp
```

### 验证 CLI 工具

```bash
# 进入容器
docker compose exec webcodecli /bin/bash

# 检查 Claude CLI
claude --version

# 检查 Codex CLI
codex --version

# 查看 Codex 配置
cat /root/.codex/config.toml

# 检查环境变量
echo $ANTHROPIC_BASE_URL
echo $NEW_API_KEY

# 退出容器
exit
```

### 检查健康状态

```bash
# 访问健康检查端点
curl http://localhost:5000/health
```

---

## 高级配置

### 使用自定义配置文件

如果需要覆盖默认的 `appsettings.json`：

```bash
# 1. 创建配置目录
mkdir -p ./config

# 2. 复制配置文件
cp WebCodeCli/appsettings.json ./config/appsettings.Production.json

# 3. 编辑配置
nano ./config/appsettings.Production.json

# 4. 修改 docker-compose.yml，取消挂载注释
# volumes:
#   - ./config/appsettings.Production.json:/app/appsettings.Production.json:ro
```

### 使用 PostgreSQL 数据库

```yaml
# docker-compose.yml 添加 PostgreSQL 服务
services:
  postgres:
    image: postgres:15
    environment:
      POSTGRES_DB: webcodecli
      POSTGRES_USER: webcodecli
      POSTGRES_PASSWORD: your_secure_password
    volumes:
      - postgres-data:/var/lib/postgresql/data
    networks:
      - webcodecli-network

  webcodecli:
    # ... 现有配置 ...
    environment:
      - DB_TYPE=PostgreSQL
      - DB_CONNECTION=Host=postgres;Username=webcodecli;Password=your_secure_password;Database=webcodecli
    depends_on:
      - postgres

volumes:
  postgres-data:
```

### 配置 HTTPS（使用 Nginx 反向代理）

```bash
# 创建 nginx 配置
cat > ./nginx.conf << 'EOF'
server {
    listen 80;
    server_name your-domain.com;
    return 301 https://$server_name$request_uri;
}

server {
    listen 443 ssl http2;
    server_name your-domain.com;

    ssl_certificate /etc/nginx/ssl/fullchain.pem;
    ssl_certificate_key /etc/nginx/ssl/privkey.pem;

    location / {
        proxy_pass http://webcodecli:5000;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_read_timeout 3600s;
        proxy_send_timeout 3600s;
    }
}
EOF
```

### 资源限制配置

在 `docker-compose.yml` 中配置资源限制：

```yaml
services:
  webcodecli:
    deploy:
      resources:
        limits:
          cpus: '4'
          memory: 4G
        reservations:
          cpus: '1'
          memory: 1G
```

---

## 故障排查

### 1. 容器无法启动

```bash
# 查看详细日志
docker compose logs webcodecli

# 查看容器状态
docker compose ps -a

# 检查端口占用
netstat -tlnp | grep 5000
```

### 2. Claude Code 无法使用

```bash
# 进入容器检查
docker compose exec webcodecli /bin/bash

# 检查 Claude CLI
claude --version

# 检查环境变量
echo $ANTHROPIC_BASE_URL
echo $ANTHROPIC_AUTH_TOKEN

# 测试 Claude CLI
claude --help
```

### 3. Codex 无法使用

```bash
# 进入容器检查
docker compose exec webcodecli /bin/bash

# 检查 Codex CLI
codex --version

# 检查配置文件
cat /root/.codex/config.toml

# 检查 API 密钥
echo $NEW_API_KEY

# 测试 Codex CLI
codex --help
```

### 4. 健康检查失败

```bash
# 检查应用日志
docker compose logs --tail=100 webcodecli

# 手动测试健康端点
docker compose exec webcodecli curl -f http://localhost:5000/health
```

### 5. 数据库连接失败

```bash
# 检查数据目录权限
docker compose exec webcodecli ls -la /app/data

# 检查数据库文件
docker compose exec webcodecli cat /app/data/webcodecli.db
```

---

## 常用命令

### 容器管理

```bash
# 启动服务
docker compose up -d

# 停止服务
docker compose stop

# 重启服务
docker compose restart

# 停止并删除容器
docker compose down

# 停止并删除容器和卷（危险：会删除数据）
docker compose down -v
```

### 日志管理

```bash
# 查看实时日志
docker compose logs -f

# 查看最近 100 行日志
docker compose logs --tail=100

# 查看特定时间段日志
docker compose logs --since="2024-01-01" --until="2024-01-02"
```

### 维护操作

```bash
# 进入容器 Shell
docker compose exec webcodecli /bin/bash

# 重新构建镜像（使用缓存）
docker compose build

# 重新构建镜像（不使用缓存）
docker compose build --no-cache

# 拉取最新代码并重新部署
git pull && docker compose up -d --build
```

### 备份和恢复

```bash
# 备份数据卷
docker run --rm -v webcodecli-data:/data -v $(pwd):/backup alpine tar czf /backup/data-backup.tar.gz -C /data .

# 恢复数据卷
docker run --rm -v webcodecli-data:/data -v $(pwd):/backup alpine tar xzf /backup/data-backup.tar.gz -C /data
```

---

## 文件结构

```
webcodecli/
├── Dockerfile              # Docker 镜像构建文件
├── docker-compose.yml      # Docker Compose 配置
├── .env.example            # 环境变量模板
├── .env                    # 环境变量配置（需自行创建）
└── docker/
    ├── docker-entrypoint.sh   # 容器启动脚本
    └── codex-config.toml      # Codex 配置模板
```

---

## 更新日志

- **v1.0** - 初始版本，支持 Claude Code CLI 和 Codex CLI 内置部署
