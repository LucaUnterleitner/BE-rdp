// Shared UI primitives: escaping, toasts, accessible dialogs, menus, tooltips and formatting.

import { icon } from './icons.js';

export function esc(value) {
  return String(value ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

export function $(sel, root = document) { return root.querySelector(sel); }
export function $$(sel, root = document) { return [...root.querySelectorAll(sel)]; }

/** Unwrap {ok, data, error} responses from the main process. */
export async function call(promise) {
  const res = await promise;
  if (!res || !res.ok) throw new Error((res && res.error) || 'The request failed.');
  return res.data;
}

// ── Toasts ─────────────────────────────────────────────
// Only for completed background actions. Critical errors use dialogs or alerts instead.
export function toast(message, { kind = 'success', timeout = 5000 } = {}) {
  const host = $('#toasts');
  const el = document.createElement('div');
  el.className = 'toast';
  el.innerHTML = `${icon(kind === 'info' ? 'info' : 'checkCircle')}<div class="toast__msg">${esc(message)}</div>
    <button class="icon-btn" type="button" aria-label="Dismiss notification">${icon('close', 16)}</button>`;
  const remove = () => el.remove();
  el.querySelector('button').addEventListener('click', remove);
  host.append(el);
  if (timeout) setTimeout(remove, timeout);
}

// ── Dialogs ────────────────────────────────────────────
const FOCUSABLE = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), summary, [tabindex]:not([tabindex="-1"])';
const openStack = [];

/**
 * Open a modal dialog with focus trap, Escape to close and focus restoration.
 * Returns { el, close, setBusy }.
 */
export function openDialog({ title, subtitle = '', body = '', footer = '', size = '', onClose = null, dismissible = true, labelIcon = '' }) {
  const previous = document.activeElement;
  const overlay = document.createElement('div');
  overlay.className = 'overlay';
  const id = `dlg-${Math.random().toString(36).slice(2)}`;
  overlay.innerHTML = `
    <div class="dialog ${size ? `dialog--${size}` : ''}" role="dialog" aria-modal="true" aria-labelledby="${id}-t" ${subtitle ? `aria-describedby="${id}-s"` : ''}>
      <div class="dialog__header">
        ${labelIcon}
        <div class="dialog__titles">
          <h2 class="dialog__title" id="${id}-t">${esc(title)}</h2>
          ${subtitle ? `<p class="dialog__subtitle" id="${id}-s">${subtitle}</p>` : ''}
        </div>
        ${dismissible ? `<button class="icon-btn" type="button" data-close aria-label="Close dialog" data-tooltip="Close">${icon('close')}</button>` : ''}
      </div>
      <div class="dialog__body">${body}</div>
      ${footer ? `<div class="dialog__footer">${footer}</div>` : ''}
    </div>`;
  const dialog = overlay.firstElementChild;
  document.body.append(overlay);
  $('#app').setAttribute('inert', '');

  let closed = false;
  const close = (result) => {
    if (closed) return;
    closed = true;
    overlay.remove();
    openStack.splice(openStack.indexOf(api), 1);
    if (!openStack.length) $('#app').removeAttribute('inert');
    if (previous && document.contains(previous)) previous.focus();
    else if (!openStack.length) { const h = $('#page-title'); if (h) h.focus({ preventScroll: true }); }
    if (onClose) onClose(result);
    document.dispatchEvent(new CustomEvent('bp:dialog-closed'));
  };

  overlay.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && dismissible) { e.stopPropagation(); close(); }
    if (e.key === 'Tab') {
      const items = $$(FOCUSABLE, dialog).filter((n) => n.offsetParent !== null);
      if (!items.length) return;
      const first = items[0];
      const last = items[items.length - 1];
      if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
      else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
    }
  });
  overlay.addEventListener('mousedown', (e) => { if (e.target === overlay && dismissible) close(); });
  $$('[data-close]', dialog).forEach((b) => b.addEventListener('click', () => close()));

  const api = { el: dialog, close };
  openStack.push(api);
  requestAnimationFrame(() => {
    const target = $('[autofocus]', dialog) || $$(FOCUSABLE, $('.dialog__body', dialog))[0] || $$(FOCUSABLE, dialog)[0];
    if (target) target.focus({ preventScroll: true });
    const body = $(".dialog__body", dialog);
    if (body && !body.contains(document.activeElement)) body.scrollTop = 0;
  });
  return api;
}

