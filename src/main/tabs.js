'use strict';
// Session tabs. Every app window is a tab host: the main window (tabs next to "Home") and windows made
// from tabs dragged out of it. A session's helper window (BpRdpHost) is placed as a child window over the
// content area of the host window that holds its tab. HTML cannot draw over that child, so the child is
// hidden while a dialog or menu is open in its window (the renderer reports this in its layout).
//
// Moving a tab: drag it out of the tab strip (a new window follows the cursor), drop it on another window's
// tab strip to dock it there, or release it at the top edge of a screen for full screen. Dragging a
// detached window by its title bar onto a tab strip docks it as well; closing it docks its tabs back.

const { BrowserWindow, Menu, screen } = require('electron');
const { isPrimaryButtonDown } = require('./win32');

const DRAG_TICK_MS = 16;
const TOP_EDGE_PX = 4;

class TabManager {
  /**
   * createWindow(kind, bounds) -> BrowserWindow: "host" (detached tab window) or "pinbar" (full-screen bar).
   * sessions: SessionManager. getMainWindow(): the main BrowserWindow.
   */
  constructor({ createWindow, sessions, getMainWindow, onDisconnect, showEarly = false }) {
    this.showEarly = showEarly; // development only: show the session window before the connection is up
    this.createWindow = createWindow;
    this.sessions = sessions;
    this.getMainWindow = getMainWindow;
    this.onDisconnect = onDisconnect;
    this.hosts = new Map(); // BrowserWindow.id -> host
    this.helpers = new Map(); // session id -> RdpHost
    this.shown = new Set(); // sessions whose transport is connected (the control has something to show)
    this.drag = null;
  }

  // ── Hosts ──────────────────────────────────────────
  hostFor(win, { create = false } = {}) {
    if (!win || win.isDestroyed()) return null;
    let host = this.hosts.get(win.id);
    if (!host && create) {
      host = { win, tabs: [], active: null, layout: null, isMain: win === this.getMainWindow(), pinbar: null, closing: false, fullscreen: false };
      this.hosts.set(win.id, host);
      if (!host.isMain) this.wireDetached(host);
      // Full-screen changes are checked on every resize as well; the enter/leave events are not always sent.
      const fsChanged = () => {
        if (win.isDestroyed() || host.fullscreen === win.isFullScreen()) return;
        host.fullscreen = win.isFullScreen();
        this.push(host);
        this.updatePinbar(host);
      };
      win.on('enter-full-screen', fsChanged);
      win.on('leave-full-screen', fsChanged);
      win.on('resize', fsChanged);
      host.checkFullscreen = fsChanged;
      win.on('moved', () => this.sync(host));
      win.on('closed', () => { this.hosts.delete(win.id); this.closePinbar(host); });
    }
    return host;
  }

  mainHost() {
    return this.hostFor(this.getMainWindow(), { create: true });
  }

  hostOfSession(id) {
    for (const host of this.hosts.values()) if (host.tabs.includes(id)) return host;
    return null;
  }

  isAppWindow(win) {
    if (!win) return false;
    if (win === this.getMainWindow() || this.hosts.has(win.id)) return true;
    for (const h of this.hosts.values()) if (h.pinbar === win) return true;
    return false;
  }

  /** A window for one dragged-out tab, placed under the cursor. */
  newHost(near) {
    const main = this.mainHost();
    const size = main.layout ? { width: Math.max(800, Math.round(main.layout.content.w)), height: Math.max(560, Math.round(main.layout.content.h) + 80) } : { width: 1200, height: 800 };
    const win = this.createWindow('host', { x: Math.round(near.x - 90), y: Math.round(near.y - 52), ...size });
    return this.hostFor(win, { create: true });
  }

  wireDetached(host) {
    const { win } = host;
    // Dropping the window (title bar drag) on another window's tab strip docks its tabs there.
    win.on('moved', () => {
      if (this.drag || host.closing) return;
      const target = this.stripAt(screen.getCursorScreenPoint(), host);
      if (target) this.mergeInto(host, target);
    });
    // Dragging the window to the top edge (Windows Snap maximizes) means full screen for a session window.
    win.on('maximize', () => {
      const p = screen.getCursorScreenPoint();
      const d = screen.getDisplayNearestPoint(p);
      if (p.y <= d.bounds.y + TOP_EDGE_PX) {
        win.unmaximize();
        this.setFullscreen(host, true);
      }
    });
    // Closing a detached window keeps its sessions: the tabs go back to the main window.
    win.on('close', (e) => {
      if (host.closing || !host.tabs.length) return;
      e.preventDefault();
      this.mergeInto(host, this.mainHost());
    });
  }

  /** Move all tabs of a window into another window and close the emptied one. */
  mergeInto(from, to) {
    if (from === to) return;
    for (const id of [...from.tabs]) this.moveSession(id, to);
    this.closeHostLater(from);
    to.win.show();
    to.win.focus();
  }

