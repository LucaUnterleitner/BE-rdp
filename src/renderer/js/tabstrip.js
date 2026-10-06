// Session tab strip, shared by the main window and by windows made from dragged-out tabs.
// The remote desktop itself is a native window that the main process places over the session area;
// this module draws the tabs, reports where the session area is, and hides the session while HTML
// dialogs or menus are open (they could not be seen above a native window).

import { icon } from './icons.js';
import { esc } from './ui.js';

const DRAG_THRESHOLD = 24;

const STATE_TEXT = { connecting: 'Connecting', active: 'Connected', reconnecting: 'Reconnecting', ended: 'Ended', failed: 'Failed' };

export function mountTabStrip({ strip, area, showHome = false, onChange = () => {} }) {
  let model = { tabs: [], active: null, isMain: showHome, fullscreen: false };
  let drag = null;

  const announce = (text) => {
    const a = document.getElementById('announcer');
    if (a) { a.textContent = ''; setTimeout(() => { a.textContent = text; }, 30); }
  };

  function tabHtml(t) {
    const selected = t.id === model.active;
    const busy = t.state === 'connecting' || t.state === 'reconnecting';
    return `<div class="stab ${selected ? 'stab--active' : ''}" data-tab="${esc(t.id)}">
      <button class="stab__main" type="button" role="tab" id="stab-${esc(t.id)}" aria-selected="${selected}" tabindex="${selected ? 0 : -1}"
        aria-controls="session-area" aria-label="${esc(t.name)}, ${esc(STATE_TEXT[t.state] || t.state)}" data-tab-id="${esc(t.id)}">
        ${busy ? '<span class="spinner stab__spinner" aria-hidden="true"></span>' : `<span class="stab__dot stab__dot--${esc(t.state)}" aria-hidden="true"></span>`}
        <span class="stab__name">${esc(t.name)}</span></button>
      <button class="stab__close" type="button" data-tab-close="${esc(t.id)}" aria-label="Disconnect ${esc(t.name)}" title="Disconnect">${icon('close', 16)}</button>
    </div>`;
  }

  function render() {
    const home = showHome
      ? `<div class="stab stab--home ${model.active ? '' : 'stab--active'}"><button class="stab__main" type="button" role="tab" id="stab-home" aria-selected="${!model.active}" tabindex="${model.active ? -1 : 0}" data-tab-home>${icon('dashboard', 16)}<span class="stab__name">Home</span></button></div>`
      : '';
    strip.innerHTML = `<div class="tabstrip__tabs" role="tablist" aria-label="Open sessions">${home}${model.tabs.map(tabHtml).join('')}</div>
      <span class="tabstrip__hint">Drag a tab out to open it in its own window</span>`;
    strip.hidden = model.fullscreen || (showHome && !model.tabs.length);
    const active = model.tabs.find((t) => t.id === model.active);
    area.hidden = !active;
    area.innerHTML = active ? placeholderHtml(active) : '';
    document.body.classList.toggle('is-fullscreen', Boolean(model.fullscreen && active));
    onChange(model);
    queueMicrotask(report);
  }

  function placeholderHtml(t) {
    const failed = t.state === 'failed' || t.state === 'ended';
    return `<div class="session-area__placeholder">
      ${failed ? icon('xCircle', 24) : '<span class="spinner" aria-hidden="true"></span>'}
      <p class="session-area__title">${failed ? `Session to ${esc(t.name)} ${t.state === 'failed' ? 'failed' : 'ended'}` : `Connecting to ${esc(t.name)}…`}</p>
      <p class="session-area__msg">${failed ? '' : 'Windows may ask for your password in a separate window.'}</p>
    </div>`;
  }

  // ── Layout for the native session window ──────────
  function overlayOpen() {
    return Boolean(document.querySelector('.overlay, .menu'));
  }

  let reportQueued = false;
  function report() {
    if (reportQueued) return;
    reportQueued = true;
    requestAnimationFrame(() => {
      reportQueued = false;
      const r = area.hidden ? { x: 0, y: 0, width: 0, height: 0 } : area.getBoundingClientRect();
      const s = strip.hidden ? null : strip.getBoundingClientRect();
      window.rdp.tabs.layout({
        content: { x: r.x, y: r.y, w: r.width, h: r.height },
        strip: s ? { x: s.x, y: s.y, w: s.width, h: s.height } : null,
        visible: Boolean(model.active) && !area.hidden && !overlayOpen() && !document.hidden,
      }).catch(() => {});
    });
  }

  new ResizeObserver(report).observe(area);
  new ResizeObserver(report).observe(strip);
  window.addEventListener('resize', report);
  document.addEventListener('visibilitychange', report);
  new MutationObserver(report).observe(document.body, { childList: true });

  // ── Interaction ───────────────────────────────────
  strip.addEventListener('click', (e) => {
    const close = e.target.closest('[data-tab-close]');
    if (close) { window.rdp.tabs.close(close.dataset.tabClose); return; }
    if (e.target.closest('[data-tab-home]')) { window.rdp.tabs.activate(null); return; }
    const tab = e.target.closest('[data-tab-id]');
    if (tab && !drag) window.rdp.tabs.activate(tab.dataset.tabId);
  });

  strip.addEventListener('contextmenu', (e) => {
    const tab = e.target.closest('[data-tab]');
    if (!tab) return;
    e.preventDefault();
    window.rdp.tabs.menu(tab.dataset.tab);
  });

  strip.addEventListener('keydown', (e) => {
    const tabs = [...strip.querySelectorAll('[role="tab"]')];
    const i = tabs.indexOf(document.activeElement);
    if (i < 0) return;
    if (e.key === 'ArrowRight' || e.key === 'ArrowLeft') {
      e.preventDefault();
      tabs[(i + (e.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length].focus();
    }
    if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); document.activeElement.click(); }
    if (e.key === 'Delete' && document.activeElement.dataset.tabId) window.rdp.tabs.close(document.activeElement.dataset.tabId);
  });

  // Dragging a tab out of the strip: the main process follows the cursor from here on.
  strip.addEventListener('pointerdown', (e) => {
    const tab = e.target.closest('[data-tab-id]');
    if (!tab || e.button !== 0 || model.fullscreen) return;
    drag = { id: tab.dataset.tabId, x: e.clientX, y: e.clientY, started: false };
    tab.setPointerCapture(e.pointerId);
  });
  strip.addEventListener('pointermove', (e) => {
    if (!drag || drag.started) return;
    const s = strip.getBoundingClientRect();
    const outside = e.clientY < s.top - DRAG_THRESHOLD || e.clientY > s.bottom + DRAG_THRESHOLD;
    if (outside || Math.abs(e.clientY - drag.y) > DRAG_THRESHOLD * 2) {
      drag.started = true;
      window.rdp.tabs.dragStart(drag.id);
    }
  });
  const endDrag = () => { const d = drag; setTimeout(() => { if (drag === d) drag = null; }, 0); };
  strip.addEventListener('pointerup', endDrag);
  strip.addEventListener('pointercancel', endDrag);

  window.rdp.on('tabs:dropHint', (on) => strip.classList.toggle('tabstrip--drop', Boolean(on)));
  window.rdp.on('tabs:update', (m) => {
    const before = new Set(model.tabs.map((t) => t.id));
    model = m;
    render();
    const added = m.tabs.filter((t) => !before.has(t.id));
    if (added.length) announce(`Tab ${added.map((t) => t.name).join(', ')} opened`);
  });
  window.rdp.tabs.state().catch(() => {});

  return { report, get model() { return model; } };
}
