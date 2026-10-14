import { test } from 'node:test';
import { readFile } from 'node:fs/promises';
import assert from 'node:assert/strict';
import { browserGroups, suitePlan, runSuiteGroup, validateArtifactCommit } from '../../scripts/run-browser-suites.mjs';

test('all twenty browser commands occur exactly once in the shard partition', () => {
  const expected = ['row-editor-templates', 'deployment-smoke', 'row-form-layout', 'row-editing', 'native-tab-order',
    'native-cells', 'native-modes', 'native-input-regressions', 'scrollbars', 'navigation-viewport', 'template-selectors',
    'run', 'workspace', 'summaries', 'performance', 'review', 'value-filters', 'chrome', 'filter-history', 'column-chooser'];
  const actual = suitePlan();
  assert.deepEqual(actual.toSorted(), expected.toSorted()); assert.equal(new Set(actual).size, 20);
  assert.equal(Object.keys(browserGroups).length, 4);
});
test('caller cannot mutate later plans and unknown/prototype group names are rejected', () => {
  const plan = suitePlan('forms'); plan.length = 0; assert.equal(suitePlan('forms').length, 4);
  for (const group of ['missing', 'toString', '__proto__', '../run']) assert.throws(() => suitePlan(group));
});
test('failed suites do not skip later independent suites or silently retry', async () => {
  const calls = [];
  const report = await runSuiteGroup('forms', async suite => { calls.push(suite); return { code: calls.length === 1 ? 1 : 0 }; });
  assert.deepEqual(calls, suitePlan('forms')); assert.equal(report.passed, false);
  assert.deepEqual(report.results.map(r => r.passed), [false, true, true, true]);
});
test('thrown process errors, signals, timeouts and missing exit codes cannot pass', async () => {
  let call = 0;
  const report = await runSuiteGroup('forms', async () => {
    switch (call++) {
      case 0: throw new Error('spawn failed');
      case 1: return { code: 0, signal: 'SIGTERM' };
      case 2: return { code: 0, timedOut: true };
      default: return { code: null };
    }
  });
  assert.equal(report.results.length, 4); assert.equal(report.passed, false);
  assert.ok(report.results.every(r => !r.passed));
});
test('successful group reports the exact ordered plan', async () => {
  const report = await runSuiteGroup('features', async () => ({ code: 0, signal: null, timedOut: false }));
  assert.equal(report.passed, true); assert.deepEqual(report.results.map(r => r.suite), suitePlan('features'));
});
test('artifact provenance rejects missing and mismatched revisions', () => {
  assert.equal(validateArtifactCommit({ commit: 'abc' }, 'abc'), 'abc');
  for (const value of [null, {}, { commit: '' }, { commit: 1 }]) assert.throws(() => validateArtifactCommit(value, 'abc'));
  assert.throws(() => validateArtifactCommit({ commit: 'other' }, 'abc'));
});

test('Pages reruns select only their own artifact without deleting earlier evidence', async () => {
  const workflow = await readFile(new URL('../../.github/workflows/pages.yml', import.meta.url), 'utf8');
  function actionInput(action, name) {
    const escaped = action.replaceAll('/', '\\/');
    const section = workflow.match(new RegExp('uses: ' + escaped + '@[^\\n]+([\\s\\S]*?)(?=\\n      - |$)'))?.[1];
    assert.ok(section, 'Missing ' + action);
    return section.match(new RegExp('^          ' + name + ': (.+)$', 'm'))?.[1];
  }
  const upload = actionInput('actions/upload-pages-artifact', 'name');
  const deploy = actionInput('actions/deploy-pages', 'artifact_name');
  assert.equal(upload, 'github-pages-${{ github.run_attempt }}');
  assert.equal(deploy, upload);
  assert.notEqual(upload.replace('${{ github.run_attempt }}', '1'), upload.replace('${{ github.run_attempt }}', '2'));
  assert.match(workflow, /actions: read/);
  assert.match(workflow, /github.event.workflow_run.conclusion == 'success'/);
  assert.equal(actionInput('actions/download-artifact', 'run-id'), '${{ github.event.workflow_run.id }}');
});

test('aggregate browser gate runs after failure but remains cancellable when superseded', async () => {
  const workflow = await readFile(new URL('../../.github/workflows/validate.yml', import.meta.url), 'utf8');
  const remainder = workflow.split('\n  browser:\n')[1];
  assert.ok(remainder, 'Missing aggregate browser gate');
  const gate = remainder.split(/\n  [a-z][\w-]*:\n/, 1)[0];
  assert.match(gate, /^    if: \$\{\{ !cancelled\(\) \}\}$/m);
  assert.match(gate, /^    needs: \[browser-build, browser-tests\]$/m);
  assert.match(gate, /^    timeout-minutes: 10$/m);
  assert.ok(gate.includes('test "$BUILD_RESULT" = success'));
  assert.ok(gate.includes('test "$TEST_RESULT" = success'));
  assert.ok(gate.includes('for group in forms native viewport features; do'));
  assert.ok(gate.includes('test -f "artifacts/browser-validation/runner-$group.json"'));
  assert.match(workflow, /cancel-in-progress: true/);
  assert.match(workflow, /fail-fast: false/);
});
