'use strict';

const {
  app, BrowserWindow, ipcMain, dialog, shell, Menu, Tray, nativeImage, protocol, net, session,
} = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { pathToFileURL } = require('node:url');

const { Store, readPolicy, checkGroupAccess, currentUser, normalizeConnection, ID_PATTERN } = require('./store');
const { CentralList } = require('./central');
const { detectAndParse } = require('./importers');
const { SessionManager, isLive } = require('./sessions');
const { probe, probeMany } = require('./probe');
const { applyPolicy, buildRdp, encodeRdp, decodeRdp, rdpToConnection, parseAddress } = require('./rdpfile');
const { explainProbe, allReasons } = require('./errors');
const win32 = require('./win32');
const samples = require('./samples');

const APP_ID = 'com.bearingpoint.remotedesktop';
// Development only: a separate data folder (and therefore a separate single-instance lock) for test runs.
const DATA_DIR = (!app.isPackaged && process.env.BP_RDP_DEV_DATA_DIR) || path.join(app.getPath('appData'), 'BearingPoint', 'RdpClient');
const RENDERER_DIR = path.join(__dirname, '..', 'renderer');
const ASSETS_DIR = path.join(__dirname, '..', '..', 'assets');
const APP_ORIGIN = 'app://bundle/';
const LOCAL_DIR = path.join(process.env.LOCALAPPDATA || app.getPath('temp'), 'BearingPoint', 'RdpClient');
const CENTRAL_ID = /^central-[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

app.setPath('userData', DATA_DIR);
// Chromium caches stay local instead of roaming with the profile.
app.setPath('sessionData', path.join(LOCAL_DIR, 'Session'));
app.setAppUserModelId(APP_ID);
protocol.registerSchemesAsPrivileged([{ scheme: 'app', privileges: { standard: true, secure: true, supportFetchAPI: true } }]);

let win = null;
let tray = null;
let store = null;
let sessions = null;
// The policy is read before the app is ready so that network switches can be set from it.
const policy = readPolicy();
if (policy.centralList && policy.centralList.url) {
  // Windows Integrated Authentication (Kerberos/NTLM) only for the host of the central list.
  app.commandLine.appendSwitch('auth-server-allowlist', new URL(policy.centralList.url).hostname);
}
let central = null;
let rendererReady = false;
let access = { allowed: true };
let quitting = false;
let trayHintShown = false;
let pendingConnectId = connectArg(process.argv);
const startHidden = process.argv.includes('--hidden');
const status = new Map();
const inFlight = new Set();
// Quick connect targets that were not saved as systems. Kept in memory for this app session only.
const adhoc = new Map();
let statusTimer = null;

if (!app.requestSingleInstanceLock()) {
  app.exit(0);
} else {
  app.on('second-instance', (_e, argv) => {
    showWindow();
    const id = connectArg(argv);
    if (id) requestConnect(id);
  });
  app.whenReady().then(start);
}

function connectArg(argv) {
  const arg = (argv || []).find((a) => a.startsWith('--connect='));
  const id = arg ? arg.slice('--connect='.length) : null;
  return id && (ID_PATTERN.test(id) || CENTRAL_ID.test(id)) ? id : null;
}

async function start() {
  store = new Store(DATA_DIR);
  access = await checkGroupAccess(policy.allowedGroups);
  sessions = new SessionManager({ tmpDir: path.join(LOCAL_DIR, 'tmp'), focusWindow: win32.focusProcessWindow });
  central = new CentralList({
    config: policy.centralList, cacheDir: path.join(LOCAL_DIR, 'central-cache'),
    defaults: store.getSettings().defaults, fetchImpl: (url, opts) => net.fetch(url, opts),
  });
  sessions.on('update', onSessionUpdate);

  hardenSession();
  protocol.handle('app', serveRendererFile);
  registerIpc();
  createWindow();
  createTray();
  updateJumpList();
  scheduleStatus();
  if (access.allowed) refreshStatus().catch(() => {});
  if (access.allowed && central.config) {
    refreshCentral();
    setInterval(refreshCentral, 60 * 60 * 1000);
  }
}

async function refreshCentral() {
  await central.refresh();
  send('connections:changed', central.info);
  updateTrayMenu();
  updateJumpList();
  refreshStatus(central.systems.map((c) => c.id)).catch(() => {});
}

/** Personal systems plus the read-only central list (with personal favorites and last use). */
function allConnections() {
  const personal = store.listConnections();
  const centralSystems = central ? central.systems.map((c) => ({ ...c, ...store.centralOverlay(c.id) })) : [];
  return [...personal, ...centralSystems];
}

function isCentral(id) {
  return CENTRAL_ID.test(String(id));
}

function touchConnection(id, lastConnectedAt) {
  if (adhoc.has(id)) return;
  if (isCentral(id)) store.patchCentral(id, { lastConnectedAt });
  else store.patchConnection(id, { lastConnectedAt });
}

// ── Security ──────────────────────────────────────────
function serveRendererFile(request) {
  const url = new URL(request.url);
  if (url.host !== 'bundle') return new Response('Not found', { status: 404 });
  let pathname;
  try { pathname = decodeURIComponent(url.pathname); } catch { return new Response('Bad request', { status: 400 }); }
  if (pathname.includes('\0')) return new Response('Bad request', { status: 400 });
  const file = path.normalize(path.join(RENDERER_DIR, pathname));
  if (!file.startsWith(RENDERER_DIR + path.sep)) return new Response('Not found', { status: 404 });
  return net.fetch(pathToFileURL(file).toString());
}

function hardenSession() {
  session.defaultSession.setPermissionRequestHandler((_wc, _permission, callback) => callback(false));
  session.defaultSession.setPermissionCheckHandler(() => false);
  app.on('web-contents-created', (_e, contents) => {
    contents.on('will-attach-webview', (ev) => ev.preventDefault());
    contents.on('will-navigate', (ev) => ev.preventDefault());
    contents.setWindowOpenHandler(({ url }) => {
      openExternalSafely(url);
      return { action: 'deny' };
    });
  });
}

/** Only https links to the help site configured by IT are opened in the browser. */
function openExternalSafely(url) {
  try {
    const u = new URL(url);
    const allowed = policy && policy.helpUrl ? new URL(policy.helpUrl).hostname : null;
    if (u.protocol === 'https:' && allowed && u.hostname === allowed) shell.openExternal(u.href);
  } catch { /* invalid URL */ }
}

// ── Window, tray, jump list ───────────────────────────
function createWindow() {
  win = new BrowserWindow({
    width: 1400,
    height: 900,
    minWidth: 960,
    minHeight: 640,
    backgroundColor: '#f7f7f7',
    title: 'BearingPoint Remote Desktop',
    icon: path.join(ASSETS_DIR, 'icon.ico'),
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      spellcheck: false,
      webSecurity: true,
    },
  });
  Menu.setApplicationMenu(null);
  win.once('ready-to-show', () => { if (!startHidden) win.show(); });
  win.on('close', onWindowClose);
  win.webContents.on('did-start-loading', () => { rendererReady = false; });
  win.loadURL(`${APP_ORIGIN}index.html`);
  // Development only: never available in the installed app, because it runs script from an environment variable.
  if (!app.isPackaged && process.env.BP_RDP_DEV_CAPTURE) devCapture(process.env.BP_RDP_DEV_CAPTURE);
}

