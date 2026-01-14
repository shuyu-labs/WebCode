# WebCode 一键部署指南

## 🚀 快速开始

只需一条命令即可完成部署：

```bash
docker-compose up -d
```

首次访问 `http://localhost:5000` 时，系统会自动引导您完成初始化配置。

---

## 📋 部署步骤

### 1. 克隆代码

```bash
git clone https://github.com/xuzeyu91/WebCode.git
cd WebCode
```

### 2. 启动服务

```bash
docker-compose up -d
```

### 3. 访问系统

打开浏览器访问：`http://localhost:5000`

### 4. 完成初始化向导

首次访问时，系统会自动跳转到初始化向导页面，您需要：

1. **设置管理员账户**
   - 用户名和密码
   - 是否启用登录认证

2. **配置 Claude Code（可选）**
   - ANTHROPIC_API_KEY
   - ANTHROPIC_BASE_URL（可选）

3. **配置 Codex（可选）**
   - NEW_API_KEY
   - CODEX_BASE_URL（可选）

---

## 🔧 高级配置

### 自定义端口

```bash
APP_PORT=8080 docker-compose up -d
```

### 使用 .env 文件

创建 `.env` 文件：

```env
APP_PORT=5000
```

然后运行：

```bash
docker-compose up -d
```

---

## 📂 数据持久化

所有数据自动持久化到 Docker 卷：

| 卷名 | 用途 |
|------|------|
| `webcodecli-data` | 数据库和配置 |
| `webcodecli-workspaces` | 工作区文件 |
| `webcodecli-logs` | 日志文件 |

---

## 🔄 常用命令

```bash
# 查看日志
docker-compose logs -f

# 重启服务
docker-compose restart

# 停止服务
docker-compose down

# 更新服务
git pull
docker-compose up -d --build
```

---

## ❓ 常见问题

### Q: 如何修改 API 配置？

登录系统后，在主界面点击「设置」按钮，可以修改环境变量配置。

### Q: 如何重置系统？

删除 Docker 卷后重新启动：

```bash
docker-compose down -v
docker-compose up -d
```

### Q: 忘记管理员密码？

删除数据卷后重新初始化：

```bash
docker-compose down
docker volume rm webcode_webcodecli-data
docker-compose up -d
```

---

## 📞 技术支持

- GitHub Issues: https://github.com/xuzeyu91/WebCode/issues
- 文档: https://github.com/xuzeyu91/WebCode
