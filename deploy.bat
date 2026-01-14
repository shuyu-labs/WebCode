@echo off
REM ============================================
REM WebCodeCli Windows 快速部署脚本
REM ============================================

echo ============================================
echo   WebCodeCli Docker 部署脚本
echo ============================================
echo.

REM 检查 Docker 是否运行
docker --version >nul 2>&1
if %errorlevel% neq 0 (
    echo 错误: Docker 未安装或未运行
    echo 请先安装并启动 Docker Desktop
    pause
    exit /b 1
)

echo √ Docker 已安装
docker --version
echo.

REM 检查 .env 文件
if not exist ".env" (
    echo 未找到 .env 文件，正在从模板创建...
    if exist ".env.example" (
        copy .env.example .env
        echo √ 已创建 .env 文件
        echo.
        echo 请编辑 .env 文件，填入你的 API 密钥:
        echo   - ANTHROPIC_AUTH_TOKEN (Claude Code)
        echo   - NEW_API_KEY (Codex)
        echo.
        echo 编辑完成后，请重新运行此脚本
        pause
        exit /b 0
    ) else (
        echo 错误: 未找到 .env.example 模板文件
        pause
        exit /b 1
    )
)

echo √ 已找到 .env 文件
echo.

:menu
echo 请选择操作:
echo   1) 构建并启动 (首次部署)
echo   2) 仅启动 (已构建镜像)
echo   3) 重新构建并启动
echo   4) 停止服务
echo   5) 查看日志
echo   6) 查看状态
echo   7) 进入容器
echo   8) 完全重置 (删除数据)
echo   0) 退出
echo.

set /p choice=请输入选项 [0-8]: 

if "%choice%"=="1" goto build_start
if "%choice%"=="2" goto start
if "%choice%"=="3" goto rebuild
if "%choice%"=="4" goto stop
if "%choice%"=="5" goto logs
if "%choice%"=="6" goto status
if "%choice%"=="7" goto shell
if "%choice%"=="8" goto reset
if "%choice%"=="0" goto end

echo 无效选项
goto menu

:build_start
echo 正在构建并启动...
docker compose build
docker compose up -d
echo.
echo √ 部署完成！
echo 访问地址: http://localhost:5000
goto end

:start
echo 正在启动服务...
docker compose up -d
echo.
echo √ 启动完成！
echo 访问地址: http://localhost:5000
goto end

:rebuild
echo 正在重新构建并启动...
docker compose down
docker compose build --no-cache
docker compose up -d
echo.
echo √ 重新部署完成！
echo 访问地址: http://localhost:5000
goto end

:stop
echo 正在停止服务...
docker compose down
echo √ 服务已停止
goto end

:logs
echo 查看日志 (Ctrl+C 退出):
docker compose logs -f
goto end

:status
echo 服务状态:
docker compose ps
goto menu

:shell
echo 进入容器...
docker compose exec webcodecli /bin/bash
goto menu

:reset
echo 警告: 此操作将删除所有数据！
set /p confirm=确认删除? (输入 yes 确认): 
if "%confirm%"=="yes" (
    docker compose down -v
    echo √ 已完全重置
) else (
    echo 操作已取消
)
goto end

:end
echo.
pause