function showWindow() {
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.show();
  win.focus();
}

function requestConnect(id) {
  if (!win || !rendererReady) { pendingConnectId = id; return; }
  showWindow();
  send('app:connect-request', id);
}

/** Closing the window keeps the app in the notification area while sessions are open, so tracking continues. */
function onWindowClose(event) {
  if (quitting) return;
  const live = sessions.liveCount();
  if (live && store.getSettings().keepRunningInTray && tray) {
    event.preventDefault();
    win.hide();
    if (!trayHintShown) {
      trayHintShown = true;
      tray.displayBalloon({ title: 'BearingPoint Remote Desktop', content: 'The app keeps running in the notification area while your sessions are open.', iconType: 'info' });
    }
    return;
  }
  if (live && !confirmQuitWithSessions()) { event.preventDefault(); return; }
  quitting = true;
}

function confirmQuitWithSessions() {
  const live = sessions.liveCount();
  if (!live) return true;
  const choice = dialog.showMessageBoxSync(win && win.isVisible() ? win : null, {
    type: 'warning',
    title: 'Sessions are still open',
    message: `${live === 1 ? '1 remote session is' : `${live} remote sessions are`} still open.`,
    detail: 'The session windows stay open, but this app stops tracking them: results and duration are not recorded.',
    buttons: ['Cancel', 'Quit app'],
    defaultId: 0,
    cancelId: 0,
    noLink: true,
  });
  if (choice === 1) {
    store.audit('app_quit_with_open_sessions', { sessions: sessions.list().filter((s) => isLive(s.state)).map((s) => ({ sessionId: s.id, host: s.host })) });
    return true;
  }
  return false;
}

