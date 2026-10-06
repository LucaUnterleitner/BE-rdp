'use strict';
// Compiles native/BpRdpHost.cs with the C# compiler that ships with Windows (.NET Framework 4.x).
// No Visual Studio or .NET SDK is needed. Output: resources/bin/BpRdpHost.exe (packaged as an extra resource).

const { execFileSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const root = path.join(__dirname, '..');
const src = path.join(root, 'native', 'BpRdpHost.cs');
const manifest = path.join(root, 'native', 'BpRdpHost.manifest');
const outDir = path.join(root, 'resources', 'bin');
const out = path.join(outDir, 'BpRdpHost.exe');
const csc = path.join(process.env.SystemRoot || 'C:\\Windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe');

if (process.platform !== 'win32') {
  console.log('build-helper: skipped (Windows only)');
  process.exit(0);
}
if (fs.existsSync(out) && fs.statSync(out).mtimeMs > Math.max(fs.statSync(src).mtimeMs, fs.statSync(manifest).mtimeMs, fs.statSync(__filename).mtimeMs)) {
  console.log('build-helper: up to date');
  process.exit(0);
}
fs.mkdirSync(outDir, { recursive: true });
execFileSync(csc, [
  '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
  `/win32manifest:${manifest}`, `/out:${out}`,
  '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll',
  '/r:Microsoft.CSharp.dll', '/r:System.Web.Extensions.dll',
  src,
], { stdio: 'inherit' });
console.log(`build-helper: ${path.relative(root, out)}`);
