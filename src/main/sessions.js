'use strict';
// Starts and tracks sessions of the Windows Remote Desktop client (mstsc.exe).
// State comes from the mstsc process lifetime and the client event log
// Microsoft-Windows-TerminalServices-RDPClient/Operational, matched by the mstsc ProcessId:
//   1024 connecting, 1027 connected, 1026 disconnected (reason code in the properties).

const { EventEmitter } = require('node:events');
const { spawn, execFile } = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { buildRdp, encodeRdp, buildDirectArgs, protectionArgs } = require('./rdpfile');
const { explainDisconnect } = require('./errors');
const { runPowerShell } = require('./ps');
const win32 = require('./win32');

// Disconnect reasons during connection setup that mstsc cannot recover from
// (name resolution, network, TLS/certificate). For others, such as a wrong password, mstsc lets the user retry.
const FATAL_CODES = new Set([0x104, 0x208, 0x508, 0x604, 0x204, 0x108, 0x704, 0x406, 0x606, 0x706, 0x906, 0xA06, 0xB06, 0xC06, 0xC08, 0x1B07]);

const MSTSC = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'mstsc.exe');
const RDPSIGN = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'rdpsign.exe');
const FAST_POLL_MS = 2500;      // while a session is connecting or reconnecting
const SLOW_POLL_MS = 10000;     // while sessions are active
const NO_LOG_GRACE_MS = 120000; // without a readable event log, treat a live client as active after this
const RECONNECT_GIVE_UP_MS = 90000;
const TMP_FILE_MAX_AGE_MS = 30000;

// One query for all tracked mstsc processes. Uses only cmdlets, so it works in Constrained Language Mode.
const EVENTS_SCRIPT = `
$start = [DateTimeOffset]::Parse($in.start).LocalDateTime
$pids = @($in.pids)
$ev = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-TerminalServices-RDPClient/Operational'; Id = 1024,1026,1027; StartTime = $start } -ErrorAction SilentlyContinue |
  Where-Object { $pids -contains $_.ProcessId } |
  Sort-Object TimeCreated |
  ForEach-Object { [pscustomobject]@{ id = $_.Id; pid = $_.ProcessId; time = $_.TimeCreated.ToUniversalTime().ToString('o'); props = @($_.Properties | ForEach-Object { [string]$_.Value }) } }
if ($ev) { ConvertTo-Json -InputObject @($ev) -Compress -Depth 4 } else { '[]' }
`;

class SessionManager extends EventEmitter {
  constructor({ tmpDir, focusWindow }) {
    super();
    this.tmpDir = tmpDir;
    this.focusWindow = focusWindow;
    this.sessions = new Map();
    this.children = new Map();
    this.pollTimer = null;
    this.polling = false;
    this.logReadable = null;
    this.sweepTmp();
  }

  list() {
    return [...this.sessions.values()].map(publicView).sort((a, b) => b.startedAt.localeCompare(a.startedAt));
  }

  get(id) {
    const s = this.sessions.get(id);
    return s ? publicView(s) : null;
  }

  activeFor(connectionId) {
    return this.list().find((s) => s.connectionId === connectionId && isLive(s.state));
  }

  liveCount() {
    return [...this.sessions.values()].filter((s) => isLive(s.state)).length;
  }

  update(session, patch) {
    Object.assign(session, patch);
    this.emit('update', publicView(session));
  }

  /**
   * Launch mstsc for a connection. launchMode "file" writes a temporary .rdp file (all settings apply;
   * Windows shows its security confirmation unless the file is signed by a trusted publisher).
   * launchMode "direct" uses "mstsc /v:" switches only (no dialog; redirection follows Default.rdp).
   */
  async start(conn, { launchMode = 'file', signingThumbprint = '' } = {}) {
    const id = crypto.randomUUID();
    const startedAt = new Date().toISOString();
    let args;
    let rdpPath = null;

    if (launchMode === 'direct') {
      args = buildDirectArgs(conn);
    } else {
      fs.mkdirSync(this.tmpDir, { recursive: true });
      rdpPath = path.join(this.tmpDir, `${id}.rdp`);
      fs.writeFileSync(rdpPath, encodeRdp(buildRdp(conn)));
      if (signingThumbprint) {
        try { await signFile(rdpPath, signingThumbprint); } catch (err) { cleanup(rdpPath); throw err; }
      }
      args = [rdpPath, ...protectionArgs(conn)];
    }

    const session = {
      id,
      connectionId: conn.id,
      name: conn.name,
      host: conn.host,
      port: conn.port || 3389,
      gateway: conn.gateway && conn.gateway.mode !== 'none' ? conn.gateway.host : '',
      displayMode: describeDisplay(conn.display),
      launchMode,
      signed: Boolean(rdpPath && signingThumbprint),
      state: 'connecting',
      startedAt,
      connectedAt: null,
      endedAt: null,
      pid: null,
      result: null,
      verified: false,
      rdpPath,
      lastChange: Date.now(),
    };

    const child = spawn(MSTSC, args, { windowsHide: false, detached: false, stdio: 'ignore' });
    session.pid = child.pid;
    this.sessions.set(id, session);
    this.children.set(id, child);
    this.emit('update', publicView(session));

    child.on('error', (err) => {
      this.children.delete(id);
      cleanup(session.rdpPath);
      this.update(session, { state: 'failed', endedAt: new Date().toISOString(), result: { kind: 'error', title: 'Remote Desktop client could not be started', message: String(err.message || err) } });
    });
    child.on('exit', () => this.onExit(session));
    this.schedulePoll(FAST_POLL_MS);
    return publicView(session);
  }

