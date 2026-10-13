import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import path from 'node:path';
import { startServer } from './server.mjs';

// Execute the SAME deployment verifier before merging, including its ribbon tab
// transitions. Local-artifact success never substitutes for public URL validation.
const root = process.env.SITE_ROOT ?? 'artifacts/site';
const marker = JSON.parse(await readFile(path.join(root, 'build.json'), 'utf8'));
assert.ok(marker.commit);
if (process.env.GITHUB_SHA) assert.equal(marker.commit, process.env.GITHUB_SHA);
const server = await startServer(root);
try {
  const code = await new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ['scripts/verify-deployment.mjs'], { stdio: 'inherit', env: {
      ...process.env, APP_URL: 'http://127.0.0.1:4173/DataGridScene/', VERIFY_LOCAL_ARTIFACT: '1',
      EXPECTED_COMMIT: marker.commit, DEPLOYMENT_RESULTS: path.resolve(process.env.BROWSER_RESULTS ?? 'artifacts/browser-validation', 'deployment-smoke')
    } });
    child.once('error', reject); child.once('exit', code => resolve(code ?? 1));
  });
  if (code !== 0) process.exitCode = code;
} finally { await new Promise(resolve => server.close(resolve)); }
