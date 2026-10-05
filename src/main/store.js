'use strict';
// Local persistence. Connection metadata and settings are JSON files in %APPDATA%\BearingPoint\RdpClient.
// No secrets are written here; passwords live only in Windows Credential Manager.

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const os = require('node:os');

const DEFAULT_SETTINGS = {
  launchMode: 'file',            // 'file' (all settings, Windows security confirmation) | 'direct' (mstsc /v:, no dialog)
  signingThumbprint: '',         // SHA-256 thumbprint of an internal code-signing certificate for rdpsign
  statusRefreshSeconds: 60,
  confirmDisconnect: true,
  showConnectDialog: true,
  startWithWindows: false,
  keepRunningInTray: true,
  defaults: {
    display: { mode: 'fullscreen', width: 1920, height: 1080, multimon: false, dynamicResolution: true, smartSizing: false },
    redirect: { clipboard: true, drives: 'none', printers: false, audio: 'local', microphone: false, smartcards: false },
    gateway: { mode: 'none', host: '' },
    security: { adminSession: false, credentialProtection: 'none', authLevel: 2 },
  },
};

// Ids end up in HTML attributes and in jump list command lines, so only plain UUIDs are accepted.
const ID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const HOST_PATTERN = /^[A-Za-z0-9._-]+$/;

class Store {
  constructor(dir) {
    this.dir = dir;
    fs.mkdirSync(dir, { recursive: true });
    this.connectionsFile = path.join(dir, 'connections.json');
    this.settingsFile = path.join(dir, 'settings.json');
    this.auditFile = path.join(dir, 'audit.log');
    this.centralStateFile = path.join(dir, 'central-state.json');
    this.centralState = readJson(this.centralStateFile, null) || { favorites: {}, lastConnectedAt: {} };
    this.corrupt = null;
    this.connections = this.loadConnections();
    this.settings = mergeDeep(structuredClone(DEFAULT_SETTINGS), readJson(this.settingsFile, {}));
  }

  /** A damaged file is kept as a copy instead of being overwritten by the next save. */
  loadConnections() {
    if (!fs.existsSync(this.connectionsFile)) return [];
    try {
      const data = JSON.parse(fs.readFileSync(this.connectionsFile, 'utf8'));
      if (!Array.isArray(data)) throw new Error('Unexpected file format');
      return data;
    } catch (err) {
      const backup = `${this.connectionsFile}.corrupt-${Date.now()}`;
      try { fs.renameSync(this.connectionsFile, backup); } catch { /* keep going with an empty list */ }
      this.corrupt = { backup, error: String(err.message || err) };
      return [];
    }
  }

  acknowledgeCorrupt() {
    this.corrupt = null;
  }

  listConnections() {
    return this.connections;
  }

  getConnection(id) {
    return this.connections.find((c) => c.id === id);
  }

  saveConnection(input) {
    const clean = normalizeConnection(input, this.settings.defaults);
    const index = this.connections.findIndex((c) => c.id === clean.id);
    if (index === -1) {
      clean.createdAt = new Date().toISOString();
      this.connections.push(clean);
    } else {
      clean.createdAt = this.connections[index].createdAt;
      clean.lastConnectedAt = this.connections[index].lastConnectedAt || null;
      this.connections[index] = clean;
    }
    this.persistConnections();
    return clean;
  }

  patchConnection(id, patch) {
    const c = this.getConnection(id);
    if (!c) return null;
    Object.assign(c, patch);
    this.persistConnections();
    return c;
  }

  deleteConnection(id) {
    const before = this.connections.length;
    this.connections = this.connections.filter((c) => c.id !== id);
    this.persistConnections();
    return this.connections.length < before;
  }

  /** Personal state for read-only central systems: favorites and last use. */
  centralOverlay(id) {
    const st = this.centralState;
    return { favorite: Boolean(st.favorites[id]), lastConnectedAt: typeof st.lastConnectedAt[id] === 'string' ? st.lastConnectedAt[id] : null };
  }

