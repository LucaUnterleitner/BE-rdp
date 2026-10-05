'use strict';
const test = require('node:test');
const assert = require('node:assert');
const crypto = require('node:crypto');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const { parseRdg, parseMremote, detectAndParse } = require('../src/main/importers');
const { CentralList, verifyDocument } = require('../src/main/central');
const { normalizeConnection, DEFAULT_SETTINGS } = require('../src/main/store');
const { SessionManager } = require('../src/main/sessions');
const { parseEventXml } = require('../src/main/win32');

const defaults = DEFAULT_SETTINGS.defaults;

// ── RDCMan ────────────────────────────────────────────
const RDG = `<?xml version="1.0" encoding="utf-8"?>
<RDCMan programVersion="2.90" schemaVersion="3"><file><credentialsProfiles/>
 <properties><expanded>True</expanded><name>BP</name></properties>
 <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>jdoe</userName><password>AQAAANCMnd8B</password><domain>BPNET</domain></logonCredentials>
 <group><properties><name>Finance</name></properties>
  <gatewaySettings inherit="None"><enabled>True</enabled><hostName>rdgw.bp.example</hostName><localBypass>True</localBypass></gatewaySettings>
  <server><properties><displayName>Finance Prod</displayName><name>fin-prod.bp.example</name><comment>SAP</comment></properties>
   <connectionSettings inherit="None"><connectToConsole>True</connectToConsole><startProgram>cmd.exe</startProgram><port>3390</port></connectionSettings>
   <remoteDesktop inherit="None"><size>1600 x 900</size><fullScreen>False</fullScreen></remoteDesktop>
   <localResources inherit="None"><redirectClipboard>False</redirectClipboard><redirectDrives>True</redirectDrives><redirectPrinters>True</redirectPrinters></localResources>
  </server>
  <group><properties><name>Test</name></properties>
   <server><properties><name>fin-test.bp.example</name></properties><logonCredentials inherit="FromParent"/></server>
  </group>
 </group></file></RDCMan>`;

test('RDCMan: maps servers, groups, inheritance and drops secrets', () => {
  const { items, warnings } = parseRdg(RDG);
  assert.strictEqual(items.length, 2);
  const [prod, testSrv] = items;
  assert.strictEqual(prod.connection.name, 'Finance Prod');
  assert.strictEqual(prod.connection.host, 'fin-prod.bp.example');
  assert.strictEqual(prod.connection.port, 3390);
  assert.strictEqual(prod.connection.folder, 'Finance');
  assert.strictEqual(prod.connection.username, 'BPNET\\jdoe');
  assert.strictEqual(prod.connection.security.adminSession, true);
  assert.deepStrictEqual(prod.connection.gateway, { mode: 'detect', host: 'rdgw.bp.example' });
  assert.strictEqual(prod.connection.display.mode, 'window');
  assert.strictEqual(prod.connection.display.width, 1600);
  assert.strictEqual(prod.connection.redirect.clipboard, false);
  assert.strictEqual(prod.connection.redirect.drives, 'none');
  assert.strictEqual(prod.connection.redirect.printers, true);
  assert.ok(prod.warnings.some((w) => w.includes('password')));
  assert.ok(prod.warnings.some((w) => w.includes('start program')));
  assert.ok(prod.warnings.some((w) => w.includes('drive')));
  assert.ok(!JSON.stringify(items).includes('AQAAANCMnd8B'), 'password never leaves the parser');
  assert.strictEqual(testSrv.connection.folder, 'Finance / Test');
  assert.strictEqual(testSrv.connection.username, 'BPNET\\jdoe', 'credentials inherited from the file');
  assert.strictEqual(testSrv.connection.gateway.host, 'rdgw.bp.example', 'gateway inherited from the group');
  assert.ok(warnings.length >= 1);
  for (const it of items) normalizeConnection(it.connection, defaults);
});

test('XML import rejects DTDs and unknown formats', () => {
  assert.throws(() => detectAndParse('<!DOCTYPE x [<!ENTITY a "b">]><RDCMan><file/></RDCMan>'), /document type/);
  assert.throws(() => detectAndParse('<foo/>'), /not supported/);
});

