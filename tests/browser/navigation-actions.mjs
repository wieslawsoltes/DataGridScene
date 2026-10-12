import assert from 'node:assert/strict';

/** Use actual wheel input in the native navigation ScrollViewer. Layout bounds of
 * offscreen children are not hit targets. Never invoke the underlying command API. */
export async function clickNavigationAction(page, name) {
  for (let attempt = 0; attempt < 16; attempt++) {
    const state = await page.evaluate(() => dataGridScene.snapshot());
    const navigation = state.navigation, target = state.buttons[name];
    assert.ok(navigation?.visible && target, `Visible navigation and registered action required: ${name}`);
    const viewport = page.viewportSize();
    assert.ok(viewport, 'Navigation input requires a sized browser viewport');
    const bounds = navigation.bounds;
    const left = Math.max(0, bounds.x), right = Math.min(viewport.width, bounds.right);
    const top = Math.max(0, bounds.y), bottom = Math.min(viewport.height, bounds.bottom);
    const x = target.x + target.width / 2, y = target.y + target.height / 2;
    assert.ok(right > left + 8 && bottom > top + 8, 'Navigation viewport is clipped or hidden');
    assert.ok(x > left && x < right, `Action is not in the navigation viewport: ${name}`);
    if (y > top + 2 && y < bottom - 2) {
      await page.mouse.click(x, y);
      await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      return;
    }
    assert.ok(navigation.scrollableHeight > 0, `Navigation action is offscreen without a scroll range: ${name}`);
    const direction = y < top + 2 ? -1 : 1;
    const delta = direction * Math.min(480, Math.max(80, (bottom - top) * 0.55));
    await page.mouse.move((left + right) / 2, (top + bottom) / 2);
    await page.mouse.wheel(0, delta);
    await page.waitForFunction(offset => dataGridScene.snapshot().navigation.verticalOffset !== offset,
      navigation.verticalOffset, { timeout: 5000 });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }
  throw new Error(`Navigation action did not become visible after bounded wheel input: ${name}`);
}