  closeHostLater(host) {
    if (host.isMain || host.tabs.length) return;
    host.closing = true;
    // Give the helpers time to leave the window before it is destroyed (child windows die with their parent).
    setTimeout(() => { if (!host.win.isDestroyed()) host.win.destroy(); }, 400);
  }

  // ── Tabs ───────────────────────────────────────────
  add(sessionId, helper) {
    this.helpers.set(sessionId, helper);
    helper.on('connected', () => { this.shown.add(sessionId); this.syncSession(sessionId); });
    helper.on('requestFullscreen', (on) => {
      const host = this.hostOfSession(sessionId);
      if (host && !host.win.isDestroyed()) this.setFullscreen(host, Boolean(on));
    });
    helper.on('focusReleased', () => {
      const host = this.hostOfSession(sessionId);
      if (host) host.win.webContents.focus();
    });
    helper.on('exit', () => this.remove(sessionId));
    const host = this.mainHost();
    host.tabs.push(sessionId);
    host.active = sessionId;
    this.push(host);
    host.win.show();
    host.win.focus();
    return host;
  }

  remove(sessionId) {
    const host = this.hostOfSession(sessionId);
    this.helpers.delete(sessionId);
    this.shown.delete(sessionId);
    if (!host) return;
    const i = host.tabs.indexOf(sessionId);
    host.tabs.splice(i, 1);
    if (host.active === sessionId) host.active = host.tabs[Math.min(i, host.tabs.length - 1)] || null;
    if (!host.tabs.length && host.win.isFullScreen()) this.setFullscreen(host, false);
    this.push(host);
    this.sync(host);
    if (!host.isMain && !host.tabs.length) this.closeHostLater(host);
  }

  has(sessionId) {
    return this.helpers.has(sessionId);
  }

  /** Show a tab (null in the main window shows the app itself). */
  activate(win, sessionId) {
    const host = sessionId ? this.hostOfSession(sessionId) : this.hostFor(win);
    if (!host) return;
    if (sessionId === null && !host.isMain) return;
    host.active = sessionId;
    this.push(host);
    this.sync(host);
    if (sessionId) this.focusSoon(sessionId);
  }

  /** Bring a session to the front: its window, its tab and the keyboard focus. */
  focus(sessionId) {
    const host = this.hostOfSession(sessionId);
    if (!host) throw new Error('This session is no longer open.');
    if (host.win.isMinimized()) host.win.restore();
    host.win.show();
    host.win.focus();
    this.activate(host.win, sessionId);
  }

  focusSoon(sessionId) {
    setTimeout(() => { const h = this.helpers.get(sessionId); if (h) h.send('focus'); }, 80);
  }

  moveSession(sessionId, to, { activate = true } = {}) {
    const from = this.hostOfSession(sessionId);
    if (from === to) return;
    if (from) {
      from.tabs.splice(from.tabs.indexOf(sessionId), 1);
      if (from.active === sessionId) from.active = from.tabs[0] || null;
      this.push(from);
    }
    to.tabs.push(sessionId);
    if (activate) to.active = sessionId;
    this.push(to);
    if (from) this.sync(from);
    this.sync(to);
  }

  toggleFullscreen(sessionId) {
    const host = this.hostOfSession(sessionId);
    if (!host) return;
    if (host.active !== sessionId) this.activate(host.win, sessionId);
    this.setFullscreen(host, !host.win.isFullScreen());
  }

  setFullscreen(host, on) {
    if (host.win.isDestroyed()) return;
    host.win.setFullScreen(on);
    setTimeout(() => host.checkFullscreen && host.checkFullscreen(), 300);
  }

  /** Native context menu (works above the session window, unlike HTML menus). */
  menu(win, sessionId) {
    const host = this.hostOfSession(sessionId);
    if (!host) return;
    const others = [...this.hosts.values()].filter((h) => h !== host && !h.win.isDestroyed());
    Menu.buildFromTemplate([
      { label: host.win.isFullScreen() ? 'Leave full screen' : 'Full screen', accelerator: 'Ctrl+Alt+Break', registerAccelerator: false, click: () => this.toggleFullscreen(sessionId) },
      { label: 'Move to new window', enabled: host.tabs.length > 1 || host.isMain, click: () => this.detachToNewWindow(sessionId) },
      ...others.map((h) => ({ label: h.isMain ? 'Move to main window' : `Move to window with ${this.titleOf(h)}`, click: () => { this.moveSession(sessionId, h); this.closeHostLater(host); h.win.focus(); } })),
      { type: 'separator' },
      { label: 'Disconnect', click: () => this.onDisconnect(sessionId) },
    ]).popup({ window: win });
  }

