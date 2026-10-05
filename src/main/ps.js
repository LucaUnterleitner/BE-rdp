'use strict';
// Runs PowerShell scripts for Win32 integration. The script travels as -EncodedCommand;
// data (including secrets) is passed as JSON on stdin so it never appears in a process command line.

const { spawn } = require('node:child_process');
const path = require('node:path');

// Absolute path so that no other powershell.exe on PATH is started.
const POWERSHELL = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe');

function runPowerShell(script, input = null, timeoutMs = 20000) {
  return new Promise((resolve, reject) => {
    const prelude = '$ErrorActionPreference = "Stop"; $ProgressPreference = "SilentlyContinue"; ' +
      '[Console]::OutputEncoding = [Text.Encoding]::UTF8; ' +
      '$in = $null; $raw = [Console]::In.ReadToEnd(); if ($raw) { $in = $raw | ConvertFrom-Json }; ';
    const encoded = Buffer.from(prelude + script, 'utf16le').toString('base64');
    const child = spawn(POWERSHELL, ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded], {
      windowsHide: true,
    });
    let out = '';
    let err = '';
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error('PowerShell timed out'));
    }, timeoutMs);
    child.stdout.on('data', (d) => { out += d.toString('utf8'); });
    child.stderr.on('data', (d) => { err += d.toString('utf8'); });
    child.on('error', (e) => { clearTimeout(timer); reject(e); });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(out.trim());
      else reject(new Error(cleanError(err) || `PowerShell exited with code ${code}`));
    });
    child.stdin.on('error', () => { /* PowerShell exited before reading stdin; reported via close */ });
    child.stdin.end(input === null ? '' : JSON.stringify(input), 'utf8');
  });
}

function cleanError(text) {
  // Keep the first meaningful line of a PowerShell error record.
  const line = String(text).split(/\r?\n/).map((l) => l.trim()).find((l) => l && !l.startsWith('At line') && !l.startsWith('+'));
  return line ? line.replace(/^#< CLIXML/, '').trim() : '';
}

module.exports = { runPowerShell };
