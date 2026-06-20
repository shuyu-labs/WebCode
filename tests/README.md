# Playwright 测试说明

## 测试文件说明

### 1. `web-workspace-management-unified.spec.ts`
- **用途**: 测试统一工作区管理功能的核心功能
- **测试范围**:
  - 页面基本元素显示
  - 新建会话功能（选择已有目录、自定义路径、默认目录）
  - 会话列表工作区路径显示
  - 工作区切换器功能
  - 清空会话功能

### 2. `workspace-authorization.spec.ts`
- **用途**: 测试目录授权相关功能
- **测试范围**:
  - 目录授权API测试
  - 撤销授权API测试
  - 敏感目录权限验证
  - 飞书命令授权

### 3. `web-workspace-management.spec.ts`
- **用途**: 原有的工作区管理测试（已更新兼容新功能）
- **注意**: 大部分测试被注释，保留了基本验证

## 运行测试

### 方法1：使用运行脚本
```bash
node tests/run-playwright-tests.js
```

### 方法2：直接运行单个测试文件
```bash
# 运行统一工作区测试
npx playwright test tests/web-workspace-management-unified.spec.ts --headed --project=chromium --timeout=120000

# 运行授权功能测试
npx playwright test tests/workspace-authorization.spec.ts --headed --project=chromium --timeout=120000

# 运行所有测试
npx playwright test tests/ --headed --project=chromium --timeout=120000
```

### 方法3：使用Playwright CLI
```bash
# 安装Playwright（如果未安装）
npm install -D @playwright/test

# 安装浏览器
npx playwright install

# 运行测试
npx playwright test
```

## 测试配置

- **测试环境**: localhost:5000
- **用户名**: luhaiyan
- **密码**: 通过环境变量 `WEBCODE_TEST_PASSWORD` 提供；未设置时示例值为 `CHANGEME_TEST_PASSWORD`
- **测试目录**: D:\\VSWorkshop\\TestWebCode
- **超时时间**: 120秒
- **浏览器**: Chromium（带UI界面）

## 注意事项

1. **应用运行**: 运行测试前，请确保 WebCodeCli 应用正在运行
   ```bash
   dotnet run --project WebCodeCli
   ```

2. **测试目录**: `D:\\VSWorkshop\\TestWebCode` 需要是实际存在的目录

3. **超时设置**: 测试使用较长的超时时间（120秒），因为应用加载和操作可能需要时间

4. **测试隔离**: 每个测试都有独立的 `beforeEach` 钩子，会重新登录

5. **错误处理**: 测试中包含错误处理，即使某个步骤失败也会继续执行其他测试

## 测试报告

测试完成后，Playwright会生成：
- 控制台输出
- 失败时的截图
- HTML测试报告（在 `test-results` 目录）

## 调试建议

如果测试失败，可以：
1. 使用 `--headed` 参数查看浏览器界面
2. 使用 `--debug` 模式进入调试模式
3. 检查应用日志
4. 手动执行相同的测试步骤
