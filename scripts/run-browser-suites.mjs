import { spawn } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

// Each suite owns a fresh browser and the same local server port. Keep suites
// sequential inside a worker; CI runs these four disjoint groups on four hosts.
export const browserGroups = Object.freeze(Object.fromEntries(Object.entries({
  forms: ['row-editor-templates', 'row-form-layout', 'row-editing', 'summaries'],
  native: ['native-tab-order', 'native-cells', 'native-input-regressions', 'review'],
  viewport: ['native-modes', 'scrollbars', 'navigation-viewport', 'template-selectors', 'filter-history'],
  features: ['column-chooser', 'run', 'workspace', 'performance', 'value-filters', 'chrome', 'deployment-smoke']
}).map(([key, values]) => [key, Object.freeze(values)])));

export function suitePlan(group = 'all') {
  if (group === 'all') return Object.values(browserGroups).flat();
  if (!Object.hasOwn(browserGroups, group)) throw new Error('Unknown browser suite group: ' + group);
  return [...browserGroups[group]];
}

export function executeSuite(suite) {
  return new Promise(resolve => {
    const grouped = process.platform !== 'win32';
    const child = spawn(process.execPath, [path.join('tests/browser', suite + '.mjs')], {
      stdio: 'inherit', detached: grouped, shell: false
    });
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      try { if (grouped && child.pid) process.kill(-child.pid, 'SIGKILL'); else child.kill('SIGKILL'); }
      catch { /* A concurrent process exit is handled by close below. */ }
    }, 10 * 60 * 1000);
    timer.unref();
    child.once('error', error => { clearTimeout(timer); resolve({ code: null, signal: null, error: String(error), timedOut }); });
    child.once('close', (code, signal) => { clearTimeout(timer); resolve({ code, signal, timedOut }); });
  });
}

export async function runSuiteGroup(group, execute = executeSuite) {
  const plan = suitePlan(group), results = [];
  for (const suite of plan) {
    const start = Date.now(); console.log('BROWSER SUITE', suite);
    let result;
    try { result = await execute(suite); }
    catch (error) { result = { code: null, error: String(error) }; }
    results.push({ suite, ...result, milliseconds: Date.now() - start,
      passed: result?.code === 0 && !result.signal && !result.timedOut && !result.error });
    // Record failures but still run independent later suites. Never retry a
    // failed interaction or replace its result with a subsequent attempt.
  }
  return { group, passed: results.length === plan.length && results.every(r => r.passed), results };
}

export function validateArtifactCommit(build, expected) {
  if (!build || typeof build.commit !== 'string' || !build.commit.length) throw new Error('Missing browser build provenance');
  if (expected && build.commit !== expected) throw new Error(`Browser artifact ${build.commit} does not match checkout ${expected}`);
  return build.commit;
}

async function main() {
  if (process.argv.length > 3) throw new Error('Usage: node scripts/run-browser-suites.mjs [all|forms|native|viewport|features]');
  const group = process.argv[2] ?? 'all'; suitePlan(group);
  const directory = path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation');
  await mkdir(directory, { recursive: true });
  let artifactCommit = null;
  if (!process.env.APP_URL) {
    const build = JSON.parse(await readFile(path.resolve(process.env.SITE_ROOT ?? 'artifacts/site', 'build.json'), 'utf8'));
    artifactCommit = validateArtifactCommit(build, process.env.GITHUB_SHA);
  }
  const report = { commit: process.env.GITHUB_SHA ?? artifactCommit, artifactCommit,
    source: process.env.APP_URL ?? 'local-artifact', ...await runSuiteGroup(group) };
  await writeFile(path.join(directory, 'runner-' + group + '.json'), JSON.stringify(report, null, 2));
  console.log(JSON.stringify(report, null, 2));
  if (!report.passed) process.exitCode = 1;
}
if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href)
  main().catch(error => { console.error(error); process.exitCode = 1; });
