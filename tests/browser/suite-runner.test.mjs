import { test } from 'node:test';
import assert from 'node:assert/strict';
import { browserGroups, suitePlan, runSuiteGroup, validateArtifactCommit } from '../../scripts/run-browser-suites.mjs';

test('all nineteen harnesses occur exactly once in the shard partition', () => {
  const expected = ['row-editor-templates', 'deployment-smoke', 'row-form-layout', 'row-editing', 'native-tab-order',
    'native-cells', 'native-modes', 'native-input-regressions', 'scrollbars', 'navigation-viewport', 'template-selectors',
    'run', 'workspace', 'summaries', 'performance', 'review', 'value-filters', 'chrome', 'filter-history'];
  const actual = suitePlan();
  assert.deepEqual(actual.toSorted(), expected.toSorted()); assert.equal(new Set(actual).size, 19);
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
