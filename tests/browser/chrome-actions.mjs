// Wait for the selected ribbon surface AND its actual native layout. A completed
// pointer event or two browser animation frames need not drain Uno's layout queue.
// This helper reads diagnostics only; it never focuses, lays out or repairs controls.
const demoCommands = ['previous-example', 'next-example', 'undo', 'redo', 'copy', 'paste',
  'add', 'delete', 'filter', 'columns', 'best-fit', 'ribbon-bands', 'full-width', 'export',
  'export-xlsx', 'export-html', 'ribbon-save', 'ribbon-restore'];

export async function clickChromeAction(page, name, timeout = 15000) {
  const surface = name.startsWith('theme-') || name.startsWith('density-') ? 'themes'
    : demoCommands.includes(name) ? 'demo' : null;
  const handle = await page.waitForFunction(({ name, surface }) => {
    const state = globalThis.dataGridScene?.snapshot();
    if (!state || surface === 'themes' && !state.chrome.themesVisible || surface === 'demo' && state.chrome.themesVisible) return false;
    const r = state.buttons[name];
    if (!r || ![r.x, r.y, r.width, r.height].every(Number.isFinite) || r.width <= 0 || r.height <= 0) return false;
    if (r.x < 0 || r.y < 0 || r.right > innerWidth || r.bottom > innerHeight) return false;
    return r;
  }, { name, surface }, { timeout, polling: 'raf' });
  try {
    const r = await handle.jsonValue();
    await page.mouse.click(r.x + r.width / 2, r.y + r.height / 2);
  } finally { await handle.dispose(); }
}
