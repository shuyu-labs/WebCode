import { test, expect } from '@playwright/test';

const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';

// 设置全局超时时间为5分钟
test.setTimeout(300000);

test.describe('Git项目导入功能测试', () => {
  test('导入GitHub公开仓库并创建会话成功', async ({ page }) => {
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
      await page.waitForTimeout(3000);

      // 4. 打开Settings菜单进入Projects管理页面
      const settingsButton = page.locator('button:has-text("Settings")');
      await settingsButton.waitFor({ state: 'visible' });
      await settingsButton.click();
      console.log('✅ 点击Settings按钮');
      await page.waitForTimeout(2000);

      // 点击Projects选项
      const projectsOption = page.locator(':has-text("Projects")').first();
      await projectsOption.waitFor({ state: 'visible' });
      await projectsOption.click();
      console.log('✅ 点击Projects选项');
      await page.waitForTimeout(5000);

      // 截图Projects页面
      await page.screenshot({ path: 'projects-page.png', fullPage: true });
      const projectsContent = await page.locator('body').textContent();
      console.log('Projects页面内容:', projectsContent?.substring(0, 5000));

      // 5. 查找并点击导入Git项目按钮
      const importButton = page.locator('button:has-text("导入"), button:has-text("Add"), button:has-text("Clone"), button:has-text("新建")').first();
      await importButton.waitFor({ state: 'visible', timeout: 15000 });
      await importButton.click();
      console.log('✅ 点击导入按钮');
      await page.waitForTimeout(2000);

      // 6. 输入Git仓库地址
      const gitInput = page.locator('input[type="text"], input').first();
      await gitInput.waitFor({ state: 'visible' });
      await gitInput.fill('https://gh.llkk.cc/https://github.com/lusile2024/skills-hub.git');
      console.log('✅ 输入Git仓库地址');

      // 7. 提交导入
      const submitButton = page.locator('button:has-text("导入"), button:has-text("确认"), button:has-text("OK"), button:has-text("创建")').first();
      await submitButton.waitFor({ state: 'visible' });
      await submitButton.click();
      console.log('✅ 提交导入请求');

      // 8. 等待导入成功
      await page.waitForSelector(':has-text("成功"), :has-text("完成"), :has-text("skills-hub")', { timeout: 120000 });
      console.log('✅ Git项目导入成功');

      // 9. 新建会话选择导入的项目
      const userMenuButton = page.locator('button').first();
      await userMenuButton.click();
      await page.waitForTimeout(2000);

      const newSessionOption = page.locator(':has-text("New Session")').first();
      await newSessionOption.click();
      console.log('✅ 点击新建会话');
      await page.waitForTimeout(2000);

      // 选择导入的项目
      const projectOption = page.locator(':has-text("skills-hub")').first();
      await projectOption.waitFor({ state: 'visible' });
      await projectOption.click();
      console.log('✅ 选择skills-hub项目');

      // 创建会话
      const createSessionButton = page.locator('button:has-text("创建会话")').first();
      await createSessionButton.click();
      console.log('✅ 创建会话成功');

      // 等待会话创建完成
      await page.waitForSelector(':has-text("Start a conversation")', { timeout: 30000 });
      console.log('🎉 所有测试步骤通过！Git项目导入并创建会话成功！');

    } catch (error) {
      // 出错时截图
      await page.screenshot({ path: 'error-final.png', fullPage: true });
      console.error('❌ 测试失败:', error);
      throw error;
    }
  });
});