// ── mRemoteNG ─────────────────────────────────────────
const MR = `<?xml version="1.0" encoding="utf-8"?>
<Connections Name="Connections" EncryptionEngine="AES" BlockCipherMode="GCM" FullFileEncryption="false" ConfVersion="2.6">
  <Node Name="Servers" Type="Container" Username="admin" Domain="CORP" Port="3389">
    <Node Name="App 1" Type="Connection" Protocol="RDP" Hostname="app1.corp.example" Port="3391" Username="" InheritUsername="true" InheritDomain="true" Password="secretblob" Resolution="Res1280x720" RedirectClipboard="true" RedirectDiskDrives="true" RDGatewayUsageMethod="Always" RDGatewayHostname="gw.corp.example" UseConsoleSession="true" RDPAuthenticationLevel="AuthRequired" />
    <Node Name="Linux" Type="Connection" Protocol="SSH2" Hostname="linux.corp.example" />
  </Node>
</Connections>`;

test('mRemoteNG: maps RDP nodes, resolves inheritance, skips other protocols, drops passwords', () => {
  const { items, warnings } = parseMremote(MR);
  assert.strictEqual(items.length, 1);
  const c = items[0].connection;
  assert.strictEqual(c.host, 'app1.corp.example');
  assert.strictEqual(c.port, 3391);
  assert.strictEqual(c.username, 'CORP\\admin');
  assert.strictEqual(c.folder, 'Servers');
  assert.strictEqual(c.display.width, 1280);
  assert.deepStrictEqual(c.gateway, { mode: 'always', host: 'gw.corp.example' });
  assert.strictEqual(c.redirect.drives, 'none');
  assert.strictEqual(c.security.authLevel, 1);
  assert.ok(!JSON.stringify(items).includes('secretblob'));
  assert.ok(warnings.some((w) => w.includes('skipped')));
  assert.throws(() => parseMremote('<Connections FullFileEncryption="true"></Connections>'), /encrypted/);
});

// ── Central list ──────────────────────────────────────
function signed(doc, privateKey) {
  const bytes = Buffer.from(JSON.stringify(doc));
  return { bytes, signature: crypto.sign(null, bytes, privateKey).toString('base64') };
}

test('central list: signature, rollback protection and offline cache', async () => {
  const { publicKey, privateKey } = crypto.generateKeyPairSync('ed25519');
  const pub = publicKey.export({ format: 'der', type: 'spki' }).toString('base64');
  const id = crypto.randomUUID();
  const docV2 = { schema: 'bp-rdp-systems/1', version: 2, systems: [{ id, name: 'Central A', host: 'central-a.example', environment: 'Production' }, { id: 'not-a-uuid', host: 'x' }] };
  const v2 = signed(docV2, privateKey);

  assert.ok(verifyDocument(v2.bytes, v2.signature, pub));
  const tampered = Buffer.from(v2.bytes.toString().replace('central-a', 'evil-host'));
  assert.throws(() => verifyDocument(tampered, v2.signature, pub), /signature/);

  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bp-central-'));
  fs.writeFileSync(path.join(dir, 'systems.json'), v2.bytes);
  fs.writeFileSync(path.join(dir, 'systems.json.sig'), v2.signature);
  const cacheDir = path.join(dir, 'cache');
  const list = new CentralList({ config: { path: path.join(dir, 'systems.json'), publicKey: pub }, cacheDir, defaults });
  await list.refresh();
  assert.strictEqual(list.info.state, 'current');
  assert.strictEqual(list.systems.length, 1, 'invalid ids are skipped');
  assert.strictEqual(list.systems[0].id, `central-${id}`);
  assert.strictEqual(list.systems[0].source, 'central');

  // Older version on the server is rejected; the cached v2 stays in use.
  const v1 = signed({ ...docV2, version: 1 }, privateKey);
  fs.writeFileSync(path.join(dir, 'systems.json'), v1.bytes);
  fs.writeFileSync(path.join(dir, 'systems.json.sig'), v1.signature);
  await list.refresh();
  assert.strictEqual(list.info.state, 'offline');
  assert.strictEqual(list.info.version, 2);
  assert.match(list.info.error, /older/);

  // Source unavailable: cached copy is used.
  fs.rmSync(path.join(dir, 'systems.json'));
  await list.refresh();
  assert.strictEqual(list.info.state, 'offline');
  assert.strictEqual(list.systems.length, 1);
  fs.rmSync(dir, { recursive: true, force: true });
});

