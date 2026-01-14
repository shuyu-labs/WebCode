#!/bin/bash
# ============================================
# WebCodeCli 快速部署脚本
# ============================================

set -e

# 颜色定义
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

echo -e "${GREEN}============================================${NC}"
echo -e "${GREEN}  WebCodeCli Docker 部署脚本${NC}"
echo -e "${GREEN}============================================${NC}"
echo ""

# 检查 Docker 是否安装
if ! command -v docker &> /dev/null; then
    echo -e "${RED}错误: Docker 未安装${NC}"
    echo "请先安装 Docker: https://docs.docker.com/get-docker/"
    exit 1
fi

# 检查 Docker Compose 是否可用
if ! docker compose version &> /dev/null; then
    echo -e "${RED}错误: Docker Compose 未安装${NC}"
    echo "请先安装 Docker Compose: https://docs.docker.com/compose/install/"
    exit 1
fi

echo -e "${GREEN}✓ Docker 已安装${NC}"
docker --version
docker compose version
echo ""

# 检查 .env 文件
if [ ! -f ".env" ]; then
    echo -e "${YELLOW}未找到 .env 文件，正在从模板创建...${NC}"
    if [ -f ".env.example" ]; then
        cp .env.example .env
        echo -e "${GREEN}✓ 已创建 .env 文件${NC}"
        echo -e "${YELLOW}请编辑 .env 文件，填入你的 API 密钥:${NC}"
        echo "  - ANTHROPIC_AUTH_TOKEN (Claude Code)"
        echo "  - NEW_API_KEY (Codex)"
        echo ""
        echo -e "${YELLOW}编辑完成后，请重新运行此脚本${NC}"
        exit 0
    else
        echo -e "${RED}错误: 未找到 .env.example 模板文件${NC}"
        exit 1
    fi
fi

echo -e "${GREEN}✓ 已找到 .env 文件${NC}"

# 检查必要的环境变量
source .env 2>/dev/null || true

if [ -z "$ANTHROPIC_AUTH_TOKEN" ] || [ "$ANTHROPIC_AUTH_TOKEN" = "your_anthropic_auth_token_here" ]; then
    echo -e "${YELLOW}警告: ANTHROPIC_AUTH_TOKEN 未配置或使用默认值${NC}"
    echo "Claude Code 可能无法正常工作"
fi

if [ -z "$NEW_API_KEY" ] || [ "$NEW_API_KEY" = "your_codex_api_key_here" ]; then
    echo -e "${YELLOW}警告: NEW_API_KEY 未配置或使用默认值${NC}"
    echo "Codex 可能无法正常工作"
fi

echo ""

# 选择操作
echo "请选择操作:"
echo "  1) 构建并启动 (首次部署)"
echo "  2) 仅启动 (已构建镜像)"
echo "  3) 重新构建并启动"
echo "  4) 停止服务"
echo "  5) 查看日志"
echo "  6) 查看状态"
echo "  7) 进入容器"
echo "  8) 完全重置 (删除数据)"
echo "  9) 清理旧镜像并重新构建"
echo ""
read -p "请输入选项 [1-9]: " choice

case $choice in
    1)
        echo -e "${GREEN}正在构建并启动...${NC}"
        docker compose build --no-cache
        docker compose up -d
        echo ""
        echo -e "${GREEN}✓ 部署完成！${NC}"
        echo "访问地址: http://localhost:${APP_PORT:-5000}"
        ;;
    2)
        echo -e "${GREEN}正在启动服务...${NC}"
        docker compose up -d
        echo ""
        echo -e "${GREEN}✓ 启动完成！${NC}"
        echo "访问地址: http://localhost:${APP_PORT:-5000}"
        ;;
    3)
        echo -e "${GREEN}正在重新构建并启动...${NC}"
        docker compose down
        docker compose build --no-cache
        docker compose up -d
        echo ""
        echo -e "${GREEN}✓ 重新部署完成！${NC}"
        echo "访问地址: http://localhost:${APP_PORT:-5000}"
        ;;
    4)
        echo -e "${YELLOW}正在停止服务...${NC}"
        docker compose down
        echo -e "${GREEN}✓ 服务已停止${NC}"
        ;;
    5)
        echo -e "${GREEN}查看日志 (Ctrl+C 退出):${NC}"
        docker compose logs -f
        ;;
    6)
        echo -e "${GREEN}服务状态:${NC}"
        docker compose ps
        ;;
    7)
        echo -e "${GREEN}进入容器...${NC}"
        docker compose exec webcodecli /bin/bash
        ;;
    8)
        echo -e "${RED}警告: 此操作将删除所有数据！${NC}"
        read -p "确认删除? (输入 'yes' 确认): " confirm
        if [ "$confirm" = "yes" ]; then
            docker compose down -v
            echo -e "${GREEN}✓ 已完全重置${NC}"
        else
            echo "操作已取消"
        fi
        ;;
    9)
        echo -e "${GREEN}正在清理旧镜像并重新构建...${NC}"
        docker compose down
        docker rmi webcodecli:latest 2>/dev/null || echo "旧镜像不存在，跳过"
        docker compose build --no-cache
        docker compose up -d
        echo ""
        echo -e "${GREEN}✓ 清理并重新部署完成！${NC}"
        echo "访问地址: http://localhost:${APP_PORT:-5000}"
        ;;
    *)
        echo -e "${RED}无效选项${NC}"
        exit 1
        ;;
esac