  schedulePoll(ms) {
    if (this.pollTimer) clearTimeout(this.pollTimer);
    this.pollTimer = setTimeout(() => this.poll(), ms);
  }

  /** Shared event log poller for all live sessions. */
  async poll() {
    this.pollTimer = null;
    const live = [...this.sessions.values()].filter((s) => this.children.has(s.id) && ['connecting', 'active', 'reconnecting'].includes(s.state));
    if (!live.length) return;
    if (this.polling) { this.schedulePoll(FAST_POLL_MS); return; }
    this.polling = true;
    try {
      const since = live.map((s) => s.startedAt).sort()[0];
      const events = await readEvents(since, live.map((s) => s.pid));
      for (const s of live) {
        try { this.applyEvents(s, events); } catch { /* one bad session must not stop tracking of the others */ }
      }
    } catch {
      /* keep polling */
    } finally {
      this.polling = false;
      const stillFast = [...this.sessions.values()].some((s) => this.children.has(s.id) && ['connecting', 'reconnecting'].includes(s.state));
      const anyLive = [...this.sessions.values()].some((s) => this.children.has(s.id) && isLive(s.state));
      if (anyLive) this.schedulePoll(stillFast ? FAST_POLL_MS : SLOW_POLL_MS);
    }
  }

  /** State machine for one live session, driven by its own events (newest last). */
  applyEvents(s, events) {
    if (events === null) {
      // Event log unavailable: fall back to the process lifetime after a grace period (marked unverified).
      if (s.state === 'connecting' && Date.now() - Date.parse(s.startedAt) > NO_LOG_GRACE_MS) {
        this.update(s, { state: 'active', connectedAt: new Date().toISOString(), verified: false, lastChange: Date.now() });
      }
      return;
    }
    const mine = events.filter((e) => e.pid === s.pid);
    const last = mine.filter((e) => e.id === 1026 || e.id === 1027).pop();
    if (!last) return;

    if (last.id === 1027) {
      if (s.state !== 'active' || !s.verified) {
        this.update(s, { state: 'active', connectedAt: s.connectedAt || last.time, verified: true, result: null, lastChange: Date.now() });
        // mstsc has read the connection file; remove it early.
        cleanup(s.rdpPath);
        s.rdpPath = null;
      }
      return;
    }

    // last.id === 1026: disconnected while the mstsc process is still alive.
    const code = reasonOf(last);
    const result = explainDisconnect(code);
    if (!result || result.kind !== 'error') return; // normal close or sign-out; the process exit follows
    if (s.state === 'connecting') {
      if (FATAL_CODES.has(code)) {
        // Nothing to retry in mstsc (name, network or TLS problem): close its error box; the app explains the error.
        s.failedEarly = true;
        this.update(s, { state: 'failed', endedAt: last.time, result, lastChange: Date.now() });
        const child = this.children.get(s.id);
        if (child) child.kill();
      } else if (!s.result || s.result.code !== result.code) {
        // For example a wrong password: mstsc lets the user try again, so keep the window and show a hint.
        this.update(s, { result: { ...result, hint: true }, lastChange: Date.now() });
      }
      return;
    }
    if (s.state === 'active') {
      // Network drop: mstsc retries automatically (autoreconnection enabled) or shows an error box.
      this.update(s, { state: 'reconnecting', result, lastChange: Date.now() });
      return;
    }
    if (s.state === 'reconnecting' && Date.now() - s.lastChange > RECONNECT_GIVE_UP_MS) {
      this.update(s, { state: 'failed', endedAt: last.time, result: result || { kind: 'error', title: 'Connection lost', message: 'The connection could not be restored. Close the Remote Desktop window and reconnect.' }, lastChange: Date.now() });
    }
  }

