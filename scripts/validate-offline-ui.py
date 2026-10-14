#!/usr/bin/env python3
"""Supplementary real Uno/X11 API validation using the Offline validation kit.

Usage: python scripts/validate-offline-ui.py /path/to/extracted/kit
Requires Linux, xvfb-run and the kit's native Skia/Uno dependencies. This compiles
shared sample/diagnostic sources, not MSBuild-generated WASM glue, and does not
replace package builds or browser pointer/keyboard/deployment tests.
"""
from __future__ import annotations
import json
import os
import pathlib
import shutil
import struct
import subprocess
import sys


def is_managed(file: pathlib.Path) -> bool:
    data = file.read_bytes()
    if len(data) < 64 or data[:2] != b'MZ':
        return False
    optional = struct.unpack_from('<I', data, 0x3c)[0] + 24
    if optional + 2 > len(data):
        return False
    directories = optional + (112 if struct.unpack_from('<H', data, optional)[0] == 0x20b else 96)
    return directories + 15 * 8 <= len(data) and struct.unpack_from('<I', data, directories + 14 * 8)[0] != 0


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    root = pathlib.Path(__file__).resolve().parents[1]
    kit = pathlib.Path(sys.argv[1]).resolve()
    if not (kit / 'dotnet').is_file() or not (kit / 'compiler/csc.dll').is_file():
        raise SystemExit('Pass the extracted Offline validation kit directory.')
    xvfb = shutil.which('xvfb-run')
    if xvfb is None:
        raise SystemExit('Install xvfb-run for the supplementary Linux UI host.')
    env = os.environ | {'DOTNET_ROOT': str(kit), 'DOTNET_CLI_TELEMETRY_OPTOUT': '1'}
    # Always compile/test current libraries first; never run stale assemblies.
    subprocess.run([sys.executable, str(root / 'scripts/validate-offline.py'), str(kit)], cwd=root, env=env, check=True, timeout=180)
    out = root / 'artifacts/offline-ui'
    out.mkdir(parents=True, exist_ok=True)
    references = sorted((kit / 'refs').glob('*.dll'))
    reference_names = {file.name for file in references}
    external = [file for file in sorted((kit / 'deps').glob('*.dll'))
                if not file.name.startswith('DataGridScene.') and file.name not in reference_names and is_managed(file)]
    for file in (kit / 'deps').iterdir():
        if file.is_file() and not file.name.startswith('DataGridScene.'):
            shutil.copy2(file, out / file.name)
    libraries = sorted((root / 'artifacts/offline').glob('DataGridScene.*.dll'))
    for file in libraries:
        shutil.copy2(file, out / file.name)
    program = out / 'OfflineUiProgram.cs'
    program.write_text('''using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
namespace DataGridScene.Demo;
internal static class OfflineUiProgram {
 [System.STAThread] public static void Main(string[] args) {
  UnoPlatformHostBuilder.Create().App(() => new OfflineUiApp()).UseX11().Build().Run();
 }
}
internal sealed class OfflineUiApp : Application {
 private Window? _window;
 public OfflineUiApp() { Resources.MergedDictionaries.Add(new XamlControlsResources()); }
 protected override void OnLaunched(LaunchActivatedEventArgs args) {
  _window = new Window { Content = new Grid() }; _window.Activate();
  _window.DispatcherQueue.TryEnqueue(() => {
   try {
    var reports = new[] { ChromeControlChecks.Run(), ControlReviewChecks.Run(), NativeControlChecks.Run(),
     NativeModeControlChecks.Run(), RowEditControlChecks.Run(), RowEditLayoutControlChecks.Run(),
     RowEditorControlChecks.Run(), FilterHistoryControlChecks.Run(), FilterPanelModeControlChecks.Run() };
    var report = new DataGridScene.Validation.ValidationReport(reports.SelectMany(item => item.Results).ToArray(), reports.Sum(item => item.Milliseconds));
    string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    System.IO.File.WriteAllText("artifacts/offline-ui-report.json", json);
    System.Console.WriteLine(json); System.Environment.Exit(report.Passed ? 0 : 1);
   } catch (System.Exception error) { System.Console.WriteLine(error); System.Environment.Exit(2); }
  });
 }
}
''', encoding='utf-8')
    sample = root / 'samples/DataGridScene.Demo'
    sources = sorted(sample.glob('*.cs')) + sorted((sample / 'Platforms/Desktop').glob('*.cs'))
    sources += sorted((sample / 'Platforms/WebAssembly').glob('BrowserBridge*.cs'))
    sources += [program, root / 'artifacts/offline/ImplicitUsings.g.cs']
    arguments = ['-nologo', '-noconfig', '-nostdlib+', '-langversion:preview', '-nullable:enable',
                 '-unsafe+', '-deterministic+', '-debug:portable', '-optimize+', '-target:exe',
                 '-main:DataGridScene.Demo.OfflineUiProgram', '-define:NET;NET10_0;NETCOREAPP;__UNO_SKIA__',
                 '-out:' + str(out / 'DataGridScene.Demo.dll')]
    arguments += ['-r:' + str(file) for file in references + external + libraries if not file.name.endswith('.Tests.dll')]
    arguments += [str(file) for file in sources]
    subprocess.run([str(kit / 'dotnet'), str(kit / 'compiler/csc.dll'), *arguments], cwd=root, env=env, check=True, timeout=120)
    (out / 'DataGridScene.Demo.runtimeconfig.json').write_text(json.dumps({'runtimeOptions': {
        'tfm': 'net10.0', 'framework': {'name': 'Microsoft.NETCore.App', 'version': '10.0.0'}}}), encoding='utf-8')
    report = root / 'artifacts/offline-ui-report.json'
    report.unlink(missing_ok=True)
    subprocess.run([xvfb, '-a', str(kit / 'dotnet'), str(out / 'DataGridScene.Demo.dll')], cwd=root, env=env, check=True, timeout=60)
    result = json.loads(report.read_text(encoding='utf-8'))
    if result.get('Passed') is not True or not result.get('Results'):
        raise SystemExit('The Uno API host did not produce a successful nonempty report.')
    print(f"{result['PassedCount']}/{len(result['Results'])} Uno API contracts passed in the supplementary X11 host.")


if __name__ == '__main__':
    main()
