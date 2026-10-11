#!/usr/bin/env python3
"""Supplementary source/compiler check for disconnected Linux environments.

Usage: python scripts/validate-offline.py /path/to/extracted/kit
The kit is produced by the Offline validation kit workflow. This does not replace
MSBuild, NuGet packing or browser validation in the main CI workflow.
"""
from __future__ import annotations
import json, os, pathlib, shutil, struct, subprocess, sys

root = pathlib.Path(__file__).resolve().parents[1]
kit = pathlib.Path(sys.argv[1]).resolve()
out = root / 'artifacts/offline'
out.mkdir(parents=True, exist_ok=True)
env = os.environ | {'DOTNET_ROOT': str(kit), 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
globals_file = out / 'ImplicitUsings.g.cs'
globals_file.write_text('\n'.join('global using ' + ns + ';' for ns in (
    'System', 'System.Collections.Generic', 'System.IO', 'System.Linq',
    'System.Net.Http', 'System.Threading', 'System.Threading.Tasks')))
refs = list(sorted((kit / 'refs').glob('*.dll')))
def managed(file: pathlib.Path) -> bool:
    data = file.read_bytes()
    if data[:2] != b'MZ': return False
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    directory = optional + (112 if magic == 0x20b else 96)
    return struct.unpack_from('<I', data, directory + 14 * 8)[0] != 0

external = [file for file in sorted((kit / 'deps').glob('*.dll'))
            if managed(file) and not file.name.startswith('DataGridScene.') and not any(file.name == ref.name for ref in refs)]
projects = [
    ('src', 'Core', []), ('src', 'Data', ['Core']), ('src', 'Layout', ['Core']),
    ('src', 'Export', ['Core']), ('src', 'Rendering.Skia', ['Core', 'Layout']),
    ('src', 'Controls', ['Core', 'Layout', 'Export', 'Rendering.Skia']),
    ('tests', 'Validation', ['Core', 'Data', 'Layout', 'Export', 'Rendering.Skia']),
    ('tests', 'Tests', ['Core', 'Data', 'Layout', 'Export', 'Validation'])]
for folder, name, dependencies in projects:
    source = root / folder / ('DataGridScene.' + name)
    files = sorted(file for file in source.glob('*.cs'))
    # User controls have no XAML/source-generated implementation in this project.
    more_refs = external if name in ('Rendering.Skia', 'Controls', 'Validation') else []
    parameters = ['-nologo', '-noconfig', '-nostdlib+', '-langversion:preview', '-nullable:enable',
                  '-unsafe+', '-deterministic+', '-debug:portable', '-optimize+',
                  '-target:' + ('exe' if name == 'Tests' else 'library'),
                  '-out:' + str(out / ('DataGridScene.' + name + '.dll')),
                  '-define:NET;NET10_0;NETCOREAPP;__UNO_SKIA__']
    parameters += ['-r:' + str(file) for file in refs + more_refs]
    parameters += ['-r:' + str(out / ('DataGridScene.' + dep + '.dll')) for dep in dependencies]
    parameters += [str(globals_file)] + [str(file) for file in files]
    print('COMPILE', name, flush=True)
    subprocess.run([str(kit/'dotnet'), str(kit/'compiler/csc.dll'), *parameters], check=True, cwd=root, env=env)
(out / 'DataGridScene.Tests.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
    'tfm': 'net10.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}}))
for name in ('SkiaSharp.dll', 'SkiaSharp.HarfBuzz.dll', 'HarfBuzzSharp.dll', 'libSkiaSharp.so', 'libHarfBuzzSharp.so'):
    shutil.copy2(kit / 'deps' / name, out / name)
print('RUN managed and raster source conformance', flush=True)
subprocess.run([str(kit/'dotnet'), str(out/'DataGridScene.Tests.dll')], check=True, cwd=root, env=env)
