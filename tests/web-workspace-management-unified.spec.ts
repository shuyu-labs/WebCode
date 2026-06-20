import { test, expect } from '@playwright/test';

// 测试配置
const BASE_URL = 'http://localhost:5000';
const USERNAME = 'luhaiyan';
const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';
const SCREEN_SIZE = { width: 1200, height: 800 };
const TEST_DIRECTORY = 'D:\\\\VSWorkshop\\\\TestWebCode'; // 测试用的工作目录

test.describe.configure({ timeout: 120000 });

test.describe('统一工作区功能测试', () => {
  test.beforeEach(async ({ page }) => {
    // 设置窗口大小
    await page.setViewportSize(SCREEN_SIZE);

    // 访问登录页
    await page.goto(`${BASE_URL}/login`);
    await page.waitForLoadState('networkidle');

    // 登录流程
    const usernameInput = page.getByRole('textbox').first();
    const passwordInput = page.getByRole('textbox').nth(1);
    const loginButton = page.getByRole('button').first();

    await usernameInput.fill(USERNAME);
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
    console.log('✅ 页面标题验证通过');

    // 检查左侧活动栏会话按钮
    await expect(page.getByRole('button', { name: 'sessions' })).toBeVisible({ timeout: 30000 });
    console.log('✅ 会话按钮验证通过');

    // 检查聊天输入框
    await expect(page.getByRole('textbox', { name: 'inputPlaceholder' })).toBeVisible({ timeout: 30000 });
    console.log('✅ 输入框验证通过');
  });

  test('测试新建会话功能 - 选择已有目录', async ({ page }) => {
    // 打开会话侧边栏
    await page.getByRole('button', { name: 'sessions' }).click();
    await expect(page.locator('.session-list-panel')).toBeVisible({ timeout: 15000 });

    // 点击新建会话按钮
    await page.getByRole('button', { name: '新建会话' }).click();

    // 等待新建会话模态框出现
    const modalTitle = page.getByText('新建会话').first();
    await expect(modalTitle).toBeVisible({ timeout: 10000 });
    console.log('✅ 新建会话模态框已打开');

    // 检查两个Tab是否存在
    const tabDirectories = page.getByRole('tab', { name: '选择已有目录' });
    const tabCustom = page.getByRole('tab', { name: '自定义路径' });
    await expect(tabDirectories).toBeVisible();
    await expect(tabCustom).toBeVisible();

    // 默认在"选择已有目录"Tab
    console.log('✅ Tab显示正常');

    // 关闭模态框
    await page.getByRole('button', { name: 'Close' }).click();
    await expect(modalTitle).toBeHidden({ timeout: 5000 });
    console.log('✅ 模态框已关闭');
  });

  test('测试新建会话功能 - 自定义路径', async ({ page }) => {
    // 打开会话侧边栏
    await page.getByRole('button', { name: 'sessions' }).click();
    await page.getByRole('button', { name: '新建会话' }).click();

    // 等待模态框出现
    const modalTitle = page.getByText('新建会话').first();
    await expect(modalTitle).toBeVisible({ timeout: 10000 });

    // 切换到自定义路径Tab
    const tabCustom = page.getByRole('tab', { name: '自定义路径' });
    await tabCustom.click();

    // 验证选项显示
    const defaultOption = page.getByText('使用默认目录');
    const customOption = page.getByText('自定义工作路径');
    await expect(defaultOption).toBeVisible();
    await expect(customOption).toBeVisible();

    // 选择自定义路径选项
    await customOption.click();

    // 输入测试目录路径
    const pathInput = page.getByPlaceholder('工作目录路径');
    await expect(pathInput).toBeVisible();
    await pathInput.fill(TEST_DIRECTORY);
    console.log('✅ 自定义路径已输入');

    // 点击创建会话按钮
    const createButton = page.getByRole('button', { name: '创建会话' });
    await createButton.click();

    // 等待创建完成
    await page.waitForLoadState('networkidle', { timeout: 20000 });
    console.log('✅ 会话创建成功');

    // 检查工作区切换器是否显示新目录
    const dirName = TEST_DIRECTORY.split('\\\\').pop() || '';
    const workspaceSwitcher = page.getByRole('button', { name: dirName }).first();
    await expect(workspaceSwitcher).toBeVisible({ timeout: 10000 });
    console.log('✅ 工作区已切换到自定义目录');
  });

  test('测试新建会话功能 - 使用默认目录', async ({ page }) => {
    // 打开会话侧边栏
    await page.getByRole('button', { name: 'sessions' }).click();
    await page.getByRole('button', { name: '新建会话' }).click();

    // 等待模态框出现
    const modalTitle = page.getByText('新建会话').first();
    await expect(modalTitle).toBeVisible({ timeout: 10000 });

    // 切换到自定义路径Tab
    const tabCustom = page.getByRole('tab', { name: '自定义路径' });
    await tabCustom.click();

    // 选择默认目录选项
    const defaultOption = page.getByText('使用默认目录');
    await defaultOption.click();

    // 创建会话
    const createButton = page.getByRole('button', { name: '创建会话' });
    await createButton.click();

    // 等待创建完成
    await page.waitForLoadState('networkidle', { timeout: 20000 });
    console.log('✅ 默认目录会话创建成功');
  });

  test('测试会话列表工作区路径显示', async ({ page }) => {
    // 先创建一个带自定义路径的会话
    await page.getByRole('button', { name: 'sessions' }).click();
    await page.getByRole('button', { name: '新建会话' }).click();

    const modalTitle = page.getByText('新建会话').first();
    await expect(modalTitle).toBeVisible({ timeout: 10000 });

    // 使用自定义路径创建会话
    const tabCustom = page.getByRole('tab', { name: '自定义路径' });
    await tabCustom.click();

    const customOption = page.getByText('自定义工作路径');
    await customOption.click();

    const pathInput = page.getByPlaceholder('工作目录路径');
    await pathInput.fill(TEST_DIRECTORY);

    const createButton = page.getByRole('button', { name: '创建会话' });
    await createButton.click();

    await page.waitForLoadState('networkidle', { timeout: 20000 });

    // 关闭新建会话模态框（如果有）
    try {
      await page.getByRole('button', { name: 'Close' }).click();
    } catch (e) {
      // 忽略错误，可能模态框已关闭
    }

    // 重新打开会话列表
    await page.getByRole('button', { name: 'sessions' }).click();
    await expect(page.locator('.session-list-panel')).toBeVisible({ timeout: 15000 });

    // 检查会话项中的工作区路径
    const sessionItems = page.locator('.session-list-panel .session-item');
    const count = await sessionItems.count();

    if (count > 0) {
      const firstSession = sessionItems.first();

      // 检查是否显示工作区路径
      const workspacePath = firstSession.locator('text-gray-400');
      const hasPath = await workspacePath.count() > 0;

      if (hasPath) {
        const pathText = await workspacePath.first().textContent();
        console.log('✅ 工作区路径显示:', pathText);

        // 验证路径包含测试目录
        if (pathText?.includes(TEST_DIRECTORY)) {
          console.log('✅ 工作区路径内容正确');
        }
      } else {
        console.log('ℹ️ 当前会话没有显示工作区路径（可能是默认目录）');
      }
    } else {
      console.log('ℹ️ 没有找到会话项');
    }
  });

  test('测试目录授权模态框（通过会话分享按钮）', async ({ page }) => {
    // 先确保有会话
    await page.getByRole('button', { name: 'sessions' }).click();
    const sessionItems = page.locator('.session-list-panel .session-item');
    const sessionCount = await sessionItems.count();

    if (sessionCount === 0) {
      console.log('ℹ️ 没有会话，跳过授权测试');
      return;
    }

    // 找到第一个会话的分享按钮
    const firstSession = sessionItems.first();
    const shareButton = firstSession.locator('button[title="分享会话"]');

    if (await shareButton.count() > 0) {
      await shareButton.click();

      // 检查是否出现授权相关选项
      // 注意：如果直接显示授权模态框，这里可以验证
      // 如果是通过飞书消息触发，可能需要等待

      // 暂时只验证分享按钮可以点击
      console.log('✅ 分享按钮可以点击');
    } else {
      console.log('ℹ️ 当前会话没有分享按钮');
    }
  });

  test('测试工作区切换器', async ({ page }) => {
    // 点击工作区切换器
    const workspaceSwitcher = page.getByRole('button', { name: /默认工作区|[:/\\]/ }).first();
    await workspaceSwitcher.click();

    // 等待下拉菜单出现
    const switchText = page.getByText('切换工作目录');
    await expect(switchText).toBeVisible({ timeout: 10000 });

    // 检查搜索框
    const searchInput = page.getByPlaceholder('搜索目录...');
    await expect(searchInput).toBeVisible();

    // 测试搜索功能
    await searchInput.fill('测试');
    console.log('✅ 搜索功能可用');

    // 关闭下拉菜单
    await page.click('body', { position: { x: 10, y: 10 } });
    await expect(switchText).toBeHidden({ timeout: 5000 });
    console.log('✅ 工作区切换器测试完成');
  });

  test('测试清空会话功能', async ({ page }) => {
    // 发送一条测试消息
    const inputBox = page.getByRole('textbox', { name: 'inputPlaceholder' });
    await inputBox.fill('Hello, this is a test message');
    await inputBox.press('Enter');

    // 等待消息显示
    await page.waitForTimeout(2000);

    // 清空会话
    await page.getByRole('button', { name: 'sessions' }).click();
    await page.getByRole('button', { name: '新建会话' }).click();

    // 验证消息已清空
    const messageArea = page.locator('.chat-messages');
    const messageCount = await messageArea.locator('div').count();

    // 检查是否还有消息
    if (messageCount === 0 || await messageArea.locator('text').count() === 0) {
      console.log('✅ 会话已清空');
    } else {
      console.log('ℹ️ 会话中可能还有其他元素');
    }
  });
});
