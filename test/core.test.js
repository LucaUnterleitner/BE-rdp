'use strict';
const test = require('node:test');
const assert = require('node:assert');
const net = require('node:net');

const { buildRdp, encodeRdp, decodeRdp, parseRdpProperties, rdpToConnection, buildDirectArgs, applyPolicy } = require('../src/main/rdpfile');
const { explainDisconnect, explainProbe } = require('../src/main/errors');
const { reasonOf } = require('../src/main/sessions');
const { normalizeConnection, mergeDeep, DEFAULT_SETTINGS } = require('../src/main/store');
const { parseAddress } = require('../src/main/rdpfile');
const { probe } = require('../src/main/probe');

const base = () => normalizeConnection({ host: 'srv01.corp.local', username: 'CORP\\jdoe' }, DEFAULT_SETTINGS.defaults);

test('buildRdp writes secure defaults and no password', () => {
  const props = parseRdpProperties(buildRdp(base()));
  assert.strictEqual(props['full address'], 'srv01.corp.local');
  assert.strictEqual(props['username'], 'CORP\\jdoe');
  assert.strictEqual(props['enablecredsspsupport'], 1);
  assert.strictEqual(props['authentication level'], 2);
  assert.strictEqual(props['drivestoredirect'], '');
  assert.strictEqual(props['screen mode id'], 2);
  assert.ok(!('password 51' in props));
});

test('buildRdp handles port, window mode and gateway', () => {
  const c = base();
  c.port = 3390;
  c.display = { ...c.display, mode: 'window', width: 1600, height: 900 };
  c.gateway = { mode: 'always', host: 'rdgw.corp.com' };
  c.redirect.drives = 'all';
  const p = parseRdpProperties(buildRdp(c));
  assert.strictEqual(p['full address'], 'srv01.corp.local:3390');
  assert.strictEqual(p['screen mode id'], 1);
  assert.strictEqual(p['desktopwidth'], 1600);
  assert.strictEqual(p['gatewayhostname'], 'rdgw.corp.com');
  assert.strictEqual(p['gatewayusagemethod'], 1);
  assert.strictEqual(p['drivestoredirect'], '*');
});

test('encode/decode round trip uses UTF-16 LE with BOM', () => {
  const buf = encodeRdp('full address:s:höst\r\n');
  assert.deepStrictEqual([...buf.subarray(0, 2)], [0xff, 0xfe]);
  assert.strictEqual(decodeRdp(buf), 'full address:s:höst\r\n');
});

test('imported .rdp files are treated as untrusted', () => {
  const text = 'full address:s:evil.example.com:4489\r\npassword 51:b:01000000D08C\r\nalternate shell:s:cmd.exe\r\ndrivestoredirect:s:*\r\nenablecredsspsupport:i:0\r\nauthentication level:i:0\r\nusername:s:bob\r\n';
  const { connection, warnings } = rdpToConnection(text, 'evil.rdp');
  assert.strictEqual(connection.host, 'evil.example.com');
  assert.strictEqual(connection.port, 4489);
  assert.strictEqual(connection.redirect.drives, 'none');
  assert.strictEqual(connection.security.authLevel, 2);
  assert.strictEqual(warnings.length, 5);
  const rebuilt = parseRdpProperties(buildRdp(normalizeConnection(connection, DEFAULT_SETTINGS.defaults)));
  assert.ok(!('alternate shell' in rebuilt));
  assert.strictEqual(rebuilt['enablecredsspsupport'], 1);
});

test('direct mstsc arguments', () => {
  const c = base();
  c.display.multimon = true;
  c.security.adminSession = true;
  c.security.credentialProtection = 'remoteGuard';
  c.gateway = { mode: 'detect', host: 'gw' };
  assert.deepStrictEqual(buildDirectArgs(c), ['/v:srv01.corp.local', '/f', '/multimon', '/admin', '/g:gw', '/remoteGuard']);
});

test('policy locks override connection settings', () => {
  const c = base();
  c.redirect.drives = 'all';
  c.redirect.clipboard = true;
  const out = applyPolicy(c, { redirect: { drives: 'none', clipboard: false } });
  assert.strictEqual(out.redirect.drives, 'none');
  assert.strictEqual(out.redirect.clipboard, false);
  assert.strictEqual(c.redirect.drives, 'all', 'input not mutated');
});

