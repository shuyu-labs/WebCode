import { test, expect } from '@playwright/test';

const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';

// 设置全局超时时间为3分钟
test.setTimeout(180000);

test.describe('Git项目导入功能测试', () => {
  test('导入GitHub公开仓库成功', async ({ page }) => {
    try {
      // 1. 导航到登录页面
      await page.goto('http://localhost:5000', { waitUntil: 'networkidle' });
      console.log('✅ 页面加载完成');

      // 等待页面完全渲染
      await page.waitForLoadState('domcontentloaded');
      await page.waitForTimeout(2000);

      // 2. 输入用户名和密码
      const usernameInput = page.locator('input[placeholder="Enter username"]');
      await usernameInput.waitFor({ state: 'visible', timeout: 10000 });
      await usernameInput.fill('luhaiyan');
      console.log('✅ 输入用户名');

      const passwordInput = page.locator('input[placeholder="Enter password"]');
      await passwordInput.waitFor({ state: 'visible' });
      await passwordInput.fill(PASSWORD);
      console.log('✅ 输入密码');

      // 3. 点击登录按钮，等待导航完成
      const loginButton = page.locator('button:has-text("Login")');
      await loginButton.waitFor({ state: 'visible' });

      await Promise.all([
        page.waitForNavigation({ waitUntil: 'networkidle', timeout: 60000 }),
        loginButton.click()
      ]);
      console.log('✅ 登录成功，页面跳转完成');

      // 先截图登录后的主页面
      await page.screenshot({ path: 'main-page.png', fullPage: true });

      // 打印页面所有按钮文本
      const buttons = await page.locator('button').allTextContents();
      console.log('页面所有按钮文本:', buttons);

      // 打印整个页面内容
      const pageText = await page.locator('body').textContent();
      console.log('主页面完整内容:', pageText?.substring(0, 3000));

      // 点击用户头像/用户名旁边的菜单按钮
      const userMenuButton = page.locator('button').nth(1); // 第二个图标按钮，可能是用户菜单
      await userMenuButton.waitFor({ state: 'visible' });
      await userMenuButton.click();
      console.log('✅ 点击用户菜单按钮');
      await page.waitForTimeout(2000);

      // 截图用户菜单
      await page.screenshot({ path: 'user-menu.png', fullPage: true });

      // 打印用户菜单内容
      const userMenuContent = await page.locator('body').textContent();
      console.log('用户菜单内容:', userMenuContent?.substring(0, 2000));

      // 点击"自定义路径"标签
      const customPathTab = page.locator(':has-text("自定义路径")').first();
      await customPathTab.waitFor({ state: 'visible' });
      await customPathTab.click();
      console.log('✅ 点击自定义路径标签');
      await page.waitForTimeout(3000);

      // 截图自定义路径界面
      await page.screenshot({ path: 'custom-path.png', fullPage: true });

      // 打印自定义路径界面完整内容
      const customPathContent = await page.locator('body').textContent();
      console.log('自定义路径界面完整内容:', customPathContent);

      // 直接获取路径输入框
      const pathInput = page.locator('input').first();
      await pathInput.waitFor({ state: 'attached' });
      await pathInput.fill('https://gh.llkk.cc/https://github.com/lusile2024/skills-hub.git');
      console.log('✅ 输入Git仓库地址到路径输入框');

      // 点击创建会话按钮
      const createButton = page.locator('button:has-text("创建会话"), button:has-text("Create"), button:has-text("确认")').first();
      await createButton.waitFor({ state: 'visible' });
      await createButton.click();
      console.log('✅ 点击创建会话按钮');

      // 等待导入完成
      await page.waitForSelector(':has-text("成功"), :has-text("完成"), :has-text("success"), :has-text("skills-hub")', { timeout: 90000 });
      console.log('✅ Git项目导入成功，会话创建完成');

      console.log('🎉 所有测试步骤通过！');

    } catch (error) {
      // 出错时截图
      await page.screenshot({ path: 'error.png', fullPage: true });
      console.error('❌ 测试失败:', error);
      throw error;
    }
  });
});