function quitApp() {
  if (!confirmQuitWithSessions()) return;
  quitting = true;
  app.quit();
}

function createTray() {
  try {
    tray = new Tray(nativeImage.createFromPath(path.join(ASSETS_DIR, 'icon-16.png')));
  } catch {
    tray = null;
    return;
  }
  tray.setToolTip('BearingPoint Remote Desktop');
  tray.on('click', showWindow);
  updateTrayMenu();
}

function updateTrayMenu() {
  if (!tray) return;
  const live = sessions.list().filter((s) => isLive(s.state));
  const recent = recentConnections(5);
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Open BearingPoint Remote Desktop', click: showWindow },
    { type: 'separator' },
    { label: live.length ? `${live.length} active ${live.length === 1 ? 'session' : 'sessions'}` : 'No active sessions', enabled: false },
    ...live.map((s) => ({ label: `Show ${s.name}`, click: () => { try { sessions.focus(s.id); } catch { showWindow(); } } })),
    { type: 'separator' },
    { label: 'Connect to', enabled: recent.length > 0, submenu: recent.length ? recent.map((c) => ({ label: c.name, click: () => requestConnect(c.id) })) : undefined },
    { type: 'separator' },
    { label: 'Quit', click: quitApp },
  ]));
  tray.setToolTip(live.length ? `BearingPoint Remote Desktop: ${live.length} active` : 'BearingPoint Remote Desktop');
}

function recentConnections(n) {
  return allConnections().filter((c) => typeof c.lastConnectedAt === 'string')
    .sort((a, b) => b.lastConnectedAt.localeCompare(a.lastConnectedAt)).slice(0, n);
}

/** Recent systems in the taskbar jump list; each entry starts the app with --connect=<id>. */
function updateJumpList() {
  const recent = recentConnections(6);
  const prefix = app.isPackaged ? '' : `"${app.getAppPath()}" `;
  try {
    app.setJumpList(recent.length ? [{
      type: 'custom',
      name: 'Recent systems',
      items: recent.map((c) => ({
        type: 'task', title: c.name, description: `Connect to ${c.host}`,
        program: process.execPath, args: `${prefix}--connect=${c.id}`, iconPath: process.execPath, iconIndex: 0,
      })),
    }] : null);
  } catch { /* jump lists are optional */ }
}

function applyLoginItem(settings) {
  if (!app.isPackaged) return;
  app.setLoginItemSettings({ openAtLogin: Boolean(settings.startWithWindows), args: ['--hidden'] });
}

app.on('before-quit', () => { quitting = true; });
app.on('window-all-closed', () => app.quit());

// ── Status ────────────────────────────────────────────
function send(channel, payload) {
  if (win && !win.isDestroyed()) win.webContents.send(channel, payload);
}

function withStatus(conn) {
  const live = sessions.activeFor(conn.id);
  return { ...conn, status: status.get(conn.id) || null, activeSessionId: live ? live.id : null, activeState: live ? live.state : null };
}

async function refreshStatus(ids) {
  const targets = allConnections().filter((c) => !ids || ids.includes(c.id)).map(probeTarget);
  const results = await probeMany(targets);
  for (const [id, r] of Object.entries(results)) status.set(id, r);
  send('status:update', Object.fromEntries(status));
  return Object.fromEntries(status);
}

/** Probe the gateway (443) when the connection always uses one, otherwise the host. */
function probeTarget(c) {
  if (c.gateway && c.gateway.mode === 'always' && c.gateway.host) return { id: c.id, host: c.gateway.host, port: 443, viaGateway: true };
  return { id: c.id, host: c.host, port: c.port || 3389 };
}