// ── Ids ───────────────────────────────────────────────
test('connection ids must be plain UUIDs', () => {
  const evil = normalizeConnection({ id: '"><img src=x onerror=alert(1)>', host: 'x' }, defaults);
  assert.match(evil.id, /^[0-9a-f-]{36}$/);
  const ok = crypto.randomUUID();
  assert.strictEqual(normalizeConnection({ id: ok, host: 'x' }, defaults).id, ok);
  assert.throws(() => normalizeConnection({ host: 'x', gateway: { mode: 'always', host: 'gw host' } }, defaults), /Gateway/);
  assert.strictEqual(normalizeConnection({ host: 'x', lastConnectedAt: 5 }, defaults).lastConnectedAt, null);
});

// ── Session state machine ─────────────────────────────
function fakeSession(sm, state) {
  const s = { id: 'S', pid: 42, state, startedAt: new Date(Date.now() - 1000).toISOString(), connectedAt: null, verified: false, result: null, lastChange: Date.now() };
  sm.sessions.set('S', s);
  sm.children.set('S', { kill() { s.killed = true; } });
  return s;
}
const ev = (id, reason) => ({ id, pid: 42, time: new Date().toISOString(), props: reason === undefined ? [] : ['Disconnect Reason', String(reason), 'Info'] });

test('session state machine', () => {
  const sm = new SessionManager({ tmpDir: path.join(os.tmpdir(), 'bp-sm-test'), focusWindow() {} });

  let s = fakeSession(sm, 'connecting');
  sm.applyEvents(s, [ev(1024), ev(1027)]);
  assert.strictEqual(s.state, 'active');
  assert.strictEqual(s.verified, true);

  s = fakeSession(sm, 'active');
  sm.applyEvents(s, [ev(1027), ev(1026, 1)]);
  assert.strictEqual(s.state, 'active', 'a normal close is not an interruption');
  sm.applyEvents(s, [ev(1027), ev(1026, 0x904)]);
  assert.strictEqual(s.state, 'reconnecting');
  sm.applyEvents(s, [ev(1027), ev(1026, 0x904), ev(1027)]);
  assert.strictEqual(s.state, 'active', 'auto-reconnect recovers');

  s = fakeSession(sm, 'connecting');
  sm.applyEvents(s, [ev(1024), ev(1026, 0x807)]);
  assert.strictEqual(s.state, 'connecting', 'wrong password: mstsc lets the user retry');
  assert.ok(s.result && s.result.hint);
  assert.ok(!s.killed);

  s = fakeSession(sm, 'connecting');
  sm.applyEvents(s, [ev(1024), ev(1026, 0x104)]);
  assert.strictEqual(s.state, 'failed');
  assert.ok(s.killed, 'fatal errors close the mstsc error box');

  s = fakeSession(sm, 'active');
  s.verified = false;
  sm.applyEvents(s, [ev(1027)]);
  assert.strictEqual(s.verified, true, 'late confirmation upgrades an unverified session');
});

test('event XML parsing', () => {
  const xml = `<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Microsoft-Windows-TerminalServices-ClientActiveXCore'/><EventID>1026</EventID><TimeCreated SystemTime='2026-10-05T06:13:06.0213Z'/><Execution ProcessID='1948' ThreadID='1'/></System><EventData><Data Name='Name'>Disconnect Reason</Data><Data Name='Value'>260</Data><Data Name='CustomLevel'>Info</Data></EventData></Event>`;
  assert.deepStrictEqual(parseEventXml(xml), { id: 1026, pid: 1948, time: '2026-10-05T06:13:06.021Z', props: ['Disconnect Reason', '260', 'Info'] });
});