test('disconnect reasons map to friendly text', () => {
  assert.strictEqual(explainDisconnect(260).title, 'Computer name not found');
  assert.strictEqual(explainDisconnect(0x204).code, '0x204');
  assert.strictEqual(explainDisconnect(1).kind, 'normal');
  assert.strictEqual(explainDisconnect(0x9999).code, '0x9999');
  assert.strictEqual(explainDisconnect(null), null);
  assert.ok(explainProbe('timeout').message.includes('VPN'));
});

test('disconnect reason is read from event properties', () => {
  assert.strictEqual(reasonOf({ id: 1026, props: ['Disconnect Reason', '2308', 'Info'] }), 2308);
  assert.strictEqual(reasonOf({ id: 1026, props: ['x'] }), null);
});

test('connection validation rejects bad input', () => {
  assert.throws(() => normalizeConnection({ host: '' }, DEFAULT_SETTINGS.defaults), /computer name/);
  assert.throws(() => normalizeConnection({ host: 'a b' }, DEFAULT_SETTINGS.defaults), /not allowed/);
  assert.throws(() => normalizeConnection({ host: 'x', port: 70000 }, DEFAULT_SETTINGS.defaults), /port/);
  assert.throws(() => normalizeConnection({ host: 'x', gateway: { mode: 'always', host: '' } }, DEFAULT_SETTINGS.defaults), /Gateway/);
  const c = normalizeConnection({ host: 'x', tags: 'a, b ,,c', environment: 'Nope' }, DEFAULT_SETTINGS.defaults);
  assert.deepStrictEqual(c.tags, ['a', 'b', 'c']);
  assert.strictEqual(c.environment, 'Internal');
});

test('probe reports reachable and refused ports', async () => {
  const server = net.createServer((s) => s.end());
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const { port } = server.address();
  const ok = await probe('127.0.0.1', port, 2000);
  assert.strictEqual(ok.reachable, true);
  server.close();
  await new Promise((r) => server.once('close', r));
  const refused = await probe('127.0.0.1', port, 2000);
  assert.strictEqual(refused.reachable, false);
  assert.strictEqual(refused.reason, 'refused');
  const dns = await probe('does-not-exist.invalid', 3389, 3000);
  assert.strictEqual(dns.reason, 'dns');
});

test('control characters cannot inject .rdp properties', () => {
  const c = base();
  c.username = 'bob\r\nalternate shell:s:cmd.exe';
  const text = buildRdp(c);
  assert.ok(!/^alternate shell/m.test(text));
  const n = normalizeConnection({ host: 'x', username: 'a\nb' }, DEFAULT_SETTINGS.defaults);
  assert.strictEqual(n.username, 'ab');
});

test('sign in as a different user', () => {
  const c = { ...base(), promptAlways: true };
  const p = parseRdpProperties(buildRdp(c));
  assert.strictEqual(p['prompt for credentials'], 1);
  assert.ok(!('username' in p));
  assert.ok(buildDirectArgs(c).includes('/prompt'));
});

test('IPv6 and ports are parsed correctly', () => {
  assert.deepStrictEqual(parseAddress('fe80::1'), { host: 'fe80::1', port: 3389 });
  assert.deepStrictEqual(parseAddress('[fe80::1]:3390'), { host: 'fe80::1', port: 3390 });
  assert.deepStrictEqual(parseAddress('srv:4000'), { host: 'srv', port: 4000 });
  const v6 = normalizeConnection({ host: 'fe80::1', port: 3390 }, DEFAULT_SETTINGS.defaults);
  assert.strictEqual(parseRdpProperties(buildRdp(v6))['full address'], '[fe80::1]:3390');
  assert.throws(() => normalizeConnection({ host: 'srv:3390' }, DEFAULT_SETTINGS.defaults), /Port field/);
});

test('settings merge ignores prototype keys', () => {
  const target = {};
  mergeDeep(target, JSON.parse('{"__proto__": {"polluted": true}, "a": {"constructor": {"x": 1}}}'));
  assert.strictEqual({}.polluted, undefined);
  assert.strictEqual(Object.prototype.polluted, undefined);
});
