'use strict';
// Builds and parses Microsoft .rdp files (format: "name:type:value" per line).
// Reference: https://learn.microsoft.com/azure/virtual-desktop/rdp-properties

const GATEWAY_USAGE = { none: 0, always: 1, detect: 2 };
const AUDIO_MODE = { local: 0, remote: 1, none: 2 };

/** Apply IT policy locks on top of a connection's settings. Returns a new object. */
function applyPolicy(conn, policy) {
  const out = structuredClone(conn);
  const lock = (policy && policy.redirect) || {};
  for (const [key, value] of Object.entries(lock)) {
    if (out.redirect && key in out.redirect) out.redirect[key] = value;
  }
  if (policy && policy.requireCredentialProtection && out.security.credentialProtection === 'none') {
    out.security.credentialProtection = policy.requireCredentialProtection;
  }
  return out;
}

function splitHost(host, port) {
  const h = String(host || '').trim();
  return { host: h, port: Number(port) || 3389 };
}

/** Produce .rdp text for a connection. No secrets are ever written. */
function buildRdp(conn) {
  const { host, port } = splitHost(conn.host, conn.port);
  const d = conn.display || {};
  const r = conn.redirect || {};
  const g = conn.gateway || {};
  const s = conn.security || {};
  const lines = [];
  // Values never contain line breaks or control characters, so no extra .rdp properties can be injected.
  const add = (name, type, value) => lines.push(`${name}:${type}:${String(value).replace(/[\u0000-\u001f\u007f]/g, '')}`);

  add('full address', 's', formatAddress(host, port));
  if (conn.username && !conn.promptAlways) add('username', 's', conn.username);
  add('prompt for credentials on client', 'i', 1);
  if (conn.promptAlways) add('prompt for credentials', 'i', 1);
  add('enablecredsspsupport', 'i', 1);
  add('authentication level', 'i', Number.isInteger(s.authLevel) ? s.authLevel : 2);
  add('negotiate security layer', 'i', 1);
  add('administrative session', 'i', s.adminSession ? 1 : 0);
  add('disableconnectionsharing', 'i', 0);
  add('autoreconnection enabled', 'i', 1);
  add('bandwidthautodetect', 'i', 1);
  add('networkautodetect', 'i', 1);
  add('compression', 'i', 1);

  // Display
  add('screen mode id', 'i', d.mode === 'window' ? 1 : 2);
  add('use multimon', 'i', d.multimon ? 1 : 0);
  if (d.mode === 'window' && d.width && d.height) {
    add('desktopwidth', 'i', clamp(d.width, 200, 8192));
    add('desktopheight', 'i', clamp(d.height, 200, 8192));
  }
  add('dynamic resolution', 'i', d.dynamicResolution === false ? 0 : 1);
  add('smart sizing', 'i', d.smartSizing ? 1 : 0);
  add('session bpp', 'i', 32);

  // Redirection (least privilege defaults)
  add('redirectclipboard', 'i', r.clipboard ? 1 : 0);
  add('drivestoredirect', 's', r.drives === 'all' ? '*' : '');
  add('redirectprinters', 'i', r.printers ? 1 : 0);
  add('audiomode', 'i', AUDIO_MODE[r.audio] ?? 0);
  add('audiocapturemode', 'i', r.microphone ? 1 : 0);
  add('redirectsmartcards', 'i', r.smartcards ? 1 : 0);
  add('redirectcomports', 'i', 0);
  add('redirectlocation', 'i', 0);
  add('keyboardhook', 'i', 2);

  // Gateway
  const usage = GATEWAY_USAGE[g.mode] ?? 0;
  if (usage && g.host) {
    add('gatewayhostname', 's', g.host);
    add('gatewayusagemethod', 'i', usage);
    add('gatewaycredentialssource', 'i', 4);
    add('gatewayprofileusagemethod', 'i', 1);
    add('promptcredentialonce', 'i', 1);
  } else {
    add('gatewayusagemethod', 'i', 4);
    add('gatewayprofileusagemethod', 'i', 0);
  }
  return lines.join('\r\n') + '\r\n';
}

/** Encode as UTF-16 LE with BOM, the encoding mstsc writes. */
function encodeRdp(text) {
  return Buffer.concat([Buffer.from([0xff, 0xfe]), Buffer.from(text, 'utf16le')]);
}

function decodeRdp(buffer) {
  if (buffer[0] === 0xff && buffer[1] === 0xfe) return buffer.subarray(2).toString('utf16le');
  if (buffer[0] === 0xef && buffer[1] === 0xbb && buffer[2] === 0xbf) return buffer.subarray(3).toString('utf8');
  return buffer.toString('utf8');
}

/** Parse .rdp text into a property map. */
function parseRdpProperties(text) {
  const props = {};
  for (const raw of text.split(/\r?\n/)) {
    const m = /^([^:]+):([sib]):(.*)$/.exec(raw.trim());
    if (!m) continue;
    const [, name, type, value] = m;
    props[name.toLowerCase()] = type === 'i' ? Number.parseInt(value, 10) : value;
  }
  return props;
}

