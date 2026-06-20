import { test, expect } from '@playwright/test';

test.describe.configure({ timeout: 120000 });

test.describe('统一工作区管理功能测试', () => {
  // 测试配置
  const BASE_URL = 'http://localhost:5000';
  const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';
  const SCREEN_SIZE = { width: 1200, height: 800 };
  const TEST_DIRECTORY = 'D:\\\\VSWorkshop\\\\TestWebCode'; // 测试用的工作目录

  test.beforeEach(async ({ page }) => {
    // 设置窗口大小
    await page.setViewportSize(SCREEN_SIZE);

    // 登录流程
    await page.goto(`${BASE_URL}/login`);
    await page.waitForLoadState('networkidle');
    // 等待本地化加载完成
    await page.waitForTimeout(5000);

    // 尝试用中文或英文placeholder定位输入框
    const usernameInput = page.getByRole('textbox').first();
    const passwordInput = page.getByRole('textbox').nth(1);
    const loginButton = page.getByRole('button').first();

    await usernameInput.fill('luhaiyan');
    await passwordInput.fill(PASSWORD);
    await loginButton.click();

    // 等待登录请求完成
    await page.waitForLoadState('networkidle', { timeout: 10000 });

    // 直接导航到代码助手页面
    await page.goto(`${BASE_URL}/code-assistant`);

    // 等待页面完全加载
    await page.waitForLoadState('networkidle', { timeout: 30000 });
  });

  test('测试页面加载和基本元素显示', async ({ page }) => {
    // 检查页面标题
    await expect(page).toHaveTitle(/WebCode/, { timeout: 30000 });
    console.log('页面标题验证通过');

    // 检查工作区切换器显示（兼容默认文本和路径显示）
    const workspaceSwitcher = page.locator('button').filter({ hasText: /默认工作区|[:/\\]/ }).first();
    await expect(workspaceSwitcher).toBeVisible({ timeout: 30000 });
    console.log('工作区切换器验证通过');

    // 检查左侧活动栏会话按钮
    await expect(page.getByRole('button', { name: 'sessions' })).toBeVisible({ timeout: 30000 });
    console.log('会话按钮验证通过');

    // 检查聊天输入框
    await expect(page.getByRole('textbox', { name: 'inputPlaceholder' })).toBeVisible({ timeout: 30000 });
    console.log('输入框验证通过');
  });

  /*
  test('测试新建会话弹窗功能', async ({ page }) => {
    // 点击活动栏的会话按钮
    await page.getByRole('button', { name: 'sessions' }).click();
    // 等待侧边栏打开
    await page.waitForSelector('.session-list-panel', { state: 'visible', timeout: 15000 });

    // 点击新建会话按钮
    await page.getByRole('button', { name: '新建会话' }).click();
    // 检查弹窗是否出现
    await expect(page.getByText('新建会话').first()).toBeVisible({ timeout: 10000 });

    // 检查两个Tab是否存在
    await expect(page.getByRole('tab', { name: '选择已有目录' })).toBeVisible();
    await expect(page.getByRole('tab', { name: '自定义路径' })).toBeVisible();

    // 切换到自定义路径Tab
    await page.getByRole('tab', { name: '自定义路径' }).click();
    await expect(page.getByText('使用默认目录')).toBeVisible();
    await expect(page.getByText('自定义工作路径')).toBeVisible();

    // 测试自定义路径输入
    await page.getByText('自定义工作路径').click();
    const pathInput = page.getByPlaceholder('工作目录路径');
    await expect(pathInput).toBeVisible();
    await pathInput.fill(TEST_DIRECTORY);

    // 关闭弹窗
    await page.getByRole('button', { name: 'Close' }).click();
    await expect(page.getByText('新建会话').first()).toBeHidden({ timeout: 5000 });
  });

  test('测试工作区切换器功能', async ({ page }) => {
    // 点击工作区切换器
    await page.getByRole('button', { name: '默认工作区' }).click();
    // 检查下拉菜单是否出现
    await expect(page.getByText('切换工作目录')).toBeVisible({ timeout: 10000 });
    // 检查搜索框是否存在
    const searchInput = page.getByPlaceholder('搜索目录...');
    await expect(searchInput).toBeVisible();

    // 测试搜索功能
    await searchInput.fill('测试');
    // 关闭下拉菜单
    await page.click('body', { position: { x: 10, y: 10 } });
    await expect(page.getByText('切换工作目录')).toBeHidden({ timeout: 5000 });
  });

  test('测试会话列表功能', async ({ page }) => {
    // 打开会话侧边栏
    await page.getByRole('button', { name: 'sessions' }).click();
    // 检查会话列表是否显示
    await expect(page.locator('.session-list-panel')).toBeVisible({ timeout: 15000 });

    // 检查新建会话按钮
    const newSessionBtn = page.getByRole('button', { name: '新建会话' });
    await expect(newSessionBtn).toBeVisible();
  });

  test('测试使用自定义目录创建会话', async ({ page }) => {
    // 点击新建会话
    await page.getByRole('button', { name: 'sessions' }).click();
    await page.getByRole('button', { name: '新建会话' }).click();

    // 切换到自定义路径
    await page.getByRole('tab', { name: '自定义路径' }).click();
    await page.getByText('自定义工作路径').click();

    // 输入测试目录
    const pathInput = page.getByPlaceholder('工作目录路径');
    await pathInput.fill(TEST_DIRECTORY);

    // 点击创建会话
    await page.getByRole('button', { name: '创建会话' }).click();

    // 等待会话创建完成
    await page.waitForLoadState('networkidle', { timeout: 20000 });

    // 检查工作区切换器是否显示新目录
    const dirName = TEST_DIRECTORY.split('\\\\').pop() || '';
    const workspaceSwitcher = page.getByRole('button', { name: dirName }).first();
    await expect(workspaceSwitcher).toBeVisible({ timeout: 10000 });
  });
  */

  /*
  test('测试目录权限验证', async ({ page, request }) => {
    // 测试API权限验证
    const response = await request.post(`${BASE_URL}/api/workspace/authorize`, {
      data: {
        DirectoryPath: 'C:\\\\Windows', // 敏感系统目录
        AuthorizedUsername: 'test',
        Permission: 'read'
      }
    });

    // 敏感目录应该返回403禁止访问
    expect([403, 400]).toContain(response.status());
  });
  */

  test('测试工作区文件访问API', async ({ page, request }) => {
    // 首先创建一个测试会话
    const createResponse = await request.post(`${BASE_URL}/api/session`, {
      data: {
        SessionId: 'test-session-' + Date.now(),
        Title: '测试会话',
        WorkspacePath: TEST_DIRECTORY,
        IsCustomWorkspace: true
      }
    });

    expect(createResponse.ok()).toBeTruthy();
    const sessionId = (await createResponse.json()).SessionId;

    // 测试文件访问API
    const fileResponse = await request.get(`${BASE_URL}/api/workspace/${sessionId}/files/test.txt`);
    // 文件不存在应该返回404
    expect(fileResponse.status()).toBe(404);
  });
});