  patchCentral(id, patch) {
    if ('favorite' in patch) {
      if (patch.favorite) this.centralState.favorites[id] = true; else delete this.centralState.favorites[id];
    }
    if (patch.lastConnectedAt) this.centralState.lastConnectedAt[id] = patch.lastConnectedAt;
    writeJson(this.centralStateFile, this.centralState);
    return this.centralOverlay(id);
  }

  persistConnections() {
    writeJson(this.connectionsFile, this.connections);
  }

  getSettings() {
    return this.settings;
  }

  saveSettings(input) {
    const s = mergeDeep(structuredClone(DEFAULT_SETTINGS), pick(input || {}, Object.keys(DEFAULT_SETTINGS)));
    s.defaults = normalizeDefaults(s.defaults);
    s.startWithWindows = Boolean(s.startWithWindows);
    s.keepRunningInTray = s.keepRunningInTray !== false;
    s.launchMode = s.launchMode === 'direct' ? 'direct' : 'file';
    s.signingThumbprint = String(s.signingThumbprint || '').replace(/[^0-9a-fA-F]/g, '').toUpperCase();
    s.statusRefreshSeconds = Math.min(600, Math.max(30, Number(s.statusRefreshSeconds) || 60));
    s.confirmDisconnect = s.confirmDisconnect !== false;
    s.showConnectDialog = s.showConnectDialog !== false;
    this.settings = s;
    writeJson(this.settingsFile, s);
    return s;
  }

  audit(event, details = {}) {
    const entry = { ts: new Date().toISOString(), user: currentUser(), event, ...details };
    try {
      // Rotate at 5 MB; one previous file is kept as audit.1.log.
      if (fs.existsSync(this.auditFile) && fs.statSync(this.auditFile).size > 5 * 1024 * 1024) {
        fs.renameSync(this.auditFile, path.join(this.dir, 'audit.1.log'));
      }
      fs.appendFileSync(this.auditFile, JSON.stringify(entry) + '\n', 'utf8');
    } catch { /* auditing must never block a connection */ }
    return entry;
  }

  readAudit(limit = 200) {
    if (!fs.existsSync(this.auditFile)) return [];
    // Read only the tail of the file.
    const size = fs.statSync(this.auditFile).size;
    const length = Math.min(size, 512 * 1024);
    const buf = Buffer.alloc(length);
    const fd = fs.openSync(this.auditFile, 'r');
    try { fs.readSync(fd, buf, 0, length, size - length); } finally { fs.closeSync(fd); }
    const lines = buf.toString('utf8').split('\n').slice(size > length ? 1 : 0).filter(Boolean);
    return lines.slice(-limit).map((l) => { try { return JSON.parse(l); } catch { return null; } }).filter(Boolean).reverse();
  }
}

