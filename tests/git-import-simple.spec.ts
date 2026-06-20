import { test, expect } from '@playwright/test';

const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';

// 设置全局超时时间为5分钟
test.setTimeout(300000);

test('新建会话时导入Git项目成功', async ({ page }) => {
  try {
    // 1. 登录
    await page.goto('http://localhost:5000', { waitUntil: 'networkidle' });
    await page.locator('input[placeholder="Enter username"]').fill('luhaiyan');
    await page.locator('input[placeholder="Enter password"]').fill(PASSWORD);
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'networkidle' }),
      page.locator('button:has-text("Login")').click()
    ]);
    console.log('✅ 登录成功');
    await page.waitForTimeout(2000);

    // 2. 打开新建会话对话框
    await page.locator('button').nth(1).click(); // 点击用户菜单按钮
    await page.waitForTimeout(2000);
    console.log('✅ 打开用户菜单');

    // 3. 点击自定义路径标签按钮
    await page.getByRole('button', { name: '自定义路径' }).click();
    await page.waitForTimeout(3000);
    console.log('✅ 切换到自定义路径标签');

    // 截图自定义路径界面
    await page.screenshot({ path: 'custom-path-final.png', fullPage: true });

    // 打印自定义路径界面内容
    const customPathContent = await page.locator('body').textContent();
    console.log('自定义路径界面内容:', customPathContent?.substring(0, 2000));

    // 4. 点击"自定义工作路径"选项
    const customPathOption = page.locator(':has-text("自定义工作路径")').first();
    await customPathOption.waitFor({ state: 'visible' });
    await customPathOption.click();
    console.log('✅ 点击自定义工作路径选项');
    await page.waitForTimeout(2000);

    // 截图点击后的界面
    await page.screenshot({ path: 'custom-path-input.png', fullPage: true });

    // 查找路径输入框
    const pathInput = page.locator('input[type="text"], input').first();
    await pathInput.waitFor({ state: 'visible' });
    await pathInput.fill('https://gh.llkk.cc/https://github.com/lusile2024/skills-hub.git');
    console.log('✅ 输入Git仓库地址');

    // 5. 点击创建会话按钮
    await page.locator('button:has-text("创建会话")').click();
    console.log('✅ 点击创建会话按钮，开始导入Git项目');

    // 6. 等待导入完成
    await page.waitForSelector(':has-text("skills-hub"), :has-text("Start a conversation")', { timeout: 120000 });
    console.log('🎉 Git项目导入成功，会话创建完成！');

  } catch (error) {
    await page.screenshot({ path: 'error-simple.png', fullPage: true });
    console.error('❌ 测试失败:', error);
    throw error;
  }
});
