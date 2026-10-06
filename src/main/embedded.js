'use strict';
// One BpRdpHost.exe per embedded session (see native/BpRdpHost.cs). The helper hosts the Microsoft RDP
// ActiveX control; this module starts it and exchanges one JSON object per line over stdin/stdout.
// Only the main process holds these pipes, so no other program can send commands to a session.

const { EventEmitter } = require('node:events');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

function helperPath(app) {
  return app.isPackaged
    ? path.join(process.resourcesPath, 'bin', 'BpRdpHost.exe')
    : path.join(__dirname, '..', '..', 'resources', 'bin', 'BpRdpHost.exe');
}

function helperAvailable(app) {
  return process.platform === 'win32' && fs.existsSync(helperPath(app));
}

const EVENTS = new Set(['ready', 'fatal', 'connecting', 'connected', 'loginComplete', 'disconnected', 'requestFullscreen',
  'fatalError', 'warning', 'requestMinimize', 'logonError', 'focusReleased', 'autoReconnected', 'autoReconnecting', 'error', 'debug']);

class RdpHost extends EventEmitter {
  constructor(exe) {
    super();
    this.ready = false;
    this.exited = false;
    this.proc = spawn(exe, ['--parent-pid', String(process.pid)], { stdio: ['pipe', 'pipe', 'ignore'], windowsHide: true });
    this.pid = this.proc.pid;
    let buffer = '';
    this.proc.stdout.setEncoding('utf8');
    this.proc.stdout.on('data', (chunk) => {
      buffer += chunk;
      if (buffer.length > 1024 * 1024) buffer = '';
      let i;
      while ((i = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, i).trim();
        buffer = buffer.slice(i + 1);
        if (!line) continue;
        let msg;
        try { msg = JSON.parse(line); } catch { continue; }
        if (!msg || !EVENTS.has(msg.type)) continue;
        if (msg.type === 'ready') this.ready = true;
        this.emit(msg.type, msg.data);
        this.emit('message', msg);
      }
    });
    this.proc.on('error', (err) => { this.exited = true; this.emit('exit', { code: null, error: err }); });
    this.proc.on('exit', (code) => { this.exited = true; this.emit('exit', { code }); });
    this.proc.stdin.on('error', () => { /* helper gone */ });
  }

  send(cmd, args = {}) {
    if (this.exited || !this.proc.stdin.writable) return false;
    this.proc.stdin.write(`${JSON.stringify({ cmd, args })}\n`);
    return true;
  }

  whenReady(timeoutMs = 8000) {
    if (this.ready) return Promise.resolve();
    return new Promise((resolve, reject) => {
      const t = setTimeout(() => reject(new Error('The Remote Desktop component did not start in time.')), timeoutMs);
      this.once('ready', () => { clearTimeout(t); resolve(); });
      this.once('fatal', (m) => { clearTimeout(t); reject(new Error(String(m || 'The Remote Desktop component is not available.'))); });
      this.once('exit', () => { clearTimeout(t); reject(new Error('The Remote Desktop component stopped unexpectedly.')); });
    });
  }

  /** Ends the session; the helper exits after the control reports the disconnect. Killed if it does not. */
  disconnect() {
    this.send('disconnect');
    setTimeout(() => { if (!this.exited) this.kill(); }, 5000);
    return true;
  }

  kill() {
    try { this.proc.kill(); } catch { /* already gone */ }
  }
}

module.exports = { RdpHost, helperPath, helperAvailable };