function normalizeConnection(input, defaults) {
  const d = structuredClone(defaults);
  const c = input || {};
  const host = String(c.host || '').trim();
  if (!host) throw new Error('Enter a computer name or IP address.');
  const isV6 = /^[0-9A-Fa-f:.]+$/.test(host) && (host.match(/:/g) || []).length > 1;
  if (!isV6 && !/^[A-Za-z0-9.\-_]+$/.test(host)) {
    throw new Error(host.includes(':') ? 'Enter the port in the Port field, not in the computer name.' : 'The computer name contains characters that are not allowed.');
  }
  const port = Number(c.port) || 3389;
  if (port < 1 || port > 65535) throw new Error('The port must be between 1 and 65535.');
  const username = String(c.username || '').trim().replace(/[\u0000-\u001f\u007f]/g, '');
  if (username && !/^[^\s"/[\]:;|=,+*?<>]+$/.test(username.replace(/\\/, ''))) throw new Error('The username format is not valid. Use DOMAIN\\username or username@domain.');
  const gateway = { ...d.gateway, ...(c.gateway || {}) };
  if (gateway.mode !== 'none' && !String(gateway.host || '').trim()) throw new Error('Enter an RD Gateway address or switch the gateway off.');
  if (gateway.mode !== 'none' && !HOST_PATTERN.test(String(gateway.host).trim())) throw new Error('The RD Gateway address contains characters that are not allowed.');

  return {
    id: ID_PATTERN.test(String(c.id || '')) ? String(c.id).toLowerCase() : crypto.randomUUID(),
    name: String(c.name || host).trim().slice(0, 120),
    host,
    port,
    username,
    folder: String(c.folder || '').trim().slice(0, 80),
    os: String(c.os || '').trim().slice(0, 80),
    location: String(c.location || '').trim().slice(0, 80),
    tags: (Array.isArray(c.tags) ? c.tags : String(c.tags || '').split(','))
      .map((t) => String(t).trim()).filter(Boolean).slice(0, 12),
    description: String(c.description || '').trim().slice(0, 500),
    favorite: Boolean(c.favorite),
    sample: Boolean(c.sample),
    credentialMode: c.credentialMode === 'saved' ? 'saved' : 'prompt',
    display: { ...d.display, ...(c.display || {}) },
    redirect: { ...d.redirect, ...(c.redirect || {}) },
    gateway: { mode: ['none', 'always', 'detect'].includes(gateway.mode) ? gateway.mode : 'none', host: String(gateway.host || '').trim() },
    security: {
      ...d.security,
      ...(c.security || {}),
      authLevel: c.security && c.security.authLevel === 1 ? 1 : 2,
      credentialProtection: ['none', 'remoteGuard', 'restrictedAdmin'].includes(c.security && c.security.credentialProtection) ? c.security.credentialProtection : 'none',
    },
    lastConnectedAt: typeof c.lastConnectedAt === 'string' && !Number.isNaN(Date.parse(c.lastConnectedAt)) ? c.lastConnectedAt : null,
  };
}

function pick(obj, keys) {
  return Object.fromEntries(keys.filter((k) => Object.prototype.hasOwnProperty.call(obj, k)).map((k) => [k, obj[k]]));
}

/** Defaults for new systems, reduced to known keys and types. */
function normalizeDefaults(d) {
  const c = normalizeConnection({ host: 'defaults.invalid', ...pick(d || {}, ['display', 'redirect', 'gateway', 'security']) }, DEFAULT_SETTINGS.defaults);
  return { display: c.display, redirect: c.redirect, gateway: c.gateway.mode !== 'none' && c.gateway.host ? c.gateway : { mode: 'none', host: '' }, security: c.security };
}

/**
 * Optional machine-wide policy deployed by IT: %ProgramData%\BearingPoint\RdpClient\policy.json
 * Only known keys with the right types are used; anything else is ignored.
 */
function readPolicy() {
  const file = path.join(process.env.ProgramData || 'C:\\ProgramData', 'BearingPoint', 'RdpClient', 'policy.json');
  const exists = fs.existsSync(file);
  if (exists && !policyOwnerTrusted(file)) {
    // A standard user could have created this file (ProgramData is writable by Users by default).
    return { file, active: false, rejected: 'The policy file was ignored because it is not owned by Administrators or SYSTEM.' };
  }
  const raw = exists ? readJson(file, null) : null;
  const p = { file, active: Boolean(raw) };
  if (!raw || typeof raw !== 'object') return p;
  const bool = (v) => (typeof v === 'boolean' ? v : undefined);
  const str = (v, max = 500) => (typeof v === 'string' ? v.slice(0, max) : undefined);

  if (raw.redirect && typeof raw.redirect === 'object') {
    p.redirect = {};
    for (const k of ['clipboard', 'printers', 'microphone', 'smartcards']) if (bool(raw.redirect[k]) !== undefined) p.redirect[k] = raw.redirect[k];
    if (['none', 'all'].includes(raw.redirect.drives)) p.redirect.drives = raw.redirect.drives;
    if (['local', 'remote', 'none'].includes(raw.redirect.audio)) p.redirect.audio = raw.redirect.audio;
  }
  if (bool(raw.allowSavedCredentials) !== undefined) p.allowSavedCredentials = raw.allowSavedCredentials;
  if (['file', 'direct'].includes(raw.launchMode)) p.launchMode = raw.launchMode;
  if (['remoteGuard', 'restrictedAdmin'].includes(raw.requireCredentialProtection)) p.requireCredentialProtection = raw.requireCredentialProtection;
  if (str(raw.signingThumbprint, 128)) p.signingThumbprint = raw.signingThumbprint.replace(/[^0-9a-fA-F]/g, '').toUpperCase();
  if (Array.isArray(raw.allowedGroups)) p.allowedGroups = raw.allowedGroups.filter((g) => typeof g === 'string').slice(0, 50);
  if (raw.notice && typeof raw.notice === 'object') p.notice = { title: str(raw.notice.title, 120) || 'Notice from IT', message: str(raw.notice.message) || '' };
  if (str(raw.helpUrl, 300) && /^https:\/\//.test(raw.helpUrl)) p.helpUrl = raw.helpUrl;
  if (str(raw.serviceDeskName, 120)) p.serviceDeskName = raw.serviceDeskName;
  if (raw.centralList && typeof raw.centralList === 'object' && str(raw.centralList.publicKey, 200)) {
    const c = raw.centralList;
    const url = str(c.url, 500) && c.url.startsWith('https://') ? c.url : undefined;
    const pathValue = !url && str(c.path, 500) ? c.path : undefined;
    if (url || pathValue) p.centralList = { url, path: pathValue, publicKey: c.publicKey, maxAgeHours: Number(c.maxAgeHours) || 72 };
  }
  return p;
}

/** Members of an allowed AD group may use the app. Uses "whoami /groups" (no PowerShell needed). */
function checkGroupAccess(allowedGroups) {
  if (!allowedGroups || !allowedGroups.length) return Promise.resolve({ allowed: true });
  const { execFile } = require('node:child_process');
  return new Promise((resolve) => {
    // Absolute path: a whoami from another tool on PATH (for example Git) must not answer this check.
    const whoami = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'whoami.exe');
    execFile(whoami, ['/groups', '/fo', 'csv', '/nh'], { windowsHide: true }, (err, stdout) => {
      if (err) { resolve({ allowed: false, reason: 'Your group memberships could not be read.' }); return; }
      const groups = stdout.split(/\r?\n/).map((l) => (l.match(/^"([^"]+)"/) || [])[1]).filter(Boolean).map((g) => g.toLowerCase());
      const wanted = allowedGroups.map((g) => g.toLowerCase());
      const ok = groups.some((g) => wanted.includes(g) || wanted.includes(g.split('\\').pop()));
      resolve(ok ? { allowed: true } : { allowed: false, reason: 'Your account is not in a group that may use this app.' });
    });
  });
}

function currentUser() {
  const domain = process.env.USERDOMAIN;
  const name = os.userInfo().username;
  return domain ? `${domain}\\${name}` : name;
}

function readJson(file, fallback) {
  try {
    return JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    return fallback;
  }
}

function policyOwnerTrusted(file) {
  try { return require('./win32').isOwnedByAdministrators(file); } catch { return false; }
}

function writeJson(file, data) {
  const tmp = `${file}.${process.pid}.tmp`;
  fs.writeFileSync(tmp, JSON.stringify(data, null, 2), 'utf8');
  // Antivirus scanners can hold a short lock on the target; retry briefly.
  for (let attempt = 0; ; attempt++) {
    try {
      fs.renameSync(tmp, file);
      return;
    } catch (err) {
      if (attempt >= 4 || !['EPERM', 'EBUSY', 'EACCES'].includes(err.code)) throw err;
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 50 * (attempt + 1));
    }
  }
}

const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

function mergeDeep(target, source) {
  for (const [k, v] of Object.entries(source || {})) {
    if (FORBIDDEN_KEYS.has(k)) continue;
    if (v && typeof v === 'object' && !Array.isArray(v) && target[k] && typeof target[k] === 'object') mergeDeep(target[k], v);
    else target[k] = v;
  }
  return target;
}

module.exports = { ID_PATTERN, HOST_PATTERN, Store, readPolicy, checkGroupAccess, currentUser, normalizeConnection, mergeDeep, DEFAULT_SETTINGS };
