'use strict';
const test = require('node:test');
const assert = require('node:assert');

const { normalizeConnection, DEFAULT_SETTINGS } = require('../src/main/store');
const { sshArgs, sshCommandLine, webTarget } = require('../src/main/launchers');
const { parseTargets, webUrl } = require('../src/renderer/js/targets.js');

const D = DEFAULT_SETTINGS.defaults;
const norm = (c) => normalizeConnection(c, D);

test('existing records without a type stay RDP', () => {
  const c = norm({ host: 'srv01' });
  assert.strictEqual(c.protocol, 'rdp');
  assert.strictEqual(c.port, 3389);
});

test('default ports follow the type', () => {
  assert.strictEqual(norm({ host: 'h', protocol: 'ssh' }).port, 22);
  assert.strictEqual(norm({ host: 'h', protocol: 'web' }).port, 443);
  assert.strictEqual(norm({ host: 'h', protocol: 'web', web: { scheme: 'http' } }).port, 80);
});

test('unknown types are rejected instead of being started as RDP', () => {
  assert.throws(() => norm({ host: 'h', protocol: 'vnc' }), /not supported/);
});

test('hosts that look like options or contain shell characters are rejected', () => {
  for (const host of ['-oProxyCommand=calc', '.hidden', 'a;calc', 'a&b', 'a|b', '$(x)', '`x`', 'a b', 'a\nb', 'a"b']) {
    assert.throws(() => norm({ host, protocol: 'ssh' }), Error, host);
  }
});

test('SSH user names are strict', () => {
  assert.strictEqual(norm({ host: 'h', protocol: 'ssh', username: 'CORP\\alice' }).username, 'CORP\\alice');
  assert.strictEqual(norm({ host: 'h', protocol: 'ssh', username: 'alice@corp.example' }).username, 'alice@corp.example');
  for (const username of ['-oProxyCommand=x', 'a;b', 'a b', 'a$(x)', 'a%PATH%', 'a&b', 'trail\\']) {
    assert.throws(() => norm({ host: 'h', protocol: 'ssh', username }), Error, username);
  }
});

test('SSH key file and jump host are validated', () => {
  assert.throws(() => norm({ host: 'h', protocol: 'ssh', ssh: { identityFile: 'id_rsa' } }), /full path/);
  assert.throws(() => norm({ host: 'h', protocol: 'ssh', ssh: { identityFile: 'C:\\keys\\a&calc' } }), /full path/);
  assert.throws(() => norm({ host: 'h', protocol: 'ssh', ssh: { jumpHost: 'a;b' } }), /jump host/);
  assert.throws(() => norm({ host: 'h', protocol: 'ssh', ssh: { jumpHost: '-oProxyCommand=x' } }), /jump host/);
  assert.strictEqual(norm({ host: 'h', protocol: 'ssh', ssh: { jumpHost: 'bob@bastion.corp:2222' } }).ssh.jumpHost, 'bob@bastion.corp:2222');
});

test('ssh arguments end options with -- before the host', () => {
  const c = norm({ host: 'srv01', protocol: 'ssh', port: 2222, username: 'alice', ssh: { jumpHost: 'bastion' } });
  assert.deepStrictEqual(sshArgs(c), ['-l', 'alice', '-p', '2222', '-J', 'bastion', '--', 'srv01']);
  assert.deepStrictEqual(sshArgs(c, { ssh: { strictHostKeyChecking: 'yes' } }).slice(-4), ['-o', 'StrictHostKeyChecking=yes', '--', 'srv01']);
});

test('the cmd.exe command line contains no characters cmd interprets in values', () => {
  const c = norm({ host: 'srv01', protocol: 'ssh', username: 'CORP\\alice' });
  const { commandLine } = sshCommandLine(c, {});
  assert.match(commandLine, /\/d \/v:off \/s \/c ""/);
  assert.match(commandLine, /-l CORP\\alice -p 22 -- srv01 & if errorlevel 255/);
  // Validation bypassed on purpose: the launcher still refuses.
  assert.throws(() => sshArgs({ ...c, ssh: { jumpHost: 'a&calc' } }), /not allowed/);
});

test('web targets are http(s) only and built from fields', () => {
  const c = norm({ host: 'ilo01.corp', protocol: 'web', port: 8443, web: { path: '/admin?x=1' } });
  assert.strictEqual(webTarget(c), 'https://ilo01.corp:8443/admin?x=1');
  assert.throws(() => norm({ host: 'h', protocol: 'web', web: { path: 'javascript:alert(1)' } }), /path/);
  assert.throws(() => norm({ host: 'h', protocol: 'web', web: { path: '/a b' } }), /path/);
  assert.strictEqual(webUrl({ host: 'fe80::1', port: 443, web: {} }), 'https://[fe80::1]');
});

test('typed addresses are detected', () => {
  const first = (s) => { const t = parseTargets(s)[0]; return t && [t.protocol, t.host, t.port, t.username]; };
  assert.deepStrictEqual(first('srv01'), ['rdp', 'srv01', 3389, '']);
  assert.deepStrictEqual(parseTargets('srv01').map((t) => t.protocol), ['rdp', 'ssh', 'web']);
  assert.deepStrictEqual(first('srv01:22'), ['ssh', 'srv01', 22, '']);
  assert.deepStrictEqual(first('alice@srv01'), ['ssh', 'srv01', 22, 'alice']);
  assert.deepStrictEqual(first('ssh -p 2222 alice@srv01'), ['ssh', 'srv01', 2222, 'alice']);
  assert.deepStrictEqual(first('ssh://bob@10.0.0.5:2200'), ['ssh', '10.0.0.5', 2200, 'bob']);
  assert.deepStrictEqual(first('https://ilo01/admin'), ['web', 'ilo01', 443, '']);
  assert.deepStrictEqual(first('rdp srv01'), ['rdp', 'srv01', 3389, '']);
  for (const bad of ['-oProxyCommand=calc', 'ssh -oProxyCommand=x host', 'a;calc', 'user$(x)@h', 'file:///c:/x', 'javascript:alert(1)']) {
    assert.deepStrictEqual(parseTargets(bad), [], bad);
  }
});
