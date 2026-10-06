'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');

const { RdpHost } = require('../src/main/embedded');

const exe = path.join(__dirname, '..', 'resources', 'bin', 'BpRdpHost.exe');
const skip = process.platform !== 'win32' || !fs.existsSync(exe) ? 'helper not built (npm run build:helper)' : false;

function collect(host, type, n, ms = 8000) {
  return new Promise((resolve, reject) => {
    const got = [];
    const t = setTimeout(() => reject(new Error(`timeout waiting for ${type}`)), ms);
    host.on(type, (d) => { got.push(d); if (got.length >= n) { clearTimeout(t); resolve(got); } });
  });
}

test('session helper refuses unknown commands, foreign windows and invalid hosts', { skip }, async () => {
  const host = new RdpHost(exe);
  try {
    await host.whenReady();
    const errors = collect(host, 'error', 4);
    host.send('setProperty', { name: 'ClearTextPassword', value: 'x' });
    host.send('attach', { parent: 65552, x: 0, y: 0, w: 100, h: 100, visible: true }); // desktop window, not ours
    host.send('connect', { host: '-evil;calc' });
    host.send('connect', { host: 'ok.example', username: 'a"b' });
    assert.deepStrictEqual(await errors, ['unknown command', 'parent window does not belong to the app', 'invalid host', 'invalid username']);
  } finally {
    host.kill();
  }
});

test('session helper reports a failed connection with the reason code', { skip }, async () => {
  const host = new RdpHost(exe);
  try {
    await host.whenReady();
    const done = collect(host, 'disconnected', 1, 30000);
    host.send('connect', { host: 'bp-rdp-selftest.invalid', port: 3389, width: 800, height: 600 });
    const [d] = await done;
    assert.ok(Number.isInteger(d.reason) && d.reason > 3, `reason ${d.reason}`);
  } finally {
    host.kill();
  }
});