  async onExit(session) {
    this.children.delete(session.id);
    cleanup(session.rdpPath);
    session.rdpPath = null;
    if (session.failedEarly || session.state === 'failed') {
      if (!session.endedAt) session.endedAt = new Date().toISOString();
      this.emit('update', publicView(session));
      return;
    }
    const endedAt = new Date().toISOString();
    const wasConnected = ['active', 'reconnecting'].includes(session.state);
    const userClosed = session.userDisconnect === true;
    let reasonCode = null;
    const events = await readEvents(session.startedAt, [session.pid]);
    if (events) {
      // Only a disconnect that was not followed by a successful (re)connection explains the exit.
      const last = events.filter((e) => e.pid === session.pid && (e.id === 1026 || e.id === 1027)).pop();
      if (last && last.id === 1026) reasonCode = reasonOf(last);
    }

    let result = explainDisconnect(reasonCode);
    let state = 'ended';
    if (userClosed && !wasConnected) {
      result = { kind: 'normal', title: 'Connection cancelled', message: 'You cancelled the connection before the session was established.' };
    } else if (userClosed) {
      result = { kind: 'normal', title: 'Disconnected', message: 'The window was closed by you. Your remote session keeps running on the server until you sign out inside it.' };
    } else if (result && result.kind === 'error') {
      state = 'failed';
    } else if (!wasConnected && !result) {
      result = { kind: 'normal', title: 'Connection cancelled', message: 'The Remote Desktop window was closed before the session was established.' };
    } else if (!result) {
      result = { kind: 'normal', title: 'Session closed', message: 'The Remote Desktop window was closed. If you did not sign out, your session keeps running on the server.' };
    }
    this.update(session, { state, endedAt, result });
  }

  focus(id) {
    const session = this.sessions.get(id);
    if (!session || !session.pid || !this.children.has(id)) throw new Error('This session is no longer open.');
    this.focusWindow(session.pid);
  }

  /** Close the local client window. The server keeps the user's session running (disconnected). */
  disconnect(id) {
    const child = this.children.get(id);
    const session = this.sessions.get(id);
    if (!child || !session) return false;
    session.userDisconnect = true;
    return child.kill();
  }

  disconnectAll() {
    for (const id of [...this.children.keys()]) this.disconnect(id);
  }

  clearEnded() {
    for (const [id, s] of this.sessions) if (!isLive(s.state) && !this.children.has(id)) this.sessions.delete(id);
  }

  /** Remove connection files left behind by a crash or by quitting while sessions were open. */
  sweepTmp() {
    try {
      for (const f of fs.readdirSync(this.tmpDir)) {
        const file = path.join(this.tmpDir, f);
        if (f.endsWith('.rdp') && Date.now() - fs.statSync(file).mtimeMs > TMP_FILE_MAX_AGE_MS) fs.rmSync(file, { force: true });
      }
    } catch { /* folder does not exist yet */ }
  }
}

function isLive(state) {
  return ['connecting', 'active', 'reconnecting'].includes(state);
}

function publicView(s) {
  const { rdpPath, failedEarly, userDisconnect, lastChange, ...rest } = s;
  return { ...rest };
}

function describeDisplay(d = {}) {
  if (d.multimon) return 'All monitors';
  if (d.mode === 'window') return `Window ${d.width || ''}×${d.height || ''}`.trim();
  return 'Full screen';
}

/**
 * Returns events for the given mstsc process ids, or null if the event log cannot be read.
 * Reads in-process through wevtapi; PowerShell is only a fallback if the native call fails.
 */
async function readEvents(sinceIso, pids) {
  try {
    return win32.queryRdpEvents(sinceIso, pids);
  } catch { /* fall back below */ }
  try {
    const out = await runPowerShell(EVENTS_SCRIPT, { start: sinceIso, pids }, 15000);
    return JSON.parse(out || '[]').map((e) => ({ ...e, pid: Number(e.pid) }));
  } catch {
    return null;
  }
}

/** Event 1026 properties look like ["Disconnect Reason", "260", "Info"]. */
function reasonOf(event) {
  const value = (event.props || []).find((p) => /^\d+$/.test(String(p)));
  return value === undefined ? null : Number.parseInt(value, 10);
}

function signFile(file, thumbprint) {
  return new Promise((resolve, reject) => {
    execFile(RDPSIGN, ['/sha256', thumbprint, file], { windowsHide: true }, (err, stdout, stderr) => {
      if (err) reject(new Error(`The connection file could not be signed (rdpsign): ${(stderr || stdout || err.message).trim()}`));
      else resolve();
    });
  });
}

function cleanup(file) {
  if (!file) return;
  fs.rm(file, { force: true }, () => {});
}

module.exports = { SessionManager, reasonOf, isLive };
