// A native key event and CDP's insertText take different paths through Uno's
// shared text input. Observe completion of the preceding native operation before
// inserting text; two RAFs and an element named uno-input do not establish it.
// All actions remain real pointer/keyboard/input events, with no control mutation.
export const rowInputTrace = [];
const normalize = value => typeof value === 'string' ? value.replace(/\r\n?/g, '\n') : value;
async function snapshot(page, field) {
  return page.evaluate(field => {
    const native = JSON.parse(editorBridge.SnapshotRowEditorInput(field));
    const input = document.getElementById('uno-input');
    return { native, dom: input ? { active: document.activeElement === input, tag: input.tagName,
      value: input.value, selectionStart: input.selectionStart, selectionEnd: input.selectionEnd } : null };
  }, field);
}
function synchronized(s) {
  return s.native.within && s.native.enabled && !s.native.readOnly && s.dom?.active &&
    typeof s.native.text === 'string' && normalize(s.native.text) === normalize(s.dom.value);
}
export async function waitForRowInput(page, field, step, predicate = () => true) {
  const start = Date.now(); let last;
  while (Date.now() - start < 15000) {
    last = await snapshot(page, field);
    if (synchronized(last) && predicate(last)) {
      rowInputTrace.push({ field, step, milliseconds: Date.now() - start, ...last });
      if (rowInputTrace.length > 128) rowInputTrace.shift();
      return last;
    }
    await page.waitForTimeout(25);
  }
  rowInputTrace.push({ field, step, failed: true, ...last });
  throw new Error(`Native row input ${field}/${step} timed out: ${JSON.stringify(last)}`);
}
export async function replaceRowInput(page, field, value, clickField) {
  await clickField();
  await waitForRowInput(page, field, 'focused-and-synchronized');
  await page.keyboard.press('Control+A');
  await waitForRowInput(page, field, 'selected-all', s =>
    s.native.selectionStart === 0 && s.native.selectionLength === s.native.text.length &&
    s.dom.selectionStart === 0 && s.dom.selectionEnd === s.dom.value.length);
  await page.keyboard.press('Backspace');
  await waitForRowInput(page, field, 'cleared', s =>
    s.native.text === '' && s.dom.value === '' && s.native.selectionLength === 0 &&
    s.native.selectionStart === 0 && s.dom.selectionStart === 0 && s.dom.selectionEnd === 0);
  if (value.length) await page.keyboard.insertText(value);
  return waitForRowInput(page, field, 'replacement-complete', s => normalize(s.native.text) === normalize(value));
}