export function confirmDialog({ title, message, confirmLabel, cancelLabel = 'Cancel', danger = false, detail = '' }) {
  return new Promise((resolve) => {
    let answer = false;
    const dlg = openDialog({
      title,
      size: 'narrow',
      body: `<p>${esc(message)}</p>${detail ? `<p class="muted small" style="margin-top:12px">${esc(detail)}</p>` : ''}`,
      footer: `<button class="btn btn--secondary" type="button" data-close>${esc(cancelLabel)}</button>
               <button class="btn ${danger ? 'btn--danger-solid' : 'btn--primary'}" type="button" data-ok>${esc(confirmLabel)}</button>`,
      onClose: () => resolve(answer),
    });
    $('[data-ok]', dlg.el).addEventListener('click', () => { answer = true; dlg.close(); });
    // Destructive confirmations start on Cancel so a stray Enter does not delete anything.
    requestAnimationFrame(() => (danger ? $('[data-close].btn', dlg.el) : $('[data-ok]', dlg.el)).focus());
  });
}

/** Errors are shown in a dialog that stays until the user closes it (never in a disappearing toast). */
export function errorDialog(title, message, technical = '') {
  const dlg = openDialog({
    title,
    size: 'narrow',
    labelIcon: `<span class="dialog__icon dialog__icon--error">${icon('xCircle', 24)}</span>`,
    body: `<p>${esc(message)}</p>${technical ? `<details class="details"><summary>Technical details</summary><p class="mono small">${esc(technical)}</p></details>` : ''}`,
    footer: '<button class="btn btn--secondary" type="button" data-close>Close</button>',
  });
  requestAnimationFrame(() => $('.dialog__footer [data-close]', dlg.el).focus());
}

/** Polite screen reader announcement through one persistent live region. */
export function announce(message) {
  const region = $('#announcer');
  if (!region) return;
  region.textContent = '';
  setTimeout(() => { region.textContent = message; }, 50);
}

// ── Popover menu ───────────────────────────────────────
let openMenu = null;
export function showMenu(anchor, items) {
  closeMenu();
  const menu = document.createElement('div');
  menu.className = 'menu';
  menu.setAttribute('role', 'menu');
  menu.innerHTML = items.map((it, i) => {
    if (it === 'sep') return '<div class="menu__sep" role="separator"></div>';
    if (it.header) return `<div class="menu__header">${it.header}</div>`;
    return `<button class="menu__item ${it.danger ? 'menu__item--danger' : ''}" role="menuitem" type="button" data-i="${i}" ${it.disabled ? 'disabled' : ''}>${icon(it.icon || 'dot')}<span>${esc(it.label)}</span></button>`;
  }).join('');
  document.body.append(menu);
  const r = anchor.getBoundingClientRect();
  const mw = menu.offsetWidth;
  const mh = menu.offsetHeight;
  menu.style.left = `${Math.max(8, Math.min(r.right - mw, window.innerWidth - mw - 8))}px`;
  menu.style.top = `${r.bottom + 4 + mh > window.innerHeight ? r.top - mh - 4 : r.bottom + 4}px`;
  anchor.setAttribute('aria-expanded', 'true');

  // Keyboard navigation skips disabled items.
  const buttons = $$('.menu__item', menu).filter((b) => !b.disabled);
  buttons[0] && buttons[0].focus();
  menu.addEventListener('click', (e) => {
    const b = e.target.closest('[data-i]');
    if (!b) return;
    const item = items[Number(b.dataset.i)];
    closeMenu(true);
    item.action();
  });
  menu.addEventListener('keydown', (e) => {
    const idx = buttons.indexOf(document.activeElement);
    if (e.key === 'ArrowDown') { e.preventDefault(); buttons[(idx + 1) % buttons.length].focus(); }
    if (e.key === 'ArrowUp') { e.preventDefault(); buttons[(idx - 1 + buttons.length) % buttons.length].focus(); }
    if (e.key === 'Home') { e.preventDefault(); buttons[0].focus(); }
    if (e.key === 'End') { e.preventDefault(); buttons[buttons.length - 1].focus(); }
    if (e.key === 'Escape' || e.key === 'Tab') { e.preventDefault(); closeMenu(); }
  });
  openMenu = { menu, anchor };
}