function scheduleStatus() {
  clearInterval(statusTimer);
  const seconds = store.getSettings().statusRefreshSeconds || 60;
  statusTimer = setInterval(() => {
    if (access.allowed && win && !win.isDestroyed() && win.isVisible() && !win.isMinimized()) refreshStatus().catch(() => {});
  }, seconds * 1000);
}

// ── Policy helpers ────────────────────────────────────
function effectiveLaunch(settings) {
  let mode = policy.launchMode || settings.launchMode;
  let note = null;
  // Direct launch cannot enforce redirection locks (mstsc reads them from Default.rdp).
  if (mode === 'direct' && policy.redirect && Object.keys(policy.redirect).length) {
    mode = 'file';
    note = 'IT policy locks device options, so connections use a connection file.';
  }
  return { launchMode: mode, signingThumbprint: policy.signingThumbprint || settings.signingThumbprint, note };
}

function assertAllowed() {
  if (!access.allowed) throw new Error(access.reason || 'You do not have access to this app.');
}

function connectionOrThrow(id) {
  const c = isCentral(id)
    ? (central && central.get(id) ? { ...central.get(id), ...store.centralOverlay(id) } : null)
    : adhoc.get(id) || store.getConnection(id);
  if (!c) throw new Error('System not found.');
  return c;
}

function assertPersonal(id) {
  if (isCentral(id)) throw new Error('This system is managed by IT and cannot be changed here. You can duplicate it as a personal system.');
}

