import assert from 'node:assert/strict';

let sequence = 0;

/**
 * Exercise Ctrl+C and wait for the actual asynchronous browser clipboard write.
 * A sentinel prevents stale content from passing copy assertions. Never writes
 * expected results or calls the grid's diagnostic copy function.
 */
export async function copyThroughKeyboard(page, { timeout = 5000 } = {}) {
  const sentinel = `DataGridScene-copy-pending-${++sequence}`;
  await page.evaluate(text => navigator.clipboard.writeText(text), sentinel);
  await page.keyboard.press('Control+C');
  const started = Date.now();
  let actual = sentinel;
  while (Date.now() - started < timeout) {
    actual = await page.evaluate(() => navigator.clipboard.readText());
    if (actual !== sentinel) return actual;
    await page.waitForTimeout(25);
  }
  assert.fail(`Ctrl+C did not replace the clipboard within ${timeout} ms.`);
}