/**
 * Convert an imported .rdp file into a connection draft plus warnings.
 * Imported files are untrusted: stored passwords and alternate shells are dropped.
 */
function rdpToConnection(text, fileName) {
  const p = parseRdpProperties(text);
  const warnings = [];
  const address = String(p['full address'] || '');
  const { host, port } = parseAddress(address, p['server port']);

  if (p['password 51']) warnings.push('A stored password was found in the file and was discarded.');
  if (p['alternate shell'] || p['remoteapplicationprogram']) warnings.push('A start program (alternate shell / RemoteApp) was found and was not imported.');
  if (p['drivestoredirect']) warnings.push(`The file requested local drive access (${p['drivestoredirect']}). It is imported as switched off.`);
  if (p['authentication level'] === 0) warnings.push('The file disabled server identity checks. The app uses "warn" instead.');
  if (p['enablecredsspsupport'] === 0) warnings.push('The file disabled Network Level Authentication. NLA stays on.');

  const usage = p['gatewayusagemethod'];
  const gatewayMode = usage === 1 ? 'always' : usage === 2 ? 'detect' : 'none';
  const audioByValue = { 0: 'local', 1: 'remote', 2: 'none' };

  const conn = {
    name: (fileName || host).replace(/\.rdp$/i, ''),
    host,
    port,
    username: p['username'] || '',
    display: {
      mode: p['screen mode id'] === 1 ? 'window' : 'fullscreen',
      width: p['desktopwidth'] || 1920,
      height: p['desktopheight'] || 1080,
      multimon: p['use multimon'] === 1,
      dynamicResolution: p['dynamic resolution'] !== 0,
      smartSizing: p['smart sizing'] === 1,
    },
    redirect: {
      clipboard: p['redirectclipboard'] !== 0,
      drives: 'none',
      printers: p['redirectprinters'] === 1,
      audio: audioByValue[p['audiomode']] || 'local',
      microphone: p['audiocapturemode'] === 1,
      smartcards: p['redirectsmartcards'] === 1,
    },
    gateway: { mode: gatewayMode, host: p['gatewayhostname'] || '' },
    security: {
      adminSession: p['administrative session'] === 1,
      credentialProtection: 'none',
      authLevel: p['authentication level'] === 1 ? 1 : 2,
    },
  };
  if (!host) warnings.push('The file does not contain a computer address.');
  return { connection: conn, warnings };
}

/** Command-line arguments for mstsc.exe when launching without a file (no security dialog). */
function buildDirectArgs(conn) {
  const { host, port } = splitHost(conn.host, conn.port);
  const d = conn.display || {};
  const s = conn.security || {};
  const args = [`/v:${formatAddress(host, port)}`];
  if (d.mode === 'window') {
    if (d.width && d.height) args.push(`/w:${clamp(d.width, 200, 8192)}`, `/h:${clamp(d.height, 200, 8192)}`);
  } else {
    args.push('/f');
  }
  if (d.multimon) args.push('/multimon');
  if (s.adminSession) args.push('/admin');
  if (conn.gateway && conn.gateway.mode !== 'none' && conn.gateway.host) args.push(`/g:${conn.gateway.host}`);
  if (conn.promptAlways) args.push('/prompt');
  return args.concat(protectionArgs(conn));
}

/** host:port, with IPv6 literals in brackets. */
function formatAddress(host, port) {
  const h = host.includes(':') && !host.startsWith('[') ? `[${host}]` : host;
  return port === 3389 ? h : `${h}:${port}`;
}

/** Parse "host", "host:port", "[v6]:port" or a bare IPv6 literal. */
function parseAddress(address, serverPort) {
  const a = String(address || '').trim();
  const fallback = Number(serverPort) || 3389;
  const bracket = /^\[([^\]]+)\](?::(\d+))?$/.exec(a);
  if (bracket) return { host: bracket[1], port: bracket[2] ? Number(bracket[2]) : fallback };
  if ((a.match(/:/g) || []).length > 1) return { host: a, port: fallback };
  const m = /^(.*?)(?::(\d+))?$/.exec(a);
  return { host: m[1], port: m[2] ? Number(m[2]) : fallback };
}

function protectionArgs(conn) {
  const p = conn.security && conn.security.credentialProtection;
  if (p === 'remoteGuard') return ['/remoteGuard'];
  if (p === 'restrictedAdmin') return ['/restrictedAdmin'];
  return [];
}

function clamp(v, min, max) {
  return Math.min(max, Math.max(min, Math.round(Number(v) || 0)));
}

module.exports = {
  applyPolicy, buildRdp, encodeRdp, decodeRdp, parseRdpProperties,
  rdpToConnection, buildDirectArgs, protectionArgs, splitHost, parseAddress, formatAddress,
};