  titleOf(host) {
    const s = this.sessions.get(host.active || host.tabs[0]);
    return s ? s.name : 'sessions';
  }

  detachToNewWindow(sessionId) {
    const from = this.hostOfSession(sessionId);
    if (!from) return;
    const b = from.win.getBounds();
    const host = this.newHost({ x: b.x + 140, y: b.y + 140 });
    this.moveSession(sessionId, host);
    host.win.show();
    this.closeHostLater(from);
  }

  // ── Layout and placement ───────────────────────────
  /** From the renderer: content and tab strip rectangles in CSS pixels, and whether the session may show. */
  setLayout(win, layout) {
    const host = this.hostFor(win, { create: true });
    if (!host || !layout || typeof layout !== 'object') return;
    const rect = (r) => (r && [r.x, r.y, r.w, r.h].every(Number.isFinite) ? { x: r.x, y: r.y, w: Math.max(0, r.w), h: Math.max(0, r.h) } : null);
    host.layout = { content: rect(layout.content) || { x: 0, y: 0, w: 0, h: 0 }, strip: rect(layout.strip), visible: Boolean(layout.visible) };
    this.sync(host);
  }

  syncSession(sessionId) {
    const host = this.hostOfSession(sessionId);
    if (host) this.sync(host);
  }

  sync(host) {
    if (!host || host.win.isDestroyed()) return;
    const hwnd = Number(host.win.getNativeWindowHandle().readBigUInt64LE(0));
    const scale = screen.getDisplayMatching(host.win.getBounds()).scaleFactor || 1;
    const c = host.layout ? host.layout.content : { x: 0, y: 0, w: 0, h: 0 };
    const px = { x: Math.round(c.x * scale), y: Math.round(c.y * scale), w: Math.round(c.w * scale), h: Math.round(c.h * scale) };
    for (const id of host.tabs) {
      const helper = this.helpers.get(id);
      if (!helper) continue;
      const visible = id === host.active && Boolean(host.layout && host.layout.visible) && (this.shown.has(id) || this.showEarly)
        && !(this.drag && this.drag.sessionId === id) && px.w > 0 && px.h > 0;
      const key = `${hwnd}:${px.x}:${px.y}:${px.w}:${px.h}:${visible}`;
      if (helper.lastAttach !== key) {
        helper.lastAttach = key;
        helper.send('attach', { parent: hwnd, ...px, visible });
      }
      const sizeKey = `${px.w}x${px.h}@${scale}`;
      if (visible && helper.lastSize !== sizeKey) {
        helper.lastSize = sizeKey;
        helper.send('resize', { w: px.w, h: px.h, scale: Math.round(scale * 100) });
      }
    }
  }

  /** The size a new session should start with: the content area of the main window, in physical pixels. */
  initialSize() {
    const host = this.mainHost();
    const scale = screen.getDisplayMatching(host.win.getBounds()).scaleFactor || 1;
    const c = host.layout && host.layout.content.w > 100 ? host.layout.content : { w: 1280, h: 800 };
    return { width: Math.round(c.w * scale), height: Math.round(c.h * scale), scale: Math.round(scale * 100) };
  }

  push(host) {
    if (!host || host.win.isDestroyed()) return;
    host.win.webContents.send('tabs:update', {
      tabs: host.tabs.map((id) => {
        const s = this.sessions.get(id);
        return s ? { id, name: s.name, host: s.host, state: s.state, connectionId: s.connectionId } : { id, name: 'Session', host: '', state: 'connecting' };
      }),
      active: host.active,
      isMain: host.isMain,
      fullscreen: host.win.isFullScreen(),
      elsewhere: host.isMain && [...this.hosts.values()].some((h) => !h.isMain && h.tabs.length),
    });
    // The main window shows its tab bar as a drop target while tabs live in other windows.
    if (!host.isMain) { const main = this.hosts.get(this.getMainWindow() && this.getMainWindow().id); if (main) this.push(main); }
    if (!host.isMain) host.win.setTitle(`${this.titleOf(host)} – BearingPoint Remote Desktop`);
  }

  /** Called on every session state change. */
  onSessionUpdate(s) {
    const host = this.hostOfSession(s.id);
    if (host) this.push(host);
  }

  // ── Dragging tabs out of a window ──────────────────
  stripAt(point, except) {
    for (const host of this.hosts.values()) {
      if (host === except || host.win.isDestroyed() || !host.win.isVisible() || host.win.isMinimized() || !host.layout || !host.layout.strip) continue;
      const cb = host.win.getContentBounds();
      const s = host.layout.strip;
      // In the main window the whole top area (header and tab bar) accepts a dropped tab.
      const r = host.isMain ? { x: cb.x, y: cb.y - 40, w: cb.width, h: s.y + s.h + 52 } : { x: cb.x + s.x, y: cb.y + s.y - 12, w: s.w, h: s.h + 24 };
      if (point.x >= r.x && point.x <= r.x + r.w && point.y >= r.y && point.y <= r.y + r.h) return host;
    }
    return null;
  }

