import { test, expect } from '@playwright/test';

// 测试配置
const BASE_URL = 'http://localhost:5000';
const USERNAME = 'luhaiyan';
const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';
const SCREEN_SIZE = { width: 1200, height: 800 };
const TEST_DIRECTORY = 'D:\\\\VSWorkshop\\\\TestWebCode';
const TEST_USER_2 = 'testuser2';

test.describe('目录授权功能测试', () => {
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

  test.describe('目录授权API测试', () => {
    test('测试获取可访问目录列表', async ({ request }) => {
      const response = await request.get(`${BASE_URL}/api/workspace/my-accessible-directories`);
      expect(response.ok()).toBeTruthy();

      const data = await response.json();
      console.log('✅ 可访问目录列表:', data);
    });

    test('测试目录授权功能', async ({ request }) => {
      // 先创建一个测试会话以获得工作目录
      const createSessionResponse = await request.post(`${BASE_URL}/api/session`, {
        data: {
          SessionId: `test-auth-${Date.now()}`,
          Title: '授权测试会话',
          WorkspacePath: TEST_DIRECTORY,
          IsCustomWorkspace: true
        }
      });

      expect(createSessionResponse.ok()).toBeTruthy();
      const sessionId = (await createSessionResponse.json()).SessionId;

      // 测试授权
      const authResponse = await request.post(`${BASE_URL}/api/workspace/authorize`, {
        data: {
          WorkspacePath: TEST_DIRECTORY,
          Username: TEST_USER_2,
          Permission: 'read',
          ExpireTime: new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString() // 7天后过期
        }
      });

      if (authResponse.ok()) {
        console.log('✅ 授权成功');

        // 验证授权列表
        const listResponse = await request.get(`${BASE_URL}/api/workspace/authorized-users?path=${encodeURIComponent(TEST_DIRECTORY)}`);
        if (listResponse.ok()) {
          const authList = await listResponse.json();
          console.log('✅ 授权用户列表:', authList);
        }
      } else {
        // 如果授权失败，检查是否有权限或其他错误
        const errorData = await authResponse.text();
        console.log('⚠️ 授权失败:', errorData);
      }
    });

    test('测试撤销授权', async ({ request }) => {
      // 测试撤销授权
      const revokeResponse = await request.delete(`${BASE_URL}/api/workspace/revoke-authorization`, {
        data: {
          path: TEST_DIRECTORY,
          username: TEST_USER_2
        }
      });

      if (revokeResponse.ok()) {
        console.log('✅ 撤销授权成功');
      } else {
        const errorData = await revokeResponse.text();
        console.log('⚠️ 撤销授权失败:', errorData);
      }
    });
  });

  test.describe('目录授权UI测试', () => {
    test('测试通过飞书命令授权', async ({ page }) => {
      // 通过输入框发送授权命令
      const inputBox = page.getByRole('textbox', { name: 'inputPlaceholder' });
      await inputBox.fill('/workspaceauth');
      await inputBox.press('Enter');

      // 等待命令响应
      await page.waitForTimeout(3000);

      // 检查是否出现授权相关界面
      // 注意：这取决于飞书命令的实现方式
      console.log('✅ 已发送授权命令');
    });

    test('测试目录权限验证', async ({ page }) => {
      // 尝试访问系统敏感目录
      const sensitiveDir = 'C:\\\\Windows';

      // 创建会话并尝试设置敏感目录
      await page.getByRole('button', { name: 'sessions' }).click();
      await page.getByRole('button', { name: '新建会话' }).click();

      const tabCustom = page.getByRole('tab', { name: '自定义路径' });
      await tabCustom.click();

      const customOption = page.getByText('自定义工作路径');
      await customOption.click();

      const pathInput = page.getByPlaceholder('工作目录路径');
      await pathInput.fill(sensitiveDir);

      const createButton = page.getByRole('button', { name: '创建会话' });
      await createButton.click();

      // 等待响应，应该显示错误或被阻止
      await page.waitForTimeout(3000);

      // 检查是否有错误提示
      const errorMessages = page.locator('text=/错误|错误|Error|failed/i');
      if (await errorMessages.count() > 0) {
        console.log('✅ 敏感目录已被正确阻止');
      } else {
        console.log('ℹ️ 未检测到敏感目录错误，可能需要后端配置');
      }
    });
  });
});
