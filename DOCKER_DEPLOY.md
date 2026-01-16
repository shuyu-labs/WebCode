# Docker 环境部署文档

## 1. 安装 Docker

```bash
apt update
apt install -y docker.io docker-compose
systemctl start docker
systemctl enable docker
```

## 2. 配置 Docker 国内镜像加速器

```bash
mkdir -p /etc/docker
cat > /etc/docker/daemon.json << 'EOF'
{
  "registry-mirrors": [
    "https://docker.mirrors.ustc.edu.cn",
    "https://hub-mirror.c.163.com",
    "https://mirror.ccs.tencentyun.com"
  ]
}
EOF

systemctl daemon-reload
systemctl restart docker
```

## 3. 修改 docker-compose.yml

**问题**：`network_mode: host` 与 `ports` 冲突，且 CPU 限制超过实际可用数。

**修改**：
- 移除 `ports` 配置
- CPU 限制从 4 调整为 2

```yaml
services:
  webcodecli:
    # ... 其他配置保持不变
    network_mode: host
    # 移除了 ports 配置

    deploy:
      resources:
        limits:
          cpus: '2'      # 从 '4' 改为 '2'
          memory: 2G     # 从 4G 改为 2G
        reservations:
          cpus: '0.5'
          memory: 512M   # 从 1G 改为 512M
```

## 4. 修改 docker-entrypoint.sh

**问题**：`set -e` 导致 chown 失败时整个脚本退出，容器不断重启。

**修改位置**：`/soft/WebCode/docker/docker-entrypoint.sh` 第 120 行

```bash
# 修改前：
chown -R appuser:appuser /app/data /app/workspaces /app/logs

# 修改后：
chown -R appuser:appuser /app/data /app/workspaces /app/logs 2>/dev/null || echo "Note: Could not change ownership (mounted volumes)"
```

## 5. 修复宿主机数据目录权限

**问题**：容器内 appuser (UID 1001) 无法写入挂载的目录。

```bash
chown -R 1001:1001 /soft/WebCode/webcodecli-data
chown -R 1001:1001 /soft/WebCode/webcodecli-logs
chown -R 1001:1001 /soft/WebCode/webcodecli-workspaces
```

## 6. 构建并启动

```bash
cd /soft/WebCode
docker-compose down
docker-compose up -d --build
```

## 7. 验证

```bash
# 检查容器状态
docker ps --filter name=webcodecli

# 检查数据库挂载
ls -la /soft/WebCode/webcodecli-data/

# 访问应用
curl http://localhost:5000
```

