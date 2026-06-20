import { test, expect } from '@playwright/test';

// ==================== 测试配置 ====================

const BASE_URL = 'http://localhost:5000';
const USERNAME = 'luhaiyan';
const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';

// 测试目录路径
const TEST_DIRECTORY = 'D:\\\\VSWorkshop\\\\TestWebCode';
const TEST_USER_2 = 'testuser2';

// 屏幕尺寸定义 - 模拟不同设备
const SCREEN_SIZES = {
  desktop: { width: 1400, height: 900, name: '桌面端 (>1200px)' },
  tablet: { width: 1024, height: 768, name: '平板端 (768-1200px)' },
  mobile: { width: 375, height: 667, name: '移动端 (<768px)' },
  mobileLarge: { width: 414, height: 896, name: '大屏移动端' }
};

// ==================== 辅助函数 ====================

/**
 * 登录流程
 */
async function performLogin(page: any) {
  await page.goto(`${BASE_URL}/login`);
  await page.waitForLoadState('networkidle');

  const usernameInput = page.getByRole('textbox').first();
  const passwordInput = page.getByRole('textbox').nth(1);
  const loginButton = page.getByRole('button').first();

  await usernameInput.fill(USERNAME);
  await passwordInput.fill(PASSWORD);
  await loginButton.click();

  await page.waitForLoadState('networkidle', { timeout: 10000 });
}

/**
 * 导航到代码助手页面
 */
async function navigateToCodeAssistant(page: any) {
  await page.goto(`${BASE_URL}/code-assistant`);
  await page.waitForLoadState('networkidle', { timeout: 30000 });
}

/**
 * 创建带工作区的测试会话
 */
async function createTestSession(page: any, sessionName: string) {
  await page.goto(`${BASE_URL}/code-assistant`);
  await page.waitForLoadState('networkidle');

  // 点击新建会话
  const newSessionBtn = page.getByRole('button', { name: /新建|创建/i });
  await newSessionBtn.click();
  await page.waitForTimeout(500);

  // 输入会话名称
  const sessionNameInput = page.getByPlaceholder('会话名称').first();
  await sessionNameInput.fill(sessionName);

  // 选择自定义路径
  const customTab = page.getByRole('tab', { name: '自定义路径' });
  await customTab.click();

  // 输入目录路径
  const pathInput = page.getByPlaceholder('工作目录路径');
  await pathInput.fill(TEST_DIRECTORY);

  // 创建会话
  const createBtn = page.getByRole('button', { name: /创建/i });
  await createBtn.click();

  await page.waitForLoadState('networkidle', { timeout: 10000 });
}

/**
 * 删除测试会话
 */
async function deleteTestSession(page: any, sessionName: string) {
  // 刷新页面
  await page.reload();
  await page.waitForLoadState('networkidle');

  // 打开会话列表
  const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
  await sessionsBtn.click();
  await page.waitForTimeout(500);

  // 找到并删除测试会话
  const sessionItem = page.getByText(sessionName).first();
  await sessionItem.hover();

  const deleteBtn = page.getByRole('button', { name: /删除/i });
  await deleteBtn.click();

  // 确认删除（如果有弹窗）
  try {
    const confirmBtn = page.getByRole('button', { name: /确认|确定/i });
    await confirmBtn.click({ timeout: 2000 });
  } catch {
    // 没有确认弹窗
  }

  await page.waitForTimeout(500);
}

// ==================== 测试套件 ====================

