import { test, expect } from '@playwright/test';

const PASSWORD = process.env.WEBCODE_TEST_PASSWORD ?? 'CHANGEME_TEST_PASSWORD';

// 设置全局超时时间为2分钟，足够Git克隆完成（手动克隆仅需20秒）
test.setTimeout(120000);

test('新建会话时导入Git项目完全测试', async ({ page }) => {
  try {
    // 1. 登录系统
    console.log('🔄 开始测试：Git项目导入功能');
    await page.goto('http://localhost:5000', { waitUntil: 'networkidle' });
    await page.waitForLoadState('domcontentloaded');
    await page.waitForTimeout(2000);

    // 输入用户名密码
    await page.locator('input[placeholder="Enter username"]').fill('luhaiyan');
    await page.locator('input[placeholder="Enter password"]').fill(PASSWORD);

    // 点击登录并等待跳转
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'networkidle' }),
      page.locator('button:has-text("Login")').click()
    ]);
    console.log('✅ 登录成功');
    await page.waitForTimeout(3000);

    // 2. 打开新建会话对话框
    await page.locator('button').nth(1).click(); // 点击用户菜单按钮（第二个图标按钮）
    await page.waitForTimeout(2000);
    console.log('✅ 打开用户菜单');

    // 3. 切换到自定义路径标签
    await page.getByRole('button', { name: '自定义路径' }).click();
    await page.waitForTimeout(2000);
    console.log('✅ 切换到自定义路径标签');

    // 4. 选择自定义工作路径选项
    await page.locator('text=自定义工作路径').click();
    await page.waitForTimeout(2000);
    console.log('✅ 选择自定义工作路径');

    // 5. 输入Git仓库地址
    const pathInput = page.locator('input').first();
    await pathInput.waitFor({ state: 'visible' });
    await pathInput.fill('https://gh.llkk.cc/https://github.com/lusile2024/skills-hub.git');
    console.log('✅ 输入Git仓库地址：https://gh.llkk.cc/https://github.com/lusile2024/skills-hub.git');
    await page.waitForTimeout(1000);

    // 6. 点击创建会话按钮，开始导入
    const createButton = page.locator('button:has-text("创建会话")');
    await createButton.waitFor({ state: 'visible' });

    console.log('🚀 点击创建会话，开始导入Git项目，预计30秒内完成...');
    await Promise.all([
      page.waitForNavigation({ waitUntil: 'networkidle', timeout: 30000 }), // 等待30秒克隆完成（手动测试仅需20秒）
      createButton.click()
    ]);

    // 7. 验证导入成功
    await page.waitForSelector('text=Start a conversation', { timeout: 120000 });
    console.log('🎉 测试成功！Git项目导入完成，会话创建成功！');
    console.log('✅ 项目：skills-hub 已成功导入并可用');

  } catch (error) {
    // 出错时截图并保存错误信息
    await page.screenshot({ path: 'git-import-error.png', fullPage: true });
    console.error('❌ 测试失败:', error);
    throw error;
  }
});