// ── IPC ───────────────────────────────────────────────
function registerIpc() {
  // open: allowed even when access is denied (info screen, help, data folder).
  const handle = (channel, fn, { open = false } = {}) => ipcMain.handle(channel, async (e, ...args) => {
    const url = e.senderFrame ? e.senderFrame.url : '';
    if (!win || e.sender !== win.webContents || !url.startsWith(APP_ORIGIN)) return { ok: false, error: 'Untrusted sender.' };
    try {
      if (!open) assertAllowed();
      return { ok: true, data: await fn(...args) };
    } catch (err) {
      return { ok: false, error: String((err && err.message) || err) };
    }
  });

  handle('app:info', () => {
    const settings = store.getSettings();
    return {
      user: currentUser(),
      displayName: os.userInfo().username,
      computer: os.hostname(),
      version: app.getVersion(),
      dataDir: DATA_DIR,
      policy,
      access,
      launch: effectiveLaunch(settings),
      central: central ? central.info : { configured: false, state: 'off' },
      packaged: app.isPackaged,
    };
  }, { open: true });
  handle('app:ready', () => {
    rendererReady = true;
    if (pendingConnectId) {
      const id = pendingConnectId;
      pendingConnectId = null;
      setTimeout(() => requestConnect(id), 0);
    }
    return true;
  }, { open: true });
  handle('app:openDataFolder', () => shell.openPath(DATA_DIR), { open: true });
  handle('app:openHelp', () => { if (policy.helpUrl) openExternalSafely(policy.helpUrl); }, { open: true });
  handle('help:errorCodes', () => allReasons(), { open: true });

  handle('connections:list', () => {
    if (store.corrupt) {
      throw new Error(`Your system list file was damaged and could not be read (${store.corrupt.error}). A copy was kept at ${store.corrupt.backup}.`);
    }
    return allConnections().map(withStatus);
  });
  handle('connections:acknowledgeCorrupt', () => { store.acknowledgeCorrupt(); return true; });
  handle('connections:refreshCentral', async () => { await refreshCentral(); return central.info; });
  handle('connections:save', (input) => {
    if (input && input.id) assertPersonal(input.id);
    const isNew = !input.id;
    const saved = store.saveConnection(input);
    store.audit(isNew ? 'system_added' : 'system_updated', { connectionId: saved.id, name: saved.name, host: saved.host });
    refreshStatus([saved.id]).catch(() => {});
    updateJumpList();
    return withStatus(saved);
  });
  handle('connections:delete', (id) => {
    assertPersonal(id);
    const c = connectionOrThrow(id);
    store.deleteConnection(id);
    status.delete(id);
    store.audit('system_removed', { connectionId: id, name: c.name, host: c.host });
    updateJumpList();
    updateTrayMenu();
    return true;
  });
  handle('connections:favorite', (id) => {
    const c = connectionOrThrow(id);
    if (isCentral(id)) {
      store.patchCentral(id, { favorite: !c.favorite });
      return withStatus(connectionOrThrow(id));
    }
    return withStatus(store.patchConnection(id, { favorite: !c.favorite }));
  });
  handle('connections:samples', () => {
    const existing = new Set(store.listConnections().map((c) => c.host));
    const added = samples.filter((s) => !existing.has(s.host)).map((s) => store.saveConnection(s));
    refreshStatus(added.map((a) => a.id)).catch(() => {});
    return added.length;
  });
  handle('connections:import', async () => {
    const res = await dialog.showOpenDialog(win, {
      title: 'Import connections',
      filters: [
        { name: 'Connection files (.rdp, .rdg, confCons.xml)', extensions: ['rdp', 'rdg', 'xml'] },
        { name: 'Remote Desktop files', extensions: ['rdp'] },
        { name: 'Remote Desktop Connection Manager', extensions: ['rdg'] },
        { name: 'mRemoteNG', extensions: ['xml'] },
      ],
      properties: ['openFile', 'multiSelections'],
    });
    if (res.canceled) return [];
    const defaults = store.getSettings().defaults;
    const items = [];
    for (const file of res.filePaths) {
      const name = path.basename(file);
      try {
        const size = fs.statSync(file).size;
        if (/\.rdp$/i.test(name)) {
          if (size > 256 * 1024) throw new Error('The file is too large to be a Remote Desktop file.');
          const { connection, warnings } = rdpToConnection(decodeRdp(fs.readFileSync(file)), name);
          normalizeConnection(connection, defaults);
          items.push({ file: name, connection, warnings });
          continue;
        }
        if (size > 5 * 1024 * 1024) throw new Error('The file is too large.');
        const parsed = detectAndParse(decodeRdp(fs.readFileSync(file)));
        if (!parsed.items.length) throw new Error(`No Remote Desktop connections were found in this ${parsed.format} file.`);
        if (parsed.warnings.length) items.push({ file: name, notice: `${parsed.format}: ${parsed.warnings.join(' ')}` });
        for (const it of parsed.items) {
          try {
            normalizeConnection(it.connection, defaults);
            items.push({ file: name, connection: it.connection, warnings: it.warnings });
          } catch (err) {
            items.push({ file: `${name}: ${it.connection.name || it.connection.host || 'entry'}`, error: err.message });
          }
        }
      } catch (err) {
        items.push({ file: name, error: String(err.message || err) });
      }
    }
    return items.slice(0, 1000);
  });
  handle('connections:confirmImport', (drafts) => {
    const saved = (Array.isArray(drafts) ? drafts : []).slice(0, 1000).map((d) => store.saveConnection({ ...d, id: undefined, favorite: false, sample: false }));
    store.audit('systems_imported', { count: saved.length, hosts: saved.map((s) => s.host).slice(0, 200) });
    refreshStatus(saved.map((s) => s.id)).catch(() => {});
    return saved.length;
  });
  handle('connections:export', async (id) => {
    const c = connectionOrThrow(id);
    const res = await dialog.showSaveDialog(win, {
      title: 'Export Remote Desktop file',
      defaultPath: `${c.name.replace(/[^\w\- ]+/g, '_')}.rdp`,
      filters: [{ name: 'Remote Desktop files', extensions: ['rdp'] }],
    });
    if (res.canceled || !res.filePath) return null;
    fs.writeFileSync(res.filePath, encodeRdp(buildRdp(applyPolicy(c, policy))));
    store.audit('system_exported', { connectionId: c.id, name: c.name, host: c.host });
    return res.filePath;
  });

  handle('settings:get', () => store.getSettings(), { open: true });
  handle('settings:save', (input) => {
    const s = store.saveSettings(input);
    scheduleStatus();
    applyLoginItem(s);
    store.audit('settings_saved', { launchMode: s.launchMode, signed: Boolean(s.signingThumbprint) });
    return s;
  });

  handle('status:probe', async (id) => {
    const t = probeTarget(connectionOrThrow(id));
    const r = await probe(t.host, t.port);
    status.set(id, r);
    send('status:update', Object.fromEntries(status));
    return r;
  });
  handle('status:probeAll', () => refreshStatus());

  // Credentials are addressed by connection id; the host is resolved here, never taken from the page.
  handle('credentials:get', (id) => win32.getCredential(connectionOrThrow(id).host));
  handle('credentials:save', ({ id, username, password }) => {
    if (policy.allowSavedCredentials === false) throw new Error('Saving passwords is switched off by IT policy.');
    const c = connectionOrThrow(id);
    win32.saveCredential(c.host, String(username || '').trim(), String(password || ''));
    store.audit('credential_saved', { connectionId: id, host: c.host, username });
    return true;
  });
  handle('credentials:delete', (id) => {
    const c = connectionOrThrow(id);
    const removed = win32.deleteCredential(c.host);
    store.audit('credential_removed', { connectionId: id, host: c.host });
    return removed;
  });

  handle('sessions:list', () => sessions.list());
  handle('sessions:focus', (id) => sessions.focus(id));
  handle('sessions:disconnect', (id) => sessions.disconnect(id));
  handle('sessions:clearEnded', () => { sessions.clearEnded(); return sessions.list(); });
  handle('sessions:connect', (id, options) => connect(id, options || {}));
  /** Quick connect: host name or IP (optionally :port) without creating a system first. */
  handle('sessions:quickTarget', ({ address, save } = {}) => {
    const { host, port } = parseAddress(String(address || '').trim());
    if (!host) throw new Error('Enter a computer name or IP address.');
    const defaults = store.getSettings().defaults;
    if (save) {
      const existing = store.listConnections().find((c) => c.host.toLowerCase() === host.toLowerCase() && c.port === port);
      if (existing) return withStatus(existing);
      const saved = store.saveConnection({ name: host, host, port });
      store.audit('system_added', { connectionId: saved.id, name: saved.name, host: saved.host, via: 'quick connect' });
      updateJumpList();
      return withStatus(saved);
    }
    const conn = { ...normalizeConnection({ name: host, host, port }, defaults), adhoc: true };
    adhoc.set(conn.id, conn);
    return withStatus(conn);
  });

  handle('audit:list', (limit) => store.readAudit(Math.min(Number(limit) || 200, 1000)));
}

