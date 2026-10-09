import { readdir, mkdir, cp, access, writeFile } from 'node:fs/promises';
import path from 'node:path';

const input = path.resolve(process.argv[2] ?? 'artifacts/browser');
const output = path.resolve(process.argv[3] ?? 'artifacts/site');
async function findIndex(directory, depth = 0) {
  try { await access(path.join(directory, 'index.html')); return directory; } catch {}
  if (depth > 5) return null;
  for (const entry of await readdir(directory, { withFileTypes: true })) {
    if (!entry.isDirectory() || entry.name === '_framework') continue;
    const result = await findIndex(path.join(directory, entry.name), depth + 1);
    if (result) return result;
  }
  return null;
}
const root = await findIndex(input);
if (!root) throw new Error(`No published index.html found under ${input}`);
await mkdir(output, { recursive: true });
await cp(root, output, { recursive: true });
await writeFile(path.join(output, '.nojekyll'), '');
await writeFile(path.join(output, 'build.json'), JSON.stringify({ commit: process.env.GITHUB_SHA ?? null, source: 'Uno Platform .NET WebAssembly', generatedUtc: new Date().toISOString() }, null, 2));
console.log(`Published site: ${root} -> ${output}`);