test.describe('工作区管理 - 全尺寸兼容性测试', () => {
  test.describe('桌面端测试 (1400x900)', () => {
    test('桌面端 - 会话列表显示目录路径', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '桌面端测试会话');

      // 打开会话列表
      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      // 验证会话列表中的目录路径显示
      const sessionItem = page.getByText('桌面端测试会话').first();
      await expect(sessionItem).toBeVisible();

      // 验证目录路径是否显示
      const directoryLabel = page.getByText('D:').first();
      await expect(directoryLabel).toBeVisible();

      // 验证目录路径的样式（小字体，灰色）
      const workspacePathElement = page.locator('text=/D:.*TestWebCode/').first();
      await expect(workspacePathElement).toBeVisible();

      console.log('✅ 桌面端 - 目录路径显示正确');
    });

    test('桌面端 - 授权管理按钮可见性', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '授权测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      // 找到测试会话
      const sessionItem = page.getByText('授权测试会话').first();

      // 悬停会话项
      await sessionItem.hover();

      // 检查授权管理按钮是否存在
      const authBtn = page.getByRole('button', { name: /授权/i });
      await expect(authBtn).toBeVisible();

      console.log('✅ 桌面端 - 授权管理按钮可见');
    });

    test('桌面端 - 打开授权管理模态框', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '模态框测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('模态框测试会话').first();
      await sessionItem.hover();

      // 点击授权管理按钮
      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      // 等待模态框出现
      await page.waitForTimeout(500);

      // 验证模态框标题
      const modalTitle = page.getByText('目录授权管理');
      await expect(modalTitle).toBeVisible();

      // 验证当前目录显示
      const currentDir = page.getByText('D:').first();
      await expect(currentDir).toBeVisible();

      console.log('✅ 桌面端 - 授权模态框打开成功');
    });

    test('桌面端 - 添加新授权', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '添加授权测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('添加授权测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 填写用户名
      const usernameInput = page.getByPlaceholder('输入要授权的用户名');
      await usernameInput.fill(TEST_USER_2);

      // 选择权限级别
      const permissionSelect = page.getByRole('combobox');
      await permissionSelect.selectOption('write');

      // 点击添加按钮
      const addBtn = page.getByRole('button', { name: /添加/i });
      await addBtn.click();

      // 等待授权显示在列表中
      await page.waitForTimeout(1000);

      // 验证新用户是否出现在授权列表中
      const authorizedUser = page.getByText(TEST_USER_2);
      await expect(authorizedUser).toBeVisible();

      console.log('✅ 桌面端 - 添加授权成功');
    });

    test('桌面端 - 撤销授权', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '撤销授权测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('撤销授权测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 先添加一个授权以便测试撤销
      const usernameInput = page.getByPlaceholder('输入要授权的用户名');
      await usernameInput.fill(TEST_USER_2);
      const addBtn = page.getByRole('button', { name: /添加/i });
      await addBtn.click();
      await page.waitForTimeout(1000);

      // 点击撤销按钮
      const revokeBtns = page.getByRole('button', { name: /撤销|取消/i });
      const lastRevokeBtn = revokeBtns.last();
      await lastRevokeBtn.click();

      // 确认撤销
      try {
        const confirmBtn = page.getByRole('button', { name: /确定/i });
        await confirmBtn.click({ timeout: 2000 });
      } catch {
        // 没有确认弹窗
      }

      // 验证用户已从授权列表中移除
      await page.waitForTimeout(500);
      const authorizedUser = page.getByText(TEST_USER_2);
      await expect(authorizedUser).not.toBeVisible();

      console.log('✅ 桌面端 - 撤销授权成功');
    });

    test('桌面端 - 权限级别选择', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '权限级别测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('权限级别测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 验证所有权限级别选项
      const permissionSelect = page.getByRole('combobox');
      await expect(permissionSelect).toBeVisible();

      // 验证选项内容
      await expect(page.getByText('只读（仅查看文件）')).toBeVisible();
      await expect(page.getByText('读写（可编辑文件）')).toBeVisible();
      await expect(page.getByText('管理员（可授权其他用户）')).toBeVisible();

      console.log('✅ 桌面端 - 权限级别选项完整');
    });

    test('桌面端 - 关闭授权模态框', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '关闭模态框测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('关闭模态框测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 点击关闭按钮
      const closeBtn = page.getByRole('button', { name: '关闭' });
      await closeBtn.click();

      // 验证模态框已关闭
      const modalTitle = page.getByText('目录授权管理');
      await expect(modalTitle).not.toBeVisible();

      console.log('✅ 桌面端 - 模态框关闭成功');
    });
  });

  test.describe('平板端测试 (1024x768)', () => {
    test('平板端 - 会话列表显示目录路径', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.tablet);
      await performLogin(page);
      await createTestSession(page, '平板端测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('平板端测试会话').first();
      await expect(sessionItem).toBeVisible();

      const directoryLabel = page.getByText('D:').first();
      await expect(directoryLabel).toBeVisible();

      // 平板端目录路径可能换行，验证仍然可见
      const workspacePathElement = page.locator('text=/D:.*TestWebCode/').first();
      await expect(workspacePathElement).toBeVisible();

      console.log('✅ 平板端 - 目录路径显示正确');
    });

    test('平板端 - 授权管理按钮布局', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.tablet);
      await performLogin(page);
      await createTestSession(page, '平板端布局测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('平板端布局测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await expect(authBtn).toBeVisible();

      console.log('✅ 平板端 - 授权按钮布局正确');
    });

    test('平板端 - 授权模态框适配', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.tablet);
      await performLogin(page);
      await createTestSession(page, '平板端模态框测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('平板端模态框测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 验证模态框在平板端是否适配
      const modalTitle = page.getByText('目录授权管理');
      await expect(modalTitle).toBeVisible();

      // 验证模态框宽度是否适配平板
      const modal = page.locator('.fixed.inset-0');
      const modalBox = await modal.boundingBox();

      // 模态框应该不超出屏幕宽度
      await expect(modalBox.width).toBeLessThanOrEqual(SCREEN_SIZES.tablet.width * 0.9);

      console.log('✅ 平板端 - 模态框适配正确');
    });
  });

  test.describe('移动端测试 (375x667)', () => {
    test('移动端 - 会话列表抽屉显示目录路径', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.mobile);
      await performLogin(page);
      await createTestSession(page, '移动端测试会话');

      // 点击菜单按钮打开侧边栏
      const menuBtn = page.locator('button').filter({ hasText: /菜单/i }).first();
      await menuBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('移动端测试会话').first();
      await expect(sessionItem).toBeVisible();

      // 验证目录路径显示（移动端可能需要滚动查看）
      const directoryLabel = page.getByText('D:').first();
      await expect(directoryLabel).toBeVisible();

      console.log('✅ 移动端 - 目录路径在侧边栏显示正确');
    });

    test('移动端 - 授权管理入口', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.mobile);
      await performLogin(page);
      await createTestSession(page, '移动端授权测试会话');

      const menuBtn = page.locator('button').filter({ hasText: /菜单/i }).first();
      await menuBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('移动端授权测试会话').first();
      await sessionItem.click();

      // 移动端点击会话进入详情后，查找授权按钮
      const authBtn = page.getByRole('button', { name: /授权/i });
      await expect(authBtn).toBeVisible();

      console.log('✅ 移动端 - 授权管理入口正确');
    });

    test('移动端 - 授权模态框全屏显示', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.mobile);
      await performLogin(page);
      await createTestSession(page, '移动端全屏测试会话');

      const menuBtn = page.locator('button').filter({ hasText: /菜单/i }).first();
      await menuBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('移动端全屏测试会话').first();
      await sessionItem.click();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      const modalTitle = page.getByText('目录授权管理');
      await expect(modalTitle).toBeVisible();

      // 移动端模态框应该占满大部分屏幕
      const modal = page.locator('.fixed.inset-0');
      const modalBox = await modal.boundingBox();
      await expect(modalBox.height).toBeGreaterThan(SCREEN_SIZES.mobile.height * 0.6);

      console.log('✅ 移动端 - 模态框全屏显示正确');
    });
  });

  test.describe('大屏移动端测试 (414x896)', () => {
    test('大屏移动端 - 目录路径显示', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.mobileLarge);
      await performLogin(page);
      await createTestSession(page, '大屏移动端测试会话');

      const menuBtn = page.locator('button').filter({ hasText: /菜单/i }).first();
      await menuBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('大屏移动端测试会话').first();
      await expect(sessionItem).toBeVisible();

      const directoryLabel = page.getByText('D:').first();
      await expect(directoryLabel).toBeVisible();

      console.log('✅ 大屏移动端 - 目录路径显示正确');
    });

    test('大屏移动端 - 授权管理体验', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.mobileLarge);
      await performLogin(page);
      await createTestSession(page, '大屏移动端授权测试会话');

      const menuBtn = page.locator('button').filter({ hasText: /菜单/i }).first();
      await menuBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('大屏移动端授权测试会话').first();
      await sessionItem.click();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await expect(authBtn).toBeVisible();

      // 验证按钮大小适合触摸
      const btnBox = await authBtn.boundingBox();
      await expect(btnBox.width).toBeGreaterThanOrEqual(44); // 44px 最小触摸目标
      await expect(btnBox.height).toBeGreaterThanOrEqual(44);

      console.log('✅ 大屏移动端 - 授权按钮触摸友好');
    });
  });

  test.describe('跨尺寸响应式测试', () => {
    const allSizes = Object.values(SCREEN_SIZES);

    allSizes.forEach((size) => {
      test.describe(`${size.name}`, () => {
        test('所有尺寸 - 目录路径显示一致性', async ({ page }) => {
          await page.setViewportSize(size);
          await performLogin(page);
          await createTestSession(page, `响应式测试-${size.name}`);

          const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
          await sessionsBtn.click();
          await page.waitForTimeout(500);

          const sessionItem = page.getByText(`响应式测试-${size.name}`);
          await expect(sessionItem).toBeVisible();

          const directoryLabel = page.getByText('D:').first();
          await expect(directoryLabel).toBeVisible();

          console.log(`✅ ${size.name} - 目录路径显示一致`);
        });

        test('所有尺寸 - 授权管理入口存在', async ({ page }) => {
          await page.setViewportSize(size);
          await performLogin(page);
          await createTestSession(page, `授权入口-${size.name}`);

          const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
          await sessionsBtn.click();
          await page.waitForTimeout(500);

          const sessionItem = page.getByText(`授权入口-${size.name}`);
          await expect(sessionItem).toBeVisible();

          // 查找授权按钮
          const authBtn = page.getByRole('button', { name: /授权/i });
          await expect(authBtn).toBeVisible();

          console.log(`✅ ${size.name} - 授权入口存在`);
        });

        test('所有尺寸 - 模态框可打开', async ({ page }) => {
          await page.setViewportSize(size);
          await performLogin(page);
          await createTestSession(page, `模态框-${size.name}`);

          const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
          await sessionsBtn.click();
          await page.waitForTimeout(500);

          const sessionItem = page.getByText(`模态框-${size.name}`);
          await sessionItem.click();

          const authBtn = page.getByRole('button', { name: /授权/i });
          await authBtn.click();

          await page.waitForTimeout(500);

          const modalTitle = page.getByText('目录授权管理');
          await expect(modalTitle).toBeVisible();

          console.log(`✅ ${size.name} - 模态框可打开`);
        });
      });
    });
  });

  test.describe('授权管理功能测试', () => {
    test('授权管理 - 空授权列表状态', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '空列表测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('空列表测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 验证空状态提示
      const emptyText = page.getByText('暂无授权用户');
      await expect(emptyText).toBeVisible();

      console.log('✅ 授权管理 - 空列表状态正确');
    });

    test('授权管理 - 只读权限用户显示', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '只读测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('只读测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 添加只读用户
      const usernameInput = page.getByPlaceholder('输入要授权的用户名');
      await usernameInput.fill('readonly-user');
      const permissionSelect = page.getByRole('combobox');
      await permissionSelect.selectOption('read');
      const addBtn = page.getByRole('button', { name: /添加/i });
      await addBtn.click();
      await page.waitForTimeout(1000);

      // 验证只读权限标签样式
      const permissionBadge = page.locator('text=只读').first();
      await expect(permissionBadge).toBeVisible();

      console.log('✅ 授权管理 - 只读权限显示正确');
    });

    test('授权管理 - 过期时间设置', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);
      await createTestSession(page, '过期测试会话');

      const sessionsBtn = page.getByRole('button', { name: /会话历史|历史/i });
      await sessionsBtn.click();
      await page.waitForTimeout(500);

      const sessionItem = page.getByText('过期测试会话').first();
      await sessionItem.hover();

      const authBtn = page.getByRole('button', { name: /授权/i });
      await authBtn.click();

      await page.waitForTimeout(500);

      // 设置过期时间（7天后）
      const expireDate = new Date();
      expireDate.setDate(expireDate.getDate() + 7);
      const expireInput = page.getByRole('textbox').filter({ hasText: /过期/i });
      await expireInput.fill(expireDate.toISOString().slice(0, 16));

      const usernameInput = page.getByPlaceholder('输入要授权的用户名');
      await usernameInput.fill('expire-user');
      const addBtn = page.getByRole('button', { name: /添加/i });
      await addBtn.click();
      await page.waitForTimeout(1000);

      // 验证过期时间是否显示
      const expireText = page.getByText(/过期时间/i);
      await expect(expireText).toBeVisible();

      console.log('✅ 授权管理 - 过期时间设置正确');
    });
  });

  test.describe('创建会话 - 目录选择测试', () => {
    test('创建会话 - 默认目录选项', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);

      await page.goto(`${BASE_URL}/code-assistant`);
      await page.waitForLoadState('networkidle');

      const newSessionBtn = page.getByRole('button', { name: /新建|创建/i });
      await newSessionBtn.click();
      await page.waitForTimeout(500);

      // 验证默认目录选项存在
      const defaultTab = page.getByRole('tab', { name: '默认目录' });
      await expect(defaultTab).toBeVisible();

      console.log('✅ 创建会话 - 默认目录选项存在');
    });

    test('创建会话 - 自定义路径输入', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);

      await page.goto(`${BASE_URL}/code-assistant`);
      await page.waitForLoadState('networkidle');

      const newSessionBtn = page.getByRole('button', { name: /新建|创建/i });
      await newSessionBtn.click();
      await page.waitForTimeout(500);

      const customTab = page.getByRole('tab', { name: '自定义路径' });
      await customTab.click();

      // 验证路径输入框
      const pathInput = page.getByPlaceholder('工作目录路径');
      await expect(pathInput).toBeVisible();

      console.log('✅ 创建会话 - 自定义路径输入框存在');
    });

    test('创建会话 - 选择已有目录', async ({ page }) => {
      await page.setViewportSize(SCREEN_SIZES.desktop);
      await performLogin(page);

      await page.goto(`${BASE_URL}/code-assistant`);
      await page.waitForLoadState('networkidle');

      const newSessionBtn = page.getByRole('button', { name: /新建|创建/i });
      await newSessionBtn.click();
      await page.waitForTimeout(500);

      // 验证"选择已有目录"选项
      const existingTab = page.getByRole('tab', { name: /已有目录|选择已有/i });
      await expect(existingTab).toBeVisible();

      console.log('✅ 创建会话 - 选择已有目录选项存在');
    });
  });

  test.afterAll(async ({ page }) => {
    console.log('='.repeat(50));
    console.log('测试套件执行完成');
    console.log('='.repeat(50));
  });
});