const OVERRIDE_KEYS = ['display', 'redirect', 'gateway', 'security', 'username'];

/** Connection flow with visible steps: availability, settings, credentials, start. */
async function connect(id, { force = false, overrides = null, quick = false } = {}) {
  assertAllowed();
  const base = connectionOrThrow(id);
  const running = sessions.activeFor(id);
  if (running) return { outcome: 'alreadyActive', session: running };
  if (inFlight.has(id)) return { outcome: 'inProgress' };
  inFlight.add(id);
  try {
    return await connectSteps(id, base, force, overrides, quick);
  } finally {
    inFlight.delete(id);
  }
}

async function connectSteps(id, base, force, overrides, quick) {
  const progress = (step, state, detail) => send('connect:progress', { connectionId: id, step, state, detail });

  // Only known option groups may be changed for a single connection; host and gateway target stay as saved
  // unless the gateway is part of the options. The result is validated like a saved system.
  const safe = {};
  for (const k of OVERRIDE_KEYS) if (overrides && Object.prototype.hasOwnProperty.call(overrides, k)) safe[k] = overrides[k];
  const merged = normalizeConnection({ ...base, ...safe, id: base.id, host: base.host, port: base.port }, store.getSettings().defaults);
  const conn = applyPolicy(merged, policy);
  conn.promptAlways = Boolean(overrides && overrides.promptAlways);

  // 1. Availability
  progress('check', 'running');
  const target = probeTarget(conn);
  let result = await probe(target.host, target.port);
  if (!result.reachable && conn.gateway.mode === 'detect' && conn.gateway.host) {
    result = await probe(conn.gateway.host, 443);
    if (result.reachable) result.viaGateway = true;
  }
  status.set(id, result);
  send('status:update', Object.fromEntries(status));
  if (!result.reachable && !force) {
    progress('check', 'failed');
    const info = explainProbe(result.reason);
    store.audit('connect_blocked', { connectionId: id, name: conn.name, host: conn.host, reason: result.reason });
    return { outcome: 'unreachable', ...info, technical: `${target.host}:${target.port} – ${result.detail || result.reason}` };
  }
  progress('check', result.reachable ? 'done' : 'skipped');

  // 2. Settings
  progress('validate', 'running');
  // Quick connect starts directly (no Windows .rdp confirmation) unless IT policy requires connection files.
  const settings = store.getSettings();
  const launch = effectiveLaunch(quick ? { ...settings, launchMode: 'direct' } : settings);
  progress('validate', 'done');

  // 3. Credentials
  progress('secure', 'running');
  let credential = { saved: false };
  try { credential = win32.getCredential(conn.host); } catch { /* Credential Manager unavailable */ }
  progress('secure', 'done', credential.saved && !conn.promptAlways ? 'saved' : 'prompt');

  // 4. Start
  progress('start', 'running');
  let session;
  try {
    session = await sessions.start(conn, launch);
  } catch (err) {
    progress('start', 'failed');
    throw err;
  }
  touchConnection(id, session.startedAt);
  store.audit('connect_started', {
    sessionId: session.id, connectionId: id, name: conn.name, host: conn.host,
    gateway: session.gateway, launchMode: session.launchMode, signed: session.signed, promptAlways: conn.promptAlways,
    redirect: conn.redirect, credentialProtection: conn.security.credentialProtection,
  });
  progress('start', 'done');
  updateJumpList();
  return { outcome: 'started', session, credentialSaved: credential.saved && !conn.promptAlways, launchNote: launch.note };
}