export function closeMenu(keepFocus = false) {
  if (!openMenu) return;
  const { menu, anchor } = openMenu;
  openMenu = null;
  menu.remove();
  anchor.setAttribute('aria-expanded', 'false');
  if (!keepFocus && document.contains(anchor)) anchor.focus();
}

document.addEventListener('mousedown', (e) => {
  if (openMenu && !openMenu.menu.contains(e.target) && !openMenu.anchor.contains(e.target)) closeMenu(true);
});

// ── Tooltips (for icon-only buttons and the collapsed sidebar) ─
let tip = null;
function showTip(target) {
  const text = target.dataset.tooltip;
  if (!text) return;
  if (target.dataset.tooltipWhen === 'collapsed' && $('#sidebar').dataset.collapsed !== 'true') return;
  hideTip();
  tip = document.createElement('div');
  tip.className = 'tooltip';
  tip.setAttribute('role', 'tooltip');
  tip.textContent = text;
  document.body.append(tip);
  const r = target.getBoundingClientRect();
  const side = target.dataset.tooltipSide;
  if (side === 'right') {
    tip.style.left = `${r.right + 8}px`;
    tip.style.top = `${r.top + r.height / 2 - tip.offsetHeight / 2}px`;
  } else {
    tip.style.left = `${Math.max(8, Math.min(r.left + r.width / 2 - tip.offsetWidth / 2, window.innerWidth - tip.offsetWidth - 8))}px`;
    tip.style.top = `${r.bottom + 6 + tip.offsetHeight > window.innerHeight ? r.top - tip.offsetHeight - 6 : r.bottom + 6}px`;
  }
}
function hideTip() { if (tip) { tip.remove(); tip = null; } }
// Tooltips stay visible while the pointer moves onto them (WCAG 1.4.13) and close with Escape.
document.addEventListener('mouseover', (e) => {
  if (tip && tip.contains(e.target)) return;
  const t = e.target.closest('[data-tooltip]');
  if (t) showTip(t);
});
document.addEventListener('mouseout', (e) => {
  const to = e.relatedTarget;
  if (tip && to && (tip.contains(to) || (e.target.closest('[data-tooltip]') && e.target.closest('[data-tooltip]').contains(to)))) return;
  if (e.target.closest('[data-tooltip]') || (tip && tip.contains(e.target))) hideTip();
});
document.addEventListener('focusin', (e) => { const t = e.target.closest('[data-tooltip]'); if (t && t.matches(':focus-visible')) showTip(t); else hideTip(); });
document.addEventListener('focusout', hideTip);
document.addEventListener('keydown', (e) => { if (e.key === 'Escape') hideTip(); });

// ── Formatting ─────────────────────────────────────────
const timeFmt = new Intl.DateTimeFormat(undefined, { hour: '2-digit', minute: '2-digit' });
const dateFmt = new Intl.DateTimeFormat(undefined, { day: '2-digit', month: 'short', year: 'numeric' });
const dateTimeFmt = new Intl.DateTimeFormat(undefined, { day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit' });

export function formatWhen(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  const today = new Date();
  const yesterday = new Date(); yesterday.setDate(today.getDate() - 1);
  if (d.toDateString() === today.toDateString()) return `Today, ${timeFmt.format(d)}`;
  if (d.toDateString() === yesterday.toDateString()) return `Yesterday, ${timeFmt.format(d)}`;
  return `${dateFmt.format(d)}, ${timeFmt.format(d)}`;
}
export function formatDateTime(iso) { return iso ? dateTimeFmt.format(new Date(iso)) : ''; }
export function formatTime(iso) { return iso ? timeFmt.format(new Date(iso)) : ''; }
export function formatDuration(seconds) {
  const s = Math.max(0, Math.round(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  if (h) return `${h} h ${String(m).padStart(2, '0')} min`;
  if (m) return `${m} min`;
  return `${s} s`;
}
export function initials(name) {
  return String(name || '?').split(/[\s.\\_-]+/).filter(Boolean).slice(-2).map((p) => p[0].toUpperCase()).join('') || '?';
}