// ==================== 测试结果总结 ====================

/*
 * 测试执行日期: 2026-03-13
 *
 * 测试环境:
 * - URL: http://localhost:5000
 * - 测试用户: luhaiyan
 *
 * 测试结果:
 *
 * ✅ 桌面端 (>1200px):
 *   1. 会话列表显示目录路径 - 通过
 *      - 验证: 会话名称下方显示工作目录路径
 *      - 示例: D:\VSWorkshop\TestWebCode
 *
 *   2. 授权管理按钮显示 - 通过
 *      - 验证: 会话项操作按钮区有4个按钮（分享、授权、重命名、删除）
 *      - 授权按钮使用绿色锁图标，位于分享和重命名按钮之间
 *
 *   3. 授权管理模态框打开 - 通过
 *      - 验证: 点击授权按钮后显示授权管理模态框
 *      - 模态框包含: 标题、目录路径、添加授权表单、已授权用户列表
 *
 *   4. 添加授权功能 - 部分通过
 *      - 验证: 可以输入用户名、选择权限、点击添加授权按钮
 *      - 问题: API返回认证配置错误
 *      - 错误: "System.InvalidOperationException: No authentication handlers are registered"
 *      - 需要修复: 后端授权API需要配置认证处理器
 *
 * ⚠️ 移动端 (<768px):
 *   1. 会话列表显示目录路径 - 未通过
 *      - 验证: 会话列表只显示标题和时间，没有显示目录路径
 *      - 需要实现: 为移动端会话列表添加目录路径显示
 *   2. 授权管理功能 - 未测试
 *      - 说明: 移动端使用独立的会话列表实现，需要单独添加授权管理功能
 *
 * 📸 截图已保存:
 *   - test-results/desktop-session-list-with-paths.png
 *   - test-results/desktop-session-list-current-state.png
 *   - test-results/desktop-authorization-modal-opened.png
 *   - test-results/desktop-authorization-api-error.png
 *   - test-results/mobile-session-list-no-directory-path.png
 *
 * 📝 后续工作:
 *   1. 为移动端会话列表添加目录路径显示
 *   2. 为移动端添加授权管理入口和模态框
 *   3. 修复后端授权API的认证配置问题
 */
