#!/bin/bash
# ============================================
# WebCodeCli Docker 启动脚本
# 动态生成 Codex 配置文件
# ============================================

set -e

echo "============================================"
echo "WebCodeCli Docker Container Starting..."
echo "============================================"

# ============================================
# 生成 Codex 配置文件
# ============================================
echo "Generating Codex configuration..."

CODEX_CONFIG_DIR="$HOME/.codex"
CODEX_CONFIG_FILE="${CODEX_CONFIG_DIR}/config.toml"

# 确保配置目录存在
mkdir -p "${CODEX_CONFIG_DIR}"

# 使用环境变量生成配置文件
cat > "${CODEX_CONFIG_FILE}" << EOF
# Codex CLI 配置文件（由 Docker 启动脚本自动生成）
# 生成时间: $(date)

model = "${CODEX_MODEL:-glm-4.7}"
model_reasoning_effort = "${CODEX_MODEL_REASONING_EFFORT:-medium}"

profile = "${CODEX_PROFILE:-ipsa}"
windows_wsl_setup_acknowledged = true

[model_providers.${CODEX_PROFILE:-ipsa}]
name = "${CODEX_PROVIDER_NAME:-azure codex-mini}"
base_url = "${CODEX_BASE_URL:-https://api.antsk.cn/v1}"
env_key = "NEW_API_KEY"
wire_api = "chat"


[profiles.${CODEX_PROFILE:-ipsa}]
# 深度模型
model = "${CODEX_MODEL:-glm-4.7}"
# provider id
model_provider = "${CODEX_PROFILE:-ipsa}"
# 审批策略
approval_policy = "${CODEX_APPROVAL_POLICY:-never}"
# 推理强度
model_reasoning_effort = "${CODEX_MODEL_REASONING_EFFORT:-medium}"
# 推理总结粒度
model_reasoning_summary = "detailed"
# 是否强制开启推理总结
model_supports_reasoning_summaries = true
model_reasoning_summary_format = "experimental"
sandbox_mode = "${CODEX_SANDBOX_MODE:-danger-full-access}"
EOF

echo "Codex configuration generated at: ${CODEX_CONFIG_FILE}"
cat "${CODEX_CONFIG_FILE}"
echo ""

# ============================================
# 验证环境变量
# ============================================
echo "Validating environment variables..."

# Claude Code 配置检查
if [ -z "${ANTHROPIC_AUTH_TOKEN}" ]; then
    echo "WARNING: ANTHROPIC_AUTH_TOKEN is not set. Claude Code may not work properly."
fi

if [ -n "${ANTHROPIC_BASE_URL}" ]; then
    echo "Claude Code Base URL: ${ANTHROPIC_BASE_URL}"
fi

if [ -n "${ANTHROPIC_MODEL}" ]; then
    echo "Claude Code Model: ${ANTHROPIC_MODEL}"
fi

# Codex 配置检查
if [ -z "${NEW_API_KEY}" ]; then
    echo "WARNING: NEW_API_KEY is not set. Codex may not work properly."
fi

echo ""

# ============================================
# 验证 CLI 工具
# ============================================
echo "Verifying CLI tools..."

echo -n "Claude CLI: "
claude --version 2>/dev/null || echo "Not available or version check failed"

echo -n "Codex CLI: "
codex --version 2>/dev/null || echo "Not available or version check failed"

echo -n "Node.js: "
node --version

echo -n "Python: "
python3 --version

echo -n "Git: "
git --version

echo ""

# ============================================
# 创建必要的目录
# ============================================
echo "Creating required directories..."
mkdir -p /app/data
mkdir -p /app/workspaces
mkdir -p /app/logs

# 设置权限
chmod -R 755 /app/data
chmod -R 755 /app/workspaces
chmod -R 755 /app/logs

echo ""

# ============================================
# 启动应用
# ============================================
echo "Starting WebCodeCli application..."
echo "============================================"

# 执行传入的命令（默认是 dotnet WebCodeCli.dll）
exec "$@"