  dragStart(win, sessionId) {
    const from = this.hostOfSession(sessionId);
    if (!from || this.drag || from.win !== win || from.win.isFullScreen()) return;
    const cursor = screen.getCursorScreenPoint();
    let host;
    if (!from.isMain && from.tabs.length === 1) {
      host = from; // the window holds only this tab: move the window itself
    } else {
      host = this.newHost(cursor);
      this.moveSession(sessionId, host);
      host.win.showInactive();
    }
    const b = host.win.getBounds();
    this.drag = { sessionId, host, from, offset: { x: cursor.x - b.x, y: cursor.y - b.y }, hint: null };
    this.sync(host);
    this.drag.timer = setInterval(() => this.dragTick(), DRAG_TICK_MS);
  }

  dragTick() {
    const d = this.drag;
    if (!d || d.host.win.isDestroyed()) { this.dragEnd(); return; }
    const p = screen.getCursorScreenPoint();
    if (!isPrimaryButtonDown()) { this.dragEnd(); return; }
    d.host.win.setPosition(Math.round(p.x - d.offset.x), Math.round(p.y - d.offset.y));
    const target = this.stripAt(p, d.host);
    if (target !== d.hint) {
      if (d.hint && !d.hint.win.isDestroyed()) d.hint.win.webContents.send('tabs:dropHint', false);
      if (target) target.win.webContents.send('tabs:dropHint', true);
      d.hint = target;
    }
  }

  dragEnd() {
    const d = this.drag;
    if (!d) return;
    clearInterval(d.timer);
    this.drag = null;
    if (d.hint && !d.hint.win.isDestroyed()) d.hint.win.webContents.send('tabs:dropHint', false);
    if (d.host.win.isDestroyed()) return;
    const p = screen.getCursorScreenPoint();
    const target = this.stripAt(p, d.host);
    if (target) {
      this.moveSession(d.sessionId, target);
      this.closeHostLater(d.host);
      target.win.focus();
      this.focusSoon(d.sessionId);
    } else {
      const display = screen.getDisplayNearestPoint(p);
      d.host.win.focus();
      if (p.y <= display.bounds.y + TOP_EDGE_PX) this.setFullscreen(d.host, true);
      this.sync(d.host);
      this.focusSoon(d.sessionId);
    }
    if (d.from !== d.host) this.closeHostLater(d.from);
  }

  // ── Full-screen bar ────────────────────────────────
  // In full screen the tab strip is hidden. A small bar at the top (its own window, so it stays above the
  // session) offers "Leave full screen", "Dock" and "Disconnect". It shrinks to a thin handle when unused.
  updatePinbar(host) {
    if (host.win.isDestroyed()) return;
    if (!host.win.isFullScreen() || !host.active) { this.closePinbar(host); return; }
    if (!host.pinbar || host.pinbar.isDestroyed()) {
      host.pinbar = this.createWindow('pinbar', this.pinbarBounds(host, true), host.win);
      host.pinbar.once('ready-to-show', () => { if (!host.pinbar.isDestroyed()) host.pinbar.showInactive(); });
    } else {
      host.pinbar.setBounds(this.pinbarBounds(host, true));
    }
  }

  pinbarBounds(host, expanded) {
    const d = screen.getDisplayMatching(host.win.getBounds()).bounds;
    const w = expanded ? 520 : 180;
    const h = expanded ? 44 : 8;
    return { x: Math.round(d.x + (d.width - w) / 2), y: d.y, width: w, height: h };
  }

  pinbarFor(win) {
    for (const host of this.hosts.values()) if (host.pinbar === win) return host;
    return null;
  }

  pinbarState(win) {
    const host = this.pinbarFor(win);
    if (!host) return null;
    const s = this.sessions.get(host.active);
    return { name: s ? s.name : '', host: s ? s.host : '', canDock: !host.isMain };
  }

  pinbarAction(win, action) {
    const host = this.pinbarFor(win);
    if (!host) return;
    if (action === 'expand' || action === 'collapse') { host.pinbar.setBounds(this.pinbarBounds(host, action === 'expand')); return; }
    if (action === 'leave') this.setFullscreen(host, false);
    if (action === 'dock' && !host.isMain) { this.setFullscreen(host, false); this.mergeInto(host, this.mainHost()); }
    if (action === 'disconnect' && host.active) this.onDisconnect(host.active);
  }

  closePinbar(host) {
    if (host.pinbar && !host.pinbar.isDestroyed()) host.pinbar.destroy();
    host.pinbar = null;
  }
}

module.exports = { TabManager };