function onSessionUpdate(s) {
  send('session:update', s);
  updateTrayMenu();
  const internal = sessions.sessions.get(s.id);
  if (!internal) return;
  if (s.state === 'active' && !internal.loggedActive) {
    internal.loggedActive = true;
    store.audit('session_connected', { sessionId: s.id, connectionId: s.connectionId, name: s.name, host: s.host, verified: s.verified });
  }
  if (s.state === 'reconnecting') {
    store.audit('session_interrupted', { sessionId: s.id, connectionId: s.connectionId, host: s.host, code: s.result && s.result.code });
  }
  if (['ended', 'failed'].includes(s.state) && !internal.loggedEnd) {
    internal.loggedEnd = true;
    const durationSec = s.connectedAt ? Math.round((Date.parse(s.endedAt) - Date.parse(s.connectedAt)) / 1000) : 0;
    store.audit('session_ended', {
      sessionId: s.id, connectionId: s.connectionId, name: s.name, host: s.host,
      state: s.state, code: s.result && s.result.code, title: s.result && s.result.title, durationSec,
    });
  }
}

/**
 * Development aid: logs renderer errors and captures screenshots of the steps listed in
 * BP_RDP_DEV_ROUTES (separated by ;;) into the folder BP_RDP_DEV_CAPTURE, then quits.
 * A step is a route name (navigates, then captures) or "js:<code>" (runs code in the page).
 */
function devCapture(dir) {
  fs.mkdirSync(dir, { recursive: true });
  win.webContents.on('console-message', ({ level, message, lineNumber, sourceId }) => {
    if (level === 'warning' || level === 'error') console.log(`[renderer] ${message} (${sourceId}:${lineNumber})`);
  });
  win.webContents.once('did-finish-load', async () => {
    const wait = (ms) => new Promise((r) => setTimeout(r, ms));
    await wait(2500);
    for (const step of (process.env.BP_RDP_DEV_ROUTES || 'dashboard').split(';;')) {
      if (step.startsWith('js:')) {
        await win.webContents.executeJavaScript(step.slice(3)).catch((e) => console.log('[dev] js failed', e.message));
        await wait(1200);
        continue;
      }
      await win.webContents.executeJavaScript(`document.querySelector('[data-nav="${step}"]')?.click()`);
      await wait(1500);
      fs.writeFileSync(path.join(dir, `${step}.png`), (await win.webContents.capturePage()).toPNG());
    }
    fs.writeFileSync(path.join(dir, 'final.png'), (await win.webContents.capturePage()).toPNG());
    quitting = true;
    sessions.disconnectAll && sessions.disconnectAll();
    app.quit();
  });
}
