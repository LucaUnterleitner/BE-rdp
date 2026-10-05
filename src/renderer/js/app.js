// BearingPoint Remote Desktop: renderer entry. State, routing, shell and views.

import { icon } from './icons.js';
import {
  esc, $, $$, call, toast, confirmDialog, errorDialog, announce, showMenu, openDialog,
  formatWhen, formatDateTime, formatTime, formatDuration, initials,
} from './ui.js';
import {
  setContext, openConnectDialog, runConnect, openEditDialog, importRdpFiles, openCredentialDialog, removeCredential,
} from './dialogs.js';

const state = {
  info: null,
  settings: null,
  connections: [],
  sessions: [],
  audit: [],
  loaded: false,
  loadError: null,
  route: { name: 'dashboard', id: null },
  filters: { q: '', status: '', environment: '', os: '', favorites: false, recent: false },
  view: readPref('view', 'grid'),
  filtersOpen: readPref('filters', 'closed') === 'open',
  notifications: [],
  unread: 0,
  dismissed: new Set(),
};

const NAV = [
  ['dashboard', 'Dashboard', 'dashboard'],
  ['systems', 'My systems', 'systems'],
  ['favorites', 'Favorites', 'star'],
  ['recent', 'Recent sessions', 'clock'],
  ['sessions', 'Active sessions', 'activity'],
  'sep',
  ['settings', 'Settings', 'settings'],
  ['help', 'Help', 'help'],
];

const ctx = {
  state,
  reload: loadData,
  upsertSession,
  focusSession,
};
setContext(ctx);

// ── Persistence of small view preferences (per viewer, optional) ─
function readPref(key, fallback) {
  try { return localStorage.getItem(`bp-rdp:${key}`) || fallback; } catch { return fallback; }
}
function writePref(key, value) {
  try { localStorage.setItem(`bp-rdp:${key}`, value); } catch { /* storage unavailable */ }
}

// ── Data ──────────────────────────────────────────────
async function loadData() {
  try {
    const [connections, sessions, settings, audit] = await Promise.all([
      call(window.rdp.connections.list()),
      call(window.rdp.sessions.list()),
      call(window.rdp.settings.get()),
      call(window.rdp.audit.list(300)),
    ]);
    Object.assign(state, { connections, sessions, settings, audit, loaded: true, loadError: null });
  } catch (err) {
    state.loadError = err.message;
    state.loadCorrupt = /damaged/.test(err.message);
  }
  render();
}

export const LIVE_STATES = ['connecting', 'active', 'reconnecting'];
const isLive = (st) => LIVE_STATES.includes(st);

function upsertSession(session) {
  const i = state.sessions.findIndex((s) => s.id === session.id);
  if (i === -1) state.sessions.unshift(session); else state.sessions[i] = session;
  for (const c of state.connections) {
    if (c.id !== session.connectionId) continue;
    if (isLive(session.state)) { c.activeSessionId = session.id; c.activeState = session.state; }
    else if (c.activeSessionId === session.id) { c.activeSessionId = null; c.activeState = null; }
  }
}

async function focusSession(id) {
  try { await call(window.rdp.sessions.focus(id)); }
  catch (err) { errorDialog('The session window could not be shown', err.message); }
}

function activeSessions() {
  return state.sessions.filter((s) => isLive(s.state));
}

// ── Status model (text + icon, never color alone) ─────
function statusOf(c) {
  if (c.activeSessionId && c.activeState === 'connecting') return { key: 'busy', label: 'Connecting…', dot: true };
  if (c.activeSessionId && c.activeState === 'reconnecting') return { key: 'busy', label: 'Reconnecting…', icon: 'refresh' };
  if (c.activeSessionId) return { key: 'busy', label: 'Busy · your session', dot: true };
  const s = c.status;
  if (!s) return { key: 'checking', label: 'Checking…', icon: 'question' };
  if (s.reachable) return { key: 'available', label: 'Available', dot: true, latency: s.latencyMs };
  if (s.reason === 'error') return { key: 'unknown', label: 'Unknown', icon: 'question' };
  return { key: 'offline', label: 'Offline', icon: 'xCircle', reason: s.reason };
}

const OFFLINE_REASON = { dns: 'Name not found', refused: 'Remote Desktop not enabled', timeout: 'No response', unreachable: 'Network unreachable' };

function statusBadge(c, { withLatency = false } = {}) {
  const s = statusOf(c);
  const visual = s.dot ? '<span class="status__dot" aria-hidden="true"></span>' : icon(s.icon, 16);
  const title = s.reason ? OFFLINE_REASON[s.reason] || '' : '';
  return `<span class="status status--${s.key}" ${title ? `data-tooltip="${esc(title)}"` : ''}>${visual}${esc(s.label)}${withLatency && s.latency !== undefined ? `<span class="status__latency">${s.latency} ms</span>` : ''}</span>`;
}

function envBadge(env) {
  return `<span class="badge badge--env-chip badge--${esc(String(env).toLowerCase())}">${esc(env)}</span>`;
}

// ── Filtering ─────────────────────────────────────────
/** Number of active filters in the collapsible panel (the search text is not counted). */
function activeFilterCount() {
  const f = state.filters;
  return [f.status, f.environment, f.os, f.favorites, f.recent].filter(Boolean).length;
}

function filtersActive() {
  const f = state.filters;
  return Boolean(f.q || f.status || f.environment || f.os || f.favorites || f.recent);
}

function applyFilters(list) {
  const f = state.filters;
  const q = f.q.trim().toLowerCase();
  const weekAgo = Date.now() - 7 * 86400000;
  return list.filter((c) => {
    if (q) {
      const hay = [c.name, c.host, c.environment, c.os, c.location, c.folder, ...(c.tags || [])].join(' ').toLowerCase();
      if (!q.split(/\s+/).every((t) => hay.includes(t))) return false;
    }
    if (f.status && statusOf(c).key !== f.status) return false;
    if (f.environment && c.environment !== f.environment) return false;
    if (f.os && c.os !== f.os) return false;
    if (f.favorites && !c.favorite) return false;
    if (f.recent && !(c.lastConnectedAt && Date.parse(c.lastConnectedAt) > weekAgo)) return false;
    return true;
  });
}

function sortByName(list) {
  return [...list].sort((a, b) => a.name.localeCompare(b.name));
}

function toolbarHtml({ showFavoritesFilter = true, showViewToggle = false } = {}) {
  const f = state.filters;
  const oses = [...new Set(state.connections.map((c) => c.os).filter(Boolean))].sort();
  const opt = (v, l, cur) => `<option value="${esc(v)}" ${v === cur ? 'selected' : ''}>${esc(l)}</option>`;
  return `<div class="toolbar" role="search">
    <div class="toolbar__row">
    <div class="search">
      <label class="visually-hidden" for="search">Search systems</label>
      ${icon('search')}
      <input class="input" id="search" type="search" value="${esc(f.q)}" placeholder="Search by name, host, environment, OS, location or tag" autocomplete="off" spellcheck="false">
    </div>
    <button class="btn btn--secondary filter-toggle" type="button" data-action="toggle-filters" aria-expanded="${state.filtersOpen}" aria-controls="filter-panel">
      ${icon('filter', 16)} Filters${activeFilterCount() ? ` <span class="filter-toggle__count" aria-label="${activeFilterCount()} active">${activeFilterCount()}</span>` : ''}
      <svg class="icon icon--16 filter-toggle__chevron" viewBox="0 0 24 24" aria-hidden="true"><path d="M6 9l6 6 6-6"/></svg>
    </button>
    ${!state.filtersOpen && activeFilterCount() ? `<button class="btn btn--ghost" type="button" data-action="clear-filters">${icon('close', 16)} Clear</button>` : ''}
    ${showViewToggle ? `<div class="view-toggle" role="group" aria-label="Layout">
      <button type="button" data-view="grid" aria-pressed="${state.view === 'grid'}" aria-label="Show as cards" data-tooltip="Cards">${icon('grid')}</button>
      <button type="button" data-view="list" aria-pressed="${state.view === 'list'}" aria-label="Show as list" data-tooltip="List">${icon('list')}</button>
    </div>` : ''}
    </div>
    <div class="toolbar__row toolbar__filters" id="filter-panel" ${state.filtersOpen ? '' : 'hidden'}>
    <div class="filter"><label class="field__label" for="f-status">Status</label>
      <select class="select" id="f-status" data-filter="status">${opt('', 'All', f.status)}${opt('available', 'Available', f.status)}${opt('busy', 'Busy', f.status)}${opt('offline', 'Offline', f.status)}${opt('unknown', 'Unknown', f.status)}</select></div>
    <div class="filter"><label class="field__label" for="f-env">Environment</label>
      <select class="select" id="f-env" data-filter="environment">${opt('', 'All', f.environment)}${['Production', 'Test', 'Development', 'Internal'].map((e) => opt(e, e, f.environment)).join('')}</select></div>
    <div class="filter"><label class="field__label" for="f-os">Operating system</label>
      <select class="select" id="f-os" data-filter="os">${opt('', 'All', f.os)}${oses.map((o) => opt(o, o, f.os)).join('')}</select></div>
    ${showFavoritesFilter ? `<label class="check toolbar__check"><input type="checkbox" data-filter="favorites" ${f.favorites ? 'checked' : ''}><span>Favorites</span></label>` : ''}
    <label class="check toolbar__check"><input type="checkbox" data-filter="recent" ${f.recent ? 'checked' : ''}><span>Recently used</span></label>
    <button class="btn btn--ghost toolbar__clear" type="button" data-action="clear-filters" ${filtersActive() ? '' : 'disabled'}>${icon('close', 16)} Clear filters</button>
    </div>
  </div>`;
}

// ── Components ────────────────────────────────────────
function serverCard(c) {
  const st = statusOf(c);
  const connected = Boolean(c.activeSessionId);
  return `<article class="card ${connected ? 'card--connected' : ''}" aria-label="${esc(c.name)}">
    <div class="card__top">
      <div class="card__icon">${icon(/windows 1\d/i.test(c.os) ? 'monitor' : 'server')}</div>
      <div class="card__top-actions">
        <button class="icon-btn fav-btn" type="button" data-action="favorite" data-id="${esc(c.id)}" aria-pressed="${c.favorite}" aria-label="${c.favorite ? `Remove ${esc(c.name)} from favorites` : `Add ${esc(c.name)} to favorites`}" data-tooltip="${c.favorite ? 'Remove from favorites' : 'Add to favorites'}">${icon('star')}</button>
        <button class="icon-btn" type="button" data-action="menu" data-id="${esc(c.id)}" aria-haspopup="menu" aria-expanded="false" aria-label="More actions for ${esc(c.name)}" data-tooltip="More actions">${icon('more')}</button>
      </div>
    </div>
    <div>
      <h3><button class="card__title-btn" type="button" data-action="details" data-id="${esc(c.id)}">${esc(c.name)}</button></h3>
      <p class="card__host">${esc(c.host)}${c.port !== 3389 ? `:${c.port}` : ''}</p>
      ${c.os ? `<p class="card__meta">${esc(c.os)}${c.location ? ` · ${esc(c.location)}` : ''}</p>` : (c.location ? `<p class="card__meta">${esc(c.location)}</p>` : '')}
    </div>
    <div class="card__row">${statusBadge(c)} ${envBadge(c.environment)} ${c.sample ? '<span class="badge badge--sample" data-tooltip="Mock data for demonstration">Sample</span>' : ''}${c.source === 'central' ? `<span class="badge" data-tooltip="Provided by IT. Read-only.">${icon('shield', 16)} Managed by IT</span>` : ''}</div>
    <div class="card__footer">
      <span class="card__meta">${c.lastConnectedAt ? `Last used: ${esc(formatWhen(c.lastConnectedAt))}` : 'Not used yet'}</span>
      ${connected && c.activeState === 'connecting'
        ? `<button class="btn btn--secondary" type="button" data-action="focus" data-session="${esc(c.activeSessionId)}" aria-label="Connecting to ${esc(c.name)}. Show the Remote Desktop window"><span class="spinner" aria-hidden="true"></span> Connecting…</button>`
        : connected
        ? `<button class="btn btn--secondary" type="button" data-action="focus" data-session="${esc(c.activeSessionId)}">${icon('window')} Show</button>`
        : `<button class="btn btn--primary" type="button" data-action="connect" data-id="${esc(c.id)}">Connect ${icon('arrowRight')}</button>`}
    </div>
  </article>`;
}

function serverRow(c) {
  return `<div class="list__row" role="row">
    <div class="list__name" role="cell">
      <button class="card__title-btn" type="button" data-action="details" data-id="${esc(c.id)}" style="font:var(--text-body-strong)">${esc(c.name)}</button>
      <span class="card__host">${esc(c.host)}</span>
    </div>
    <div role="cell" class="small">${esc(c.os || '–')}</div>
    <div role="cell">${envBadge(c.environment)}</div>
    <div role="cell">${statusBadge(c)}</div>
    <div class="list__actions" role="cell">
      <button class="icon-btn fav-btn" type="button" data-action="favorite" data-id="${esc(c.id)}" aria-pressed="${c.favorite}" aria-label="${c.favorite ? `Remove ${esc(c.name)} from favorites` : `Add ${esc(c.name)} to favorites`}" data-tooltip="${c.favorite ? 'Remove from favorites' : 'Add to favorites'}">${icon('star')}</button>
      <button class="icon-btn" type="button" data-action="menu" data-id="${esc(c.id)}" aria-haspopup="menu" aria-expanded="false" aria-label="More actions for ${esc(c.name)}" data-tooltip="More actions">${icon('more')}</button>
      ${c.activeSessionId
        ? `<button class="btn btn--secondary btn--small" type="button" data-action="focus" data-session="${esc(c.activeSessionId)}">Show</button>`
        : `<button class="btn btn--primary btn--small" type="button" data-action="connect" data-id="${esc(c.id)}">Connect</button>`}
    </div>
  </div>`;
}

function systemsBlock(list) {
  if (state.view === 'list') {
    return `<div class="list" role="table" aria-label="Systems">
      <div class="list__row list__head" role="row"><span role="columnheader">Name</span><span role="columnheader">Operating system</span><span role="columnheader">Environment</span><span role="columnheader">Status</span><span role="columnheader" class="visually-hidden">Actions</span></div>
      ${list.map(serverRow).join('')}</div>`;
  }
  return `<div class="grid">${list.map(serverCard).join('')}</div>`;
}

function emptyState({ title, message, actions = '', iconName = 'search' }) {
  return `<div class="empty"><div class="empty__icon">${icon(iconName)}</div><p class="empty__title">${esc(title)}</p><p class="empty__msg">${esc(message)}</p>${actions ? `<div class="empty__actions">${actions}</div>` : ''}</div>`;
}

function noSystemsState() {
  return emptyState({
    iconName: 'server',
    title: 'No systems yet',
    message: 'Add the remote systems you work with, or import existing Remote Desktop (.rdp) files.',
    actions: `<button class="btn btn--primary" type="button" data-action="add">${icon('plus')} Add system</button>
              <button class="btn btn--secondary" type="button" data-action="import">${icon('upload')} Import connection files</button>
              <button class="btn btn--ghost" type="button" data-action="samples">Load sample systems (mock data)</button>`,
  });
}

function noResultsState() {
  return emptyState({ title: 'No systems found', message: 'Try changing the filters or search term.', actions: '<button class="btn btn--secondary" type="button" data-action="clear-filters">Clear filters</button>' });
}

function skeletonGrid(n = 6) {
  return `<div class="grid" aria-busy="true" aria-label="Loading systems">${'<div class="skeleton skeleton-card"></div>'.repeat(n)}</div>`;
}

function sessionStateLabel(s) {
  const map = {
    connecting: ['Connecting', '<span class="spinner" style="width:14px;height:14px" aria-hidden="true"></span>'],
    active: ['Active', icon('checkCircle', 16)],
    reconnecting: ['Reconnecting', icon('refresh', 16)],
    ended: ['Ended', icon('dot', 16)],
    failed: ['Failed', icon('xCircle', 16)],
  };
  const [label, ic] = map[s.state] || ['Unknown', icon('question', 16)];
  return `<span class="session-state session-state--${s.state}">${ic}${label}</span>`;
}

function sessionCard(s) {
  const live = isLive(s.state);
  const since = s.connectedAt || s.startedAt;
  const duration = live ? (Date.now() - Date.parse(since)) / 1000 : (s.connectedAt && s.endedAt ? (Date.parse(s.endedAt) - Date.parse(s.connectedAt)) / 1000 : 0);
  const r = s.result;
  return `<article class="session" aria-label="Session ${esc(s.name)}">
    <div>
      <div class="card__row" style="justify-content:space-between">
        <div><h3 class="card__title">${esc(s.name)}</h3><p class="card__host">${esc(s.host)}${s.gateway ? ` via ${esc(s.gateway)}` : ''}</p></div>
        ${sessionStateLabel(s)}
      </div>
      <div class="session__facts">
        <div><div class="fact__label">Started</div><div class="fact__value">${esc(formatWhen(s.startedAt))}</div></div>
        <div><div class="fact__label">Duration</div><div class="fact__value" ${live ? `data-duration-since="${esc(since)}"` : ''}>${duration ? formatDuration(duration) : '–'}</div></div>
        <div><div class="fact__label">Display</div><div class="fact__value">${esc(s.displayMode)}</div></div>
        <div><div class="fact__label">Launch</div><div class="fact__value">${s.launchMode === 'direct' ? 'Direct' : s.signed ? 'Signed file' : 'Connection file'}</div></div>
      </div>
      ${r && !live ? `<div class="session__result"><div class="alert alert--${r.kind === 'error' ? 'error' : 'info'}">${icon(r.kind === 'error' ? 'xCircle' : 'info')}<div class="alert__body"><p class="alert__title">${esc(r.title)}</p><p class="alert__msg">${esc(r.message)}</p></div></div></div>` : ''}
      <details class="details"><summary>View session details</summary>
        <dl class="kv">
          <dt>Session ID</dt><dd class="mono">${esc(s.id)}</dd>
          <dt>Client process</dt><dd class="mono">mstsc.exe (PID ${esc(s.pid || '–')})</dd>
          <dt>Started</dt><dd>${esc(formatDateTime(s.startedAt))}</dd>
          ${s.connectedAt ? `<dt>Connected</dt><dd>${esc(formatDateTime(s.connectedAt))}${s.verified ? '' : ' (not confirmed by the event log)'}</dd>` : ''}
          ${s.endedAt ? `<dt>Ended</dt><dd>${esc(formatDateTime(s.endedAt))}</dd>` : ''}
          ${r && r.code ? `<dt>Technical code</dt><dd class="mono">${esc(r.code)}</dd>` : ''}
        </dl>
      </details>
    </div>
    <div class="session__actions">
      ${live ? `<button class="btn btn--secondary" type="button" data-action="focus" data-session="${esc(s.id)}">${icon('window')} Show window</button>
                <button class="btn btn--danger" type="button" data-action="disconnect" data-session="${esc(s.id)}">${icon('disconnect')} Disconnect</button>`
             : `<button class="btn btn--primary" type="button" data-action="reconnect" data-id="${esc(s.connectionId)}">${icon('refresh')} Reconnect</button>`}
    </div>
  </article>`;
}

// ── Views ─────────────────────────────────────────────
function pageHeader(title, desc, actions = '') {
  return `<header class="page__header"><div><h1 class="page__title" id="page-title" tabindex="-1">${esc(title)}</h1>${desc ? `<p class="page__desc">${esc(desc)}</p>` : ''}</div>${actions ? `<div class="page__actions">${actions}</div>` : ''}</header>`;
}

const addImportActions = `<button class="btn btn--secondary" type="button" data-action="import">${icon('upload')} Import</button>
  <button class="btn btn--primary" type="button" data-action="add">${icon('plus')} Add system</button>`;

/**
 * Looks offline when the browser reports no network, or when every checked system fails with
 * a network-level reason (no name resolution or no route), which usually means VPN is off.
 */
function looksOffline() {
  if (!navigator.onLine) return true;
  const checked = state.connections.filter((c) => c.status);
  if (checked.length < 2) return false;
  return checked.every((c) => !c.status.reachable && ['dns', 'unreachable', 'timeout'].includes(c.status.reason))
    && checked.some((c) => !c.sample);
}

function globalBanners() {
  const out = [];
  if (state.loaded && looksOffline()) {
    out.push(`<div class="alert alert--warning">${icon('warning')}<div class="alert__body"><p class="alert__title">You seem to be offline or not on the VPN</p>
      <p class="alert__msg">None of your systems can be reached. Check your network connection and connect to the BearingPoint VPN, then check again.</p>
      <div class="alert__actions"><button class="btn btn--secondary btn--small" type="button" data-action="probe-all">${icon('refresh', 16)} Check again</button></div></div></div>`);
  }
  const central = state.info.central || {};
  if (central.configured && central.state === 'error' && !state.dismissed.has('central')) {
    out.push(`<div class="alert alert--error">${icon('xCircle')}<div class="alert__body"><p class="alert__title">The IT system list could not be loaded</p>
      <p class="alert__msg">${esc(central.error || '')} Your personal systems are not affected.</p>
      <div class="alert__actions"><button class="btn btn--secondary btn--small" type="button" data-action="refresh-central">${icon('refresh', 16)} Try again</button></div></div>
      <button class="icon-btn" type="button" data-action="dismiss" data-key="central" aria-label="Dismiss">${icon('close', 16)}</button></div>`);
  } else if (central.configured && (central.state === 'offline' || central.stale) && !state.dismissed.has('central')) {
    out.push(`<div class="alert alert--info">${icon('info')}<div class="alert__body"><p class="alert__title">IT system list from ${esc(formatWhen(central.fetchedAt))}</p>
      <p class="alert__msg">The current list could not be loaded, so the last verified copy is shown. Systems may have changed since then.</p></div>
      <button class="icon-btn" type="button" data-action="dismiss" data-key="central" aria-label="Dismiss">${icon('close', 16)}</button></div>`);
  }
  return out.length ? `<div class="banners">${out.join('')}</div>` : '';
}

function attentionAlerts() {
  const alerts = [];
  const policy = state.info.policy || {};
  if (policy.notice && !state.dismissed.has('policy-notice')) {
    alerts.push(`<div class="alert alert--warning">${icon('warning')}<div class="alert__body"><p class="alert__title">${esc(policy.notice.title || 'Notice from IT')}</p><p class="alert__msg">${esc(policy.notice.message || '')}</p></div>
      <button class="icon-btn" type="button" data-action="dismiss" data-key="policy-notice" aria-label="Dismiss notice">${icon('close', 16)}</button></div>`);
  }
  const failed = state.sessions.filter((s) => s.state === 'failed' && !state.dismissed.has(s.id)).slice(0, 2);
  for (const s of failed) {
    alerts.push(`<div class="alert alert--error">${icon('xCircle')}<div class="alert__body">
      <p class="alert__title">Connection to ${esc(s.name)} failed</p><p class="alert__msg">${esc(s.result ? s.result.title : '')}. ${esc(s.result ? s.result.message : '')}</p>
      <div class="alert__actions"><button class="btn btn--secondary btn--small" type="button" data-action="reconnect" data-id="${esc(s.connectionId)}">Try again</button></div></div>
      <button class="icon-btn" type="button" data-action="dismiss" data-key="${esc(s.id)}" aria-label="Dismiss">${icon('close', 16)}</button></div>`);
  }
  return alerts.length ? `<div class="section" style="margin-top:0;margin-bottom:24px">${alerts.join('')}</div>` : '';
}

function viewDashboard() {
  const all = state.connections;
  const active = activeSessions();
  const available = all.filter((c) => statusOf(c).key === 'available').length;
  let content;
  if (!state.loaded) content = skeletonGrid();
  else if (!all.length) content = noSystemsState();
  else if (filtersActive()) {
    const list = sortByName(applyFilters(all));
    content = `<p class="result-count">${list.length} of ${all.length} systems</p>${list.length ? systemsBlock(list) : noResultsState()}`;
  } else {
    const favs = sortByName(all.filter((c) => c.favorite));
    const recentEvents = state.audit.filter((e) => ['session_ended', 'connect_blocked'].includes(e.event)).slice(0, 5);
    content = `
      ${active.length ? `<section class="section" style="margin-top:0" aria-labelledby="h-active"><div class="section__header"><h2 class="section__title" id="h-active">Active sessions</h2><button class="btn btn--ghost btn--small" type="button" data-nav="sessions">View all ${icon('arrowRight', 16)}</button></div>${active.slice(0, 3).map(sessionCard).join('')}</section>` : ''}
      ${favs.length ? `<section class="section" ${active.length ? '' : 'style="margin-top:0"'} aria-labelledby="h-fav"><div class="section__header"><h2 class="section__title" id="h-fav">Favorites</h2><span class="section__meta">${favs.length} ${favs.length === 1 ? 'system' : 'systems'}</span></div>${systemsBlock(favs)}</section>` : ''}
      <section class="section" aria-labelledby="h-all"><div class="section__header"><h2 class="section__title" id="h-all">All systems</h2><span class="section__meta">${available} of ${all.length} available</span></div>${systemsBlock(sortByName(all))}</section>
      <section class="section" aria-labelledby="h-recent"><div class="section__header"><h2 class="section__title" id="h-recent">Recent activity</h2>${recentEvents.length ? `<button class="btn btn--ghost btn--small" type="button" data-nav="recent">View all ${icon('arrowRight', 16)}</button>` : ''}</div>
        ${recentEvents.length ? historyTable(recentEvents) : emptyState({ iconName: 'clock', title: 'No connections yet', message: 'Your recent connections will appear here.' })}</section>`;
  }
  return `<div class="page">${pageHeader('Dashboard', 'Find a system, start a session and see what needs your attention.', state.connections.length ? addImportActions : '')}
    ${state.loaded ? attentionAlerts() : ''}
    ${state.connections.length ? toolbarHtml({ showViewToggle: true }) : ''}
    ${content}</div>`;
}

function viewSystems() {
  let content;
  if (!state.loaded) content = skeletonGrid();
  else if (!state.connections.length) content = noSystemsState();
  else {
    const list = sortByName(applyFilters(state.connections));
    if (!list.length) content = noResultsState();
    else {
      const groups = new Map();
      for (const c of list) {
        const k = c.folder || 'Ungrouped';
        if (!groups.has(k)) groups.set(k, []);
        groups.get(k).push(c);
      }
      const names = [...groups.keys()].sort((a, b) => (a === 'Ungrouped') - (b === 'Ungrouped') || a.localeCompare(b));
      content = `<p class="result-count">${list.length} of ${state.connections.length} systems</p>` + names.map((n, i) => `
        <section class="section" ${i === 0 ? 'style="margin-top:0"' : ''} aria-labelledby="g-${i}">
          <div class="section__header"><h2 class="section__title" id="g-${i}">${icon('folder')} ${esc(n)}</h2><span class="section__meta">${groups.get(n).length}</span></div>
          ${systemsBlock(groups.get(n))}
        </section>`).join('');
    }
  }
  return `<div class="page">${pageHeader('My systems', 'All remote systems you can connect to, grouped by team or purpose.', addImportActions)}
    ${state.connections.length ? toolbarHtml({ showViewToggle: true }) : ''}${content}</div>`;
}

function viewFavorites() {
  const favs = sortByName(state.connections.filter((c) => c.favorite));
  const content = !state.loaded ? skeletonGrid(3)
    : favs.length ? systemsBlock(favs)
    : emptyState({ iconName: 'star', title: 'No favorites yet', message: 'Select the star on a system to keep it here for quick access.', actions: '<button class="btn btn--secondary" type="button" data-nav="systems">Go to My systems</button>' });
  return `<div class="page">${pageHeader('Favorites', 'Systems you use most often.')}${content}</div>`;
}

function historyTable(events) {
  return `<div class="table-wrap"><table class="table">
    <thead><tr><th scope="col">System</th><th scope="col">When</th><th scope="col">Duration</th><th scope="col">Result</th><th scope="col"><span class="visually-hidden">Actions</span></th></tr></thead>
    <tbody>${events.map((e) => {
      const conn = state.connections.find((c) => c.id === e.connectionId);
      const ok = e.event === 'session_ended' && e.state === 'ended';
      const result = e.event === 'connect_blocked'
        ? `<span class="status status--offline">${icon('xCircle', 16)} Not reachable</span>`
        : ok ? `<span class="status status--available">${icon('checkCircle', 16)} ${esc(e.title || 'Ended')}</span>`
        : `<span class="status status--offline">${icon('xCircle', 16)} ${esc(e.title || 'Failed')}${e.code ? ` <span class="mono small">(${esc(e.code)})</span>` : ''}</span>`;
      return `<tr>
        <td><strong>${esc(e.name || e.host)}</strong><div class="card__host">${esc(e.host)}</div></td>
        <td>${esc(formatWhen(e.ts))}</td>
        <td>${e.durationSec ? esc(formatDuration(e.durationSec)) : '–'}</td>
        <td>${result}</td>
        <td style="text-align:right">${conn ? `<button class="btn btn--secondary btn--small" type="button" data-action="connect" data-id="${esc(conn.id)}">Connect</button>` : `<button class="btn btn--secondary btn--small" type="button" data-action="quick-host" data-host="${esc(e.host || '')}" data-tooltip="Quick connect (not saved as a system)">Connect</button>`}</td>
      </tr>`;
    }).join('')}</tbody></table></div>`;
}

function viewRecent() {
  const events = state.audit.filter((e) => ['session_ended', 'connect_blocked'].includes(e.event));
  return `<div class="page">${pageHeader('Recent sessions', 'Your connection history on this computer. Stored locally, without passwords.')}
    ${events.length ? historyTable(events.slice(0, 100)) : emptyState({ iconName: 'clock', title: 'No sessions yet', message: 'Sessions you start appear here with their result and duration.' })}</div>`;
}

function viewSessions() {
  const live = activeSessions();
  const ended = state.sessions.filter((s) => !isLive(s.state));
  return `<div class="page">${pageHeader('Active sessions', 'Sessions started from this app. Each session runs in its own Remote Desktop window.')}
    <div class="alert alert--info" style="margin-bottom:24px">${icon('info')}<div class="alert__body"><p class="alert__title">Disconnect is not the same as sign out</p>
      <p class="alert__msg">Disconnect closes the window on this computer. Your session and open programs keep running on the server, and you can reconnect later. To end the session completely, select Sign out inside the remote session.</p></div></div>
    ${live.length ? live.map(sessionCard).join('') : emptyState({ iconName: 'activity', title: 'No active sessions', message: 'Connect to a system to start a session.', actions: '<button class="btn btn--secondary" type="button" data-nav="dashboard">Go to dashboard</button>' })}
    ${ended.length ? `<section class="section"><div class="section__header"><h2 class="section__title">Ended in this app session</h2><button class="btn btn--ghost btn--small" type="button" data-action="clear-ended">Clear list</button></div>${ended.map(sessionCard).join('')}</section>` : ''}
  </div>`;
}

function viewDetails(id) {
  const c = state.connections.find((x) => x.id === id);
  if (!c) return `<div class="page">${backLink()}${emptyState({ iconName: 'server', title: 'System not found', message: 'It may have been removed.', actions: '<button class="btn btn--secondary" type="button" data-nav="systems">Go to My systems</button>' })}</div>`;
  const history = state.audit.filter((e) => e.connectionId === c.id && ['session_ended', 'connect_blocked'].includes(e.event)).slice(0, 10);
  const st = statusOf(c);
  const yes = (b) => (b ? 'On' : 'Off');
  const lock = (state.info.policy && state.info.policy.redirect) || {};
  const eff = (k) => (k in lock ? lock[k] : c.redirect[k]);
  const by = (k) => (k in lock ? ' <span class="policy-note">(IT policy)</span>' : '');
  const audio = { local: 'Play on this computer', remote: 'Play on remote computer', none: 'Do not play' }[((state.info.policy || {}).redirect || {}).audio || c.redirect.audio];
  const prot = { none: 'Standard sign-in', remoteGuard: 'Remote Credential Guard', restrictedAdmin: 'Restricted Admin mode' }[c.security.credentialProtection];
  return `<div class="page">${backLink()}
    ${pageHeader(c.name, '', `
      ${c.source === 'central' ? '' : `<button class="btn btn--secondary" type="button" data-action="edit" data-id="${esc(c.id)}">${icon('edit')} Edit</button>`}
      ${c.activeSessionId ? `<button class="btn btn--secondary" type="button" data-action="focus" data-session="${esc(c.activeSessionId)}">${icon('window')} Show window</button>`
        : `<button class="btn btn--primary" type="button" data-action="connect" data-id="${esc(c.id)}">Connect ${icon('arrowRight')}</button>`}`)}
    <div class="card__row" style="margin:-12px 0 24px">${statusBadge(c, { withLatency: true })} ${envBadge(c.environment)} ${c.sample ? '<span class="badge badge--sample">Sample (mock data)</span>' : ''}${c.source === 'central' ? `<span class="badge">${icon('shield', 16)} Managed by IT (read-only)</span>` : ''}
      <button class="btn btn--ghost btn--small" type="button" data-action="probe" data-id="${esc(c.id)}">${icon('refresh', 16)} Check now</button></div>
    ${st.key === 'offline' ? `<div class="alert alert--error" style="margin-bottom:24px">${icon('xCircle')}<div class="alert__body"><p class="alert__title">System not reachable</p><p class="alert__msg">${esc(OFFLINE_REASON[st.reason] || 'No response')}. Check that the system is online and that the VPN is active. Last checked ${esc(formatTime(c.status.checkedAt))}.</p></div></div>` : ''}
    <div class="two-col">
      <div>
        <div class="settings-card">
          <h2 class="settings-card__title">System</h2>
          <dl class="kv" style="font:var(--text-body);margin-top:12px">
            <dt>Computer name</dt><dd class="mono">${esc(c.host)}${c.port !== 3389 ? `:${c.port}` : ''}</dd>
            <dt>Operating system</dt><dd>${esc(c.os || '–')}</dd>
            <dt>Location</dt><dd>${esc(c.location || '–')}</dd>
            <dt>Group</dt><dd>${esc(c.folder || '–')}</dd>
            <dt>Tags</dt><dd>${c.tags.length ? c.tags.map((t) => `<span class="badge">${esc(t)}</span>`).join(' ') : '–'}</dd>
            <dt>Last used</dt><dd>${esc(c.lastConnectedAt ? formatWhen(c.lastConnectedAt) : 'Never')}</dd>
            ${c.description ? `<dt>Description</dt><dd>${esc(c.description)}</dd>` : ''}
          </dl>
        </div>
        <section class="section"><div class="section__header"><h2 class="section__title">Recent sessions</h2></div>
          ${history.length ? historyTable(history) : emptyState({ iconName: 'clock', title: 'No sessions yet', message: 'Sessions to this system appear here.' })}</section>
      </div>
      <div class="settings">
        <div class="settings-card">
          <h2 class="settings-card__title">Sign-in</h2>
          <dl class="kv" style="margin:12px 0 16px"><dt>Username</dt><dd>${esc(c.username || 'Suggested by Windows')}</dd><dt>Password</dt><dd data-cred>Checking…</dd></dl>
          <div class="page__actions" data-cred-actions></div>
        </div>
        <div class="settings-card">
          <h2 class="settings-card__title">Connection options</h2>
          <dl class="kv" style="margin-top:12px">
            <dt>Display</dt><dd>${c.display.mode === 'window' ? `Window ${c.display.width} × ${c.display.height}` : 'Full screen'}${c.display.multimon ? ', all monitors' : ''}</dd>
            <dt>Clipboard</dt><dd>${yes(eff('clipboard'))}${by('clipboard')}</dd>
            <dt>Local drives</dt><dd>${yes(eff('drives') === 'all')}${by('drives')}</dd>
            <dt>Printers</dt><dd>${yes(eff('printers'))}${by('printers')}</dd>
            <dt>Audio</dt><dd>${esc(audio)}</dd>
            <dt>Microphone</dt><dd>${yes(eff('microphone'))}${by('microphone')}</dd>
            <dt>Smart cards</dt><dd>${yes(eff('smartcards'))}${by('smartcards')}</dd>
            <dt>RD Gateway</dt><dd>${c.gateway.mode === 'none' ? 'Not used' : `${esc(c.gateway.host)} (${c.gateway.mode === 'always' ? 'always' : 'if needed'})`}</dd>
            <dt>Credentials</dt><dd>${esc(prot)}</dd>
            <dt>Admin session</dt><dd>${yes(c.security.adminSession)}</dd>
          </dl>
        </div>
        <div class="settings-card">
          <h2 class="settings-card__title">More actions</h2>
          <div class="page__actions" style="margin-top:12px">
            <button class="btn btn--secondary" type="button" data-action="export" data-id="${esc(c.id)}">${icon('download')} Export .rdp</button>
            <button class="btn btn--secondary" type="button" data-action="duplicate" data-id="${esc(c.id)}">${icon('copy')} Duplicate</button>
            ${c.source === 'central' ? '' : `<button class="btn btn--danger" type="button" data-action="delete" data-id="${esc(c.id)}">${icon('trash')} Remove system</button>`}
          </div>
        </div>
      </div>
    </div></div>`;
}

function backLink() {
  return `<button class="back-link" type="button" data-action="back">${icon('arrowLeft', 16)} Back</button>`;
}

async function hydrateCredentials(c) {
  const dd = $('[data-cred]');
  const actions = $('[data-cred-actions]');
  if (!dd) return;
  const allowSaved = (state.info.policy || {}).allowSavedCredentials !== false;
  try {
    const cred = await call(window.rdp.credentials.get(c.id));
    if (!document.contains(dd)) return;
    dd.innerHTML = cred.saved ? `Saved for ${esc(cred.username)} in Windows Credential Manager` : 'Not saved. Windows asks when you connect.';
    actions.innerHTML = cred.saved
      ? `<button class="btn btn--secondary" type="button" data-action="save-cred" data-id="${esc(c.id)}">${icon('key')} Replace password</button>
         <button class="btn btn--danger" type="button" data-action="remove-cred" data-id="${esc(c.id)}">Remove saved password</button>`
      : allowSaved ? `<button class="btn btn--secondary" type="button" data-action="save-cred" data-id="${esc(c.id)}">${icon('key')} Save password</button>` : `<span class="policy-note">${icon('shield', 16)} Saving passwords is switched off by IT policy</span>`;
  } catch {
    dd.textContent = 'Windows Credential Manager is not available.';
  }
}

function viewSettings() {
  const s = state.settings;
  if (!s) return `<div class="page">${pageHeader('Settings', '')}${skeletonGrid(2)}</div>`;
  const p = state.info.policy || {};
  const d = s.defaults;
  const launch = state.info.launch || {};
  const effMode = launch.launchMode || s.launchMode;
  const launchLock = p.launchMode ? 'The launch mode is set by IT policy.' : launch.note || '';
  return `<div class="page">${pageHeader('Settings', 'Preferences for this computer. Settings of individual systems are edited on the system.')}
    <form id="settings-form" class="settings" novalidate>
      <section class="settings-card" aria-labelledby="st-launch">
        <h2 class="settings-card__title" id="st-launch">How connections start</h2>
        <p class="settings-card__desc">Since April 2026, Windows asks for confirmation each time a Remote Desktop file is opened, unless the file is signed by a publisher your IT trusts.</p>
        <fieldset class="radio-group" ${launchLock ? 'disabled' : ''}><legend class="visually-hidden">Launch mode</legend>${launchLock ? `<p class="policy-note" style="margin-bottom:8px">${icon('shield', 16)} ${esc(launchLock)}</p>` : ''}
          <label class="check"><input type="radio" name="launchMode" value="file" ${effMode === 'file' ? 'checked' : ''}><span class="check__text"><span>Use a connection file (recommended)</span><span class="check__help">All options apply: username, devices, clipboard, gateway. Windows shows its security confirmation unless the file is signed.</span></span></label>
          <label class="check"><input type="radio" name="launchMode" value="direct" ${effMode === 'direct' ? 'checked' : ''}><span class="check__text"><span>Start directly without a file</span><span class="check__help">No confirmation dialog. Only display, multi-monitor, gateway, admin and credential protection apply; device redirection and username come from Windows' Default.rdp.</span></span></label>
        </fieldset>
        <div class="field" style="margin-top:16px"><label class="field__label" for="st-thumb">Signing certificate thumbprint (SHA-256, optional)</label>
          <input class="input mono" id="st-thumb" name="signingThumbprint" value="${esc(p.signingThumbprint || s.signingThumbprint)}" ${p.signingThumbprint ? 'disabled' : ''} placeholder="Provided by IT" spellcheck="false" autocomplete="off">
          <span class="field__help">When set, connection files are signed with rdpsign.exe. The certificate must be installed on this computer and trusted by Group Policy.</span></div>
        ${check('showConnectDialog', s.showConnectDialog !== false, 'Show connection options before connecting', 'When off, Connect starts immediately with the saved options of the system.')}
      </section>
      <section class="settings-card" aria-labelledby="st-new">
        <h2 class="settings-card__title" id="st-new">Defaults for new systems</h2>
        <p class="settings-card__desc">Least-privilege defaults. Each system can override them.</p>
        <div class="field"><label class="field__label" for="st-mode">Display</label>
          <select class="select" id="st-mode" name="defMode"><option value="fullscreen" ${d.display.mode === 'fullscreen' ? 'selected' : ''}>Full screen</option><option value="window" ${d.display.mode === 'window' ? 'selected' : ''}>Window</option></select></div>
        ${check('defMultimon', d.display.multimon, 'Use multiple monitors')}
        ${check('defClipboard', d.redirect.clipboard, 'Enable clipboard')}
        ${check('defPrinters', d.redirect.printers, 'Redirect printers')}
        ${check('defDrives', d.redirect.drives === 'all', 'Redirect local drives', 'Not recommended as a default.')}
      </section>
      <section class="settings-card" aria-labelledby="st-status">
        <h2 class="settings-card__title" id="st-status">Availability checks</h2>
        <p class="settings-card__desc">The app opens a short network connection to each system's Remote Desktop port (or the gateway) to show its status. No sign-in happens.</p>
        <div class="field"><label class="field__label" for="st-refresh">Check every</label>
          <select class="select" id="st-refresh" name="statusRefreshSeconds">${[30, 60, 120, 300, 600].map((v) => `<option value="${v}" ${s.statusRefreshSeconds === v ? 'selected' : ''}>${v < 60 ? `${v} seconds` : `${v / 60} ${v === 60 ? 'minute' : 'minutes'}`}</option>`).join('')}</select></div>
        ${check('confirmDisconnect', s.confirmDisconnect, 'Ask before disconnecting a session')}
      </section>
      <section class="settings-card" aria-labelledby="st-app">
        <h2 class="settings-card__title" id="st-app">App behavior</h2>
        <p class="settings-card__desc">Recent systems also appear in the taskbar jump list (right-click the app icon).</p>
        ${check('keepRunningInTray', s.keepRunningInTray !== false, 'Keep running in the notification area while sessions are open', 'Closing the window then hides the app, so session results are still recorded.')}
        ${check('startWithWindows', s.startWithWindows, 'Start with Windows', state.info.packaged ? 'Starts in the notification area when you sign in.' : 'Available in the installed app only.')}
      </section>
      <div class="page__actions"><button class="btn btn--primary" type="submit">Save settings</button></div>
    </form>
    <div class="settings" style="margin-top:24px">
      <section class="settings-card" aria-labelledby="st-policy">
        <h2 class="settings-card__title" id="st-policy">IT policy</h2>
        ${p.rejected ? `<div class="alert alert--warning" style="margin-bottom:12px">${icon('warning')}<div class="alert__body"><p class="alert__title">Policy file ignored</p><p class="alert__msg">${esc(p.rejected)}</p></div></div>` : ''}
        <p class="settings-card__desc">${p.active ? 'A policy from IT is active on this computer. Locked options are marked in the connection settings.' : 'No app policy is installed on this computer. Windows Group Policy for Remote Desktop still applies.'}</p>
        <dl class="kv"><dt>Policy file</dt><dd class="mono">${esc(p.file || '')}</dd>
          <dt>Saving passwords</dt><dd>${p.allowSavedCredentials === false ? 'Not allowed' : 'Allowed'}</dd>
          ${p.redirect ? `<dt>Locked options</dt><dd>${esc(Object.entries(p.redirect).map(([k, v]) => `${k}: ${v}`).join(', '))}</dd>` : ''}</dl>
      </section>
      ${(state.info.central || {}).configured ? `<section class="settings-card" aria-labelledby="st-central">
        <h2 class="settings-card__title" id="st-central">IT system list</h2>
        <p class="settings-card__desc">Systems provided by IT. They are read-only here; the signature is checked before use.</p>
        <dl class="kv">
          <dt>Source</dt><dd class="mono">${esc((p.centralList || {}).url || (p.centralList || {}).path || '')}</dd>
          <dt>Status</dt><dd>${esc({ current: 'Up to date', offline: 'Offline copy', error: 'Not available', loading: 'Loading' }[state.info.central.state] || state.info.central.state)}</dd>
          ${state.info.central.version ? `<dt>Version</dt><dd>${esc(state.info.central.version)} · ${esc(state.info.central.count)} systems</dd>` : ''}
          ${state.info.central.fetchedAt ? `<dt>Loaded</dt><dd>${esc(formatDateTime(state.info.central.fetchedAt))}</dd>` : ''}
          ${state.info.central.error ? `<dt>Last error</dt><dd>${esc(state.info.central.error)}</dd>` : ''}
        </dl>
        <div class="page__actions" style="margin-top:16px"><button class="btn btn--secondary" type="button" data-action="refresh-central">${icon('refresh')} Refresh now</button></div>
      </section>` : ''}
      <section class="settings-card" aria-labelledby="st-data">
        <h2 class="settings-card__title" id="st-data">Data and audit log</h2>
        <p class="settings-card__desc">System list, settings and the local audit log (connections, results, changes; never passwords) are stored for your Windows account only.</p>
        <dl class="kv"><dt>Folder</dt><dd class="mono">${esc(state.info.dataDir)}</dd><dt>App version</dt><dd>${esc(state.info.version)}</dd></dl>
        <div class="page__actions" style="margin-top:16px"><button class="btn btn--secondary" type="button" data-action="open-data">${icon('folder')} Open folder</button>
          <button class="btn btn--ghost" type="button" data-action="samples">Load sample systems (mock data)</button></div>
      </section>
    </div></div>`;
}

function check(name, checked, label, help = '') {
  return `<label class="check"><input type="checkbox" name="${name}" ${checked ? 'checked' : ''}><span class="check__text"><span>${esc(label)}</span>${help ? `<span class="check__help">${esc(help)}</span>` : ''}</span></label>`;
}

async function saveSettings(form) {
  const f = form.elements;
  const s = structuredClone(state.settings);
  if (!f.launchMode[0].matches(':disabled')) s.launchMode = f.launchMode.value;
  if (!f.signingThumbprint.disabled) s.signingThumbprint = f.signingThumbprint.value.trim();
  s.showConnectDialog = f.showConnectDialog.checked;
  s.statusRefreshSeconds = Number(f.statusRefreshSeconds.value);
  s.confirmDisconnect = f.confirmDisconnect.checked;
  s.keepRunningInTray = f.keepRunningInTray.checked;
  s.startWithWindows = f.startWithWindows.checked;
  s.defaults.display.mode = f.defMode.value;
  s.defaults.display.multimon = f.defMultimon.checked;
  s.defaults.redirect.clipboard = f.defClipboard.checked;
  s.defaults.redirect.printers = f.defPrinters.checked;
  s.defaults.redirect.drives = f.defDrives.checked ? 'all' : 'none';
  try {
    state.settings = await call(window.rdp.settings.save(s));
    state.info = await call(window.rdp.appInfo());
    toast('Settings saved');
  } catch (err) {
    errorDialog('Settings could not be saved', err.message);
  }
}

async function viewHelp() {
  let codes = [];
  try { codes = await call(window.rdp.help.errorCodes()); } catch { /* shown as empty */ }
  return `<div class="page">${pageHeader('Help and troubleshooting', 'Answers to common questions and what connection errors mean.')}
    <div class="help-grid">
      <section class="settings-card prose"><h2 class="settings-card__title">How connections work</h2>
        <p>This app manages your systems and starts the Windows Remote Desktop client (mstsc.exe) with the right settings. Each session opens in its own Remote Desktop window, which uses Microsoft's encryption, Network Level Authentication and RD Gateway support.</p>
        <p>Before connecting, the app checks whether the system answers on its Remote Desktop port. This check does not sign in.</p></section>
      <section class="settings-card prose"><h2 class="settings-card__title">Disconnect or sign out?</h2>
        <ul><li><strong>Disconnect</strong> closes the window here. Your programs keep running on the server, and you can reconnect.</li>
        <li><strong>Sign out</strong> (inside the remote session, Start menu) ends the session and closes your programs.</li></ul></section>
      <section class="settings-card prose"><h2 class="settings-card__title">Windows asks me to confirm every connection</h2>
        <p>Since April 2026, Windows shows a security confirmation each time a Remote Desktop file is opened. Check that the computer name is the one you expect and that only the devices you need are listed, then select Connect.</p>
        <p>IT can remove the prompt by signing connection files with a trusted certificate (Settings, signing certificate). Alternatively, "Start directly without a file" skips it with fewer options.</p></section>
      <section class="settings-card prose"><h2 class="settings-card__title">Passwords</h2>
        <p>The app never stores passwords itself. If you choose to save one, it goes to Windows Credential Manager, protected by your Windows account. Don't save passwords on shared computers.</p></section>
    </div>
    <section class="section" aria-labelledby="h-keys"><div class="section__header"><h2 class="section__title" id="h-keys">Keyboard shortcuts</h2></div>
      <div class="table-wrap"><table class="table"><thead><tr><th scope="col">Keys</th><th scope="col">Action</th></tr></thead><tbody>
        <tr><td><kbd>Ctrl</kbd> + <kbd>K</kbd></td><td>Quick connect: find a system and connect</td></tr>
        <tr><td><kbd>Ctrl</kbd> + <kbd>F</kbd> or <kbd>/</kbd></td><td>Search systems</td></tr>
        <tr><td><kbd>Ctrl</kbd> + <kbd>N</kbd></td><td>Add a system</td></tr>
        <tr><td><kbd>Esc</kbd></td><td>Close dialog, menu or tooltip</td></tr>
        <tr><td><kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>Break</kbd></td><td>Inside a session: switch between full screen and window (Windows Remote Desktop)</td></tr>
        <tr><td><kbd>Ctrl</kbd> + <kbd>Alt</kbd> + <kbd>End</kbd></td><td>Inside a session: security options (change password, sign out)</td></tr>
      </tbody></table></div></section>
    <section class="section" aria-labelledby="h-codes"><div class="section__header"><h2 class="section__title" id="h-codes">Error codes</h2><span class="section__meta">Technical codes shown by Remote Desktop and what to do</span></div>
      <div class="table-wrap"><table class="table"><thead><tr><th scope="col">Code</th><th scope="col">Meaning</th><th scope="col">What you can do</th></tr></thead>
      <tbody>${codes.map((c) => `<tr><td class="mono">${esc(c.code)}</td><td><strong>${esc(c.title)}</strong></td><td class="muted">${esc(c.message)}</td></tr>`).join('')}</tbody></table></div></section>
    </div>`;
}

// ── Shell ─────────────────────────────────────────────
function renderShell() {
  const info = state.info;
  const collapsed = readPref('sidebar', 'expanded') === 'collapsed' || window.innerWidth < 768;
  $('#app').innerHTML = `
    <a class="skip-link" href="#main">Skip to content</a>
    <div class="shell">
      <header class="topbar">
        <button class="icon-btn" type="button" data-action="toggle-sidebar" aria-controls="sidebar" aria-expanded="${!collapsed}" aria-label="${collapsed ? 'Expand navigation' : 'Collapse navigation'}" data-tooltip="${collapsed ? 'Expand navigation' : 'Collapse navigation'}">${icon('panel')}</button>
        <div class="topbar__brand">
          <span class="topbar__mark" aria-hidden="true">BP</span>
          <span class="topbar__company">BearingPoint</span>
          <span class="topbar__divider" aria-hidden="true"></span>
          <span class="topbar__product">Remote Desktop</span>
          <span class="badge">Internal</span>
        </div>
        <span class="topbar__spacer"></span>
        <button class="quick-btn" type="button" data-action="quick-connect" ${info.access.allowed ? '' : 'hidden'} aria-keyshortcuts="Control+K">${icon('search', 16)}<span>Connect to host or IP…</span><kbd>Ctrl K</kbd></button>
        <div class="topbar__actions">
          <button class="icon-btn" type="button" data-action="notifications" aria-label="Notifications" aria-haspopup="dialog" data-tooltip="Notifications" id="bell">${icon('bell')}<span class="icon-btn__dot" hidden></span></button>
          <button class="icon-btn" type="button" data-nav="help" aria-label="Help" data-tooltip="Help">${icon('help')}</button>
          <button class="user-button" type="button" data-action="user-menu" aria-haspopup="menu" aria-expanded="false" aria-label="User menu for ${esc(info.user)}">
            <span class="avatar" aria-hidden="true">${esc(initials(info.displayName))}</span><span class="user-button__name">${esc(info.user)}</span></button>
        </div>
      </header>
      <div class="shell__body">
        <nav class="sidebar" id="sidebar" aria-label="Main navigation" data-collapsed="${collapsed}">
          <div class="nav">${NAV.map((n) => n === 'sep' ? '<div class="nav__sep" role="separator"></div>'
            : `<button class="nav__item" type="button" data-nav="${n[0]}" data-tooltip="${n[1]}" data-tooltip-side="right" data-tooltip-when="collapsed">${icon(n[2])}<span class="nav__label">${n[1]}</span>${n[0] === 'sessions' ? '<span class="nav__count" data-active-count hidden></span>' : ''}</button>`).join('')}</div>
        </nav>
        <main class="main" id="main" tabindex="-1"></main>
      </div>
    </div>`;
}

let renderToken = 0;
async function render({ focus = false } = {}) {
  if (!state.info) return;
  const token = ++renderToken;
  const main = $('#main');
  const scroll = main.scrollTop;
  const active = document.activeElement;
  const activeId = active && main.contains(active) && active.id;
  const activeKey = active && main.contains(active) && !active.id ? focusKey(active) : null;
  state.dirty = false;
  const selStart = active && active.selectionStart;

  const r = state.route;
  let html;
  if (!state.info.access.allowed && r.name !== 'help') html = accessDeniedPage();
  else if (state.loadError && !['settings', 'help'].includes(r.name)) html = errorPage(state.loadError);
  else if (r.name === 'dashboard') html = viewDashboard();
  else if (r.name === 'systems') html = viewSystems();
  else if (r.name === 'favorites') html = viewFavorites();
  else if (r.name === 'recent') html = viewRecent();
  else if (r.name === 'sessions') html = viewSessions();
  else if (r.name === 'details') html = viewDetails(r.id);
  else if (r.name === 'settings') html = viewSettings();
  else if (r.name === 'help') html = await viewHelp();
  if (token !== renderToken) return;
  if (state.info.access.allowed && !state.loadError && !['settings', 'help'].includes(r.name)) {
    html = html.replace('<div class="page">', `<div class="page">${globalBanners()}`);
  }
  main.innerHTML = html;

  $$('.nav__item').forEach((b) => {
    const current = b.dataset.nav === r.name || (r.name === 'details' && b.dataset.nav === 'systems');
    if (current) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
  });
  const count = activeSessions().length;
  const badge = $('[data-active-count]');
  badge.hidden = !count; badge.textContent = count;
  badge.setAttribute('aria-label', `${count} active`);
  $('#bell .icon-btn__dot').hidden = !state.unread;
  $('#bell').setAttribute('aria-label', state.unread ? `Notifications, ${state.unread} new` : 'Notifications');

  if (r.name === 'details') {
    const c = state.connections.find((x) => x.id === r.id);
    if (c) hydrateCredentials(c);
  }
  if (focus) {
    main.scrollTop = 0;
    const h = $('#page-title');
    if (h) h.focus({ preventScroll: true });
  } else {
    main.scrollTop = scroll;
    if (activeId && document.getElementById(activeId)) {
      const el = document.getElementById(activeId);
      el.focus({ preventScroll: true });
      if (selStart !== undefined && selStart !== null && el.setSelectionRange) try { el.setSelectionRange(selStart, selStart); } catch { /* not a text field */ }
    } else if (activeKey) {
      const el = main.querySelector(activeKey);
      if (el) el.focus({ preventScroll: true });
    }
  }
}

/** Selector that finds the "same" control after a re-render (buttons have no ids). */
function focusKey(el) {
  const parts = ['action', 'id', 'session', 'nav', 'filter', 'view']
    .filter((k) => el.dataset && el.dataset[k] !== undefined)
    .map((k) => `[data-${k}="${CSS.escape(el.dataset[k])}"]`);
  return parts.length ? parts.join('') : null;
}

/** Re-render unless a dialog or menu is open; then defer until it closes. */
function softRender() {
  if (document.querySelector('.overlay') || document.querySelector('.menu')) { state.dirty = true; return; }
  render();
}
document.addEventListener('bp:dialog-closed', () => { if (state.dirty && !document.querySelector('.overlay')) render(); });

/** Generic error state. A damaged system list gets its own recovery path. */
function errorPage(message) {
  if (state.loadCorrupt) {
    return `<div class="page">${pageHeader('Your system list could not be read', '')}
      <div class="alert alert--error" role="alert">${icon('xCircle')}<div class="alert__body">
        <p class="alert__title">The file with your systems is damaged</p>
        <p class="alert__msg">A copy of the damaged file was kept, so IT can try to recover it. You can start with an empty list and import or add your systems again. Your remote sessions are not affected.</p>
        <details class="details"><summary>Technical details</summary><p class="mono small">${esc(message)}</p></details>
        <div class="alert__actions"><button class="btn btn--primary" type="button" data-action="start-empty">Start with an empty list</button>
          <button class="btn btn--secondary" type="button" data-action="open-data">${icon('folder')} Open data folder</button></div>
      </div></div></div>`;
  }
  return `<div class="page">${pageHeader('Something is blocking the app', '')}
    <div class="alert alert--error" role="alert">${icon('xCircle')}<div class="alert__body">
      <p class="alert__title">Your systems could not be loaded</p>
      <p class="alert__msg">The local data could not be read. Your remote sessions are not affected. Trying again is safe.</p>
      <details class="details"><summary>Technical details</summary><p class="mono small">${esc(message)}</p></details>
      <div class="alert__actions"><button class="btn btn--primary" type="button" data-action="retry-load">${icon('refresh')} Try again</button></div>
    </div></div></div>`;
}

/** Shown when IT restricts the app to certain groups and the user is not a member. */
function accessDeniedPage() {
  const p = state.info.policy || {};
  return `<div class="page"><div class="empty" style="max-width:640px;margin:48px auto;text-align:left;padding:32px">
    <div class="empty__icon">${icon('shield')}</div>
    <h1 class="page__title" id="page-title" tabindex="-1">Access denied</h1>
    <p class="page__desc" style="margin-top:8px">${esc(state.info.access.reason || 'You do not have access to this app.')}</p>
    <p style="margin-top:16px">Signed in as <strong>${esc(state.info.user)}</strong> on ${esc(state.info.computer)}.</p>
    <p class="muted" style="margin-top:8px">If you need Remote Desktop access for your work, request it from ${esc(p.serviceDeskName || 'the IT Service Desk')}. Include your username and the systems you need.</p>
    <div class="empty__actions" style="justify-content:flex-start">
      ${p.helpUrl ? `<button class="btn btn--primary" type="button" data-action="open-help">Request access</button>` : ''}
      <button class="btn btn--secondary" type="button" data-nav="help">Help</button>
    </div></div></div>`;
}

function navigate(name, id = null) {
  state.prevRoute = state.route;
  state.route = { name, id };
  if (name === 'settings' || name === 'recent' || name === 'dashboard') refreshAudit();
  render({ focus: true });
}

async function refreshAudit() {
  try { state.audit = await call(window.rdp.audit.list(300)); } catch { /* keep previous */ }
}

// ── Actions ───────────────────────────────────────────
function connById(id) { return state.connections.find((c) => c.id === id); }

async function toggleFavorite(id) {
  try {
    const updated = await call(window.rdp.connections.toggleFavorite(id));
    const c = connById(id);
    Object.assign(c, { favorite: updated.favorite });
    render();
    toast(updated.favorite ? `${c.name} added to favorites` : `${c.name} removed from favorites`);
  } catch (err) { errorDialog('The action could not be completed', err.message); }
}

async function disconnectSession(id) {
  const s = state.sessions.find((x) => x.id === id);
  if (!s) return;
  if (state.settings.confirmDisconnect) {
    const ok = await confirmDialog({
      title: `Disconnect from ${s.name}?`,
      message: 'The Remote Desktop window closes. Your session and open programs keep running on the server, and you can reconnect later.',
      detail: 'To end the session and close your programs, select Sign out inside the remote session instead.',
      confirmLabel: 'Disconnect', danger: true,
    });
    if (!ok) return;
  }
  try { await call(window.rdp.sessions.disconnect(id)); toast(`Disconnected from ${s.name}`); }
  catch (err) { errorDialog('The action could not be completed', err.message); }
}

async function deleteSystem(id) {
  const c = connById(id);
  const ok = await confirmDialog({
    title: `Remove ${c.name}?`,
    message: 'The system is removed from your list. This does not affect the remote computer. A saved password stays in Windows Credential Manager until you remove it.',
    confirmLabel: 'Remove system', danger: true,
  });
  if (!ok) return;
  try {
    await call(window.rdp.connections.remove(id));
    await loadData();
    // Stay on the current page; only leave the details page of the system that no longer exists.
    if (state.route.name === 'details' && state.route.id === id) {
      navigate(state.prevRoute && state.prevRoute.name !== 'details' ? state.prevRoute.name : 'systems');
    }
    toast(`${c.name} removed`);
  } catch (err) { errorDialog('The action could not be completed', err.message); }
}

function systemMenu(anchor, c) {
  showMenu(anchor, [
    { label: 'View details', icon: 'info', action: () => navigate('details', c.id) },
    { label: c.activeSessionId ? 'Show session window' : 'Connect', icon: c.activeSessionId ? 'window' : 'connect', action: () => (c.activeSessionId ? focusSession(c.activeSessionId) : openConnectDialog(c)) },
    { label: 'Edit', icon: 'edit', disabled: c.source === 'central', action: () => openEditDialog(c) },
    { label: 'Duplicate', icon: 'copy', action: () => openEditDialog({ ...c, id: undefined, name: `${c.name} (copy)`, favorite: false, sample: false, lastConnectedAt: null }) },
    { label: 'Export .rdp file', icon: 'download', action: () => exportSystem(c.id) },
    'sep',
    { label: 'Remove system', icon: 'trash', danger: true, disabled: c.source === 'central', action: () => deleteSystem(c.id) },
  ]);
}

async function exportSystem(id) {
  try {
    const file = await call(window.rdp.connections.exportRdp(id));
    if (file) toast('Remote Desktop file exported');
  } catch (err) { errorDialog('The file could not be exported', err.message); }
}

function userMenu(anchor) {
  showMenu(anchor, [
    { header: `<strong>${esc(state.info.user)}</strong><div class="small muted">${esc(state.info.computer)}</div>` },
    'sep',
    { label: 'Settings', icon: 'settings', action: () => navigate('settings') },
    { label: 'Open data folder', icon: 'folder', action: () => call(window.rdp.openDataFolder()) },
    { label: 'Help and troubleshooting', icon: 'help', action: () => navigate('help') },
  ]);
}

function notificationsPanel() {
  state.unread = 0;
  $('#bell .icon-btn__dot').hidden = true;
  $('#bell').setAttribute('aria-label', 'Notifications');
  const label = { error: 'Error', success: 'Success', info: 'Information' };
  openDialog({
    title: 'Notifications',
    subtitle: 'Events from this app session',
    body: state.notifications.length
      ? `<ul class="notif-list">${state.notifications.slice(0, 30).map((n) => `<li class="notif">${icon(n.kind === 'error' ? 'xCircle' : n.kind === 'success' ? 'checkCircle' : 'info')}
          <div><p><span class="visually-hidden">${label[n.kind] || ''}: </span><strong>${esc(n.title)}</strong></p>${n.message ? `<p class="small muted">${esc(n.message)}</p>` : ''}<p class="small muted">${esc(formatWhen(n.ts))}</p></div></li>`).join('')}</ul>`
      : '<p class="muted">No notifications in this app session.</p>',
    footer: '<button class="btn btn--secondary" type="button" data-close>Close</button>',
  });
}

/** Host name, IPv4, IPv6 (bare or in brackets), each optionally with :port. */
function isAddress(v) {
  return /^[A-Za-z0-9._-]+(:\d{1,5})?$/.test(v) || /^\[[0-9A-Fa-f:.]+\](:\d{1,5})?$/.test(v) || (/^[0-9A-Fa-f:.]+$/.test(v) && (v.match(/:/g) || []).length > 1);
}

/**
 * Quick connect (Ctrl+K): type a host name or IP address and connect immediately, without creating a
 * system first. Windows then asks for username and password. Matching saved systems are listed below.
 */
function quickConnect(prefill = '') {
  if (document.querySelector('.overlay') || !state.info || !state.info.access.allowed) return;
  const listId = 'qc-list';
  const dlg = openDialog({
    title: 'Quick connect',
    subtitle: 'Enter a computer name or IP address to connect right away, or pick a saved system.',
    body: `<div class="search" style="max-width:none"><label class="visually-hidden" for="qc-input">Computer name, IP address or saved system</label>${icon('connect')}
        <input class="input" id="qc-input" type="text" role="combobox" aria-expanded="true" aria-controls="${listId}" aria-autocomplete="list" placeholder="for example server01.corp.local, 10.20.30.40 or 10.20.30.40:3390" autocomplete="off" spellcheck="false" autofocus></div>
      <ul class="qc-list" id="${listId}" role="listbox" aria-label="Connection targets"></ul>
      <label class="check" style="margin-top:8px"><input type="checkbox" id="qc-save"><span class="check__text"><span>Save to My systems</span><span class="check__help">Otherwise the address is used once and not stored. It still appears in Recent sessions.</span></span></label>
      <p class="small muted" style="margin-top:8px">Arrow keys to choose, Enter to connect, Esc to close.</p>`,
  });
  const input = $('#qc-input', dlg.el);
  const list = $('#qc-list', dlg.el);
  input.value = prefill;
  let options = [];
  let index = 0;
  const draw = () => {
    const raw = input.value.trim();
    const q = raw.toLowerCase();
    const all = [...state.connections].sort((a, b) => (b.lastConnectedAt || '').localeCompare(a.lastConnectedAt || '') || a.name.localeCompare(b.name));
    const matches = all.filter((c) => !q || [c.name, c.host, ...(c.tags || [])].join(' ').toLowerCase().includes(q)).slice(0, 7);
    const exact = matches.some((c) => c.host.toLowerCase() === q || `${c.host}:${c.port}`.toLowerCase() === q);
    options = [];
    if (raw && isAddress(raw) && !exact) options.push({ type: 'address', value: raw });
    for (const c of matches) options.push({ type: 'system', conn: c });
    index = Math.min(index, Math.max(0, options.length - 1));
    list.innerHTML = options.length ? options.map((o, i) => (o.type === 'address'
      ? `<li class="qc-item qc-item--address" role="option" id="qc-${i}" aria-selected="${i === index}" data-i="${i}">
          <span class="qc-item__main"><strong>${icon('connect', 16)} Connect to ${esc(o.value)}</strong><span class="small muted">Windows asks for your username and password</span></span><kbd>Enter</kbd></li>`
      : `<li class="qc-item" role="option" id="qc-${i}" aria-selected="${i === index}" data-i="${i}">
          <span class="qc-item__main"><strong>${esc(o.conn.name)}</strong><span class="card__host">${esc(o.conn.host)}</span></span>${statusBadge(o.conn)}</li>`)).join('')
      : `<li class="qc-empty">${raw ? 'Not a valid computer name or IP address, and no saved system matches.' : 'Type a computer name or IP address.'}</li>`;
    if (options.length) input.setAttribute('aria-activedescendant', `qc-${index}`); else input.removeAttribute('aria-activedescendant');
  };
  const choose = async (i) => {
    const o = options[i];
    if (!o) return;
    const save = $('#qc-save', dlg.el).checked;
    dlg.close();
    if (o.type === 'system') {
      if (o.conn.activeSessionId) focusSession(o.conn.activeSessionId); else runConnect(o.conn, null, { quick: true });
      return;
    }
    try {
      const conn = await call(window.rdp.sessions.quickTarget({ address: o.value, save }));
      if (save) { await loadData(); toast(`${conn.name} saved to My systems`); }
      if (conn.activeSessionId) { focusSession(conn.activeSessionId); return; }
      runConnect(conn, null, { quick: true });
    } catch (err) {
      errorDialog('Quick connect is not possible', err.message);
    }
  };
  input.addEventListener('input', () => { index = 0; draw(); });
  input.addEventListener('keydown', (e) => {
    if (e.key === 'ArrowDown') { e.preventDefault(); index = (index + 1) % Math.max(1, options.length); draw(); }
    if (e.key === 'ArrowUp') { e.preventDefault(); index = (index - 1 + options.length) % Math.max(1, options.length); draw(); }
    if (e.key === 'Enter') { e.preventDefault(); choose(index); }
  });
  list.addEventListener('click', (e) => { const li = e.target.closest('[data-i]'); if (li) choose(Number(li.dataset.i)); });
  draw();
}

function notify(n) {
  state.notifications.unshift({ ts: new Date().toISOString(), ...n });
  state.unread += 1;
}

async function handleClick(e) {
  const nav = e.target.closest('[data-nav]');
  if (nav) { navigate(nav.dataset.nav); return; }
  const btn = e.target.closest('[data-action]');
  if (!btn) return;
  const id = btn.dataset.id;
  const c = id ? connById(id) : null;
  switch (btn.dataset.action) {
    case 'connect': if (c) openConnectDialog(c); break;
    case 'reconnect': { const rc = connById(btn.dataset.id); if (rc) runConnect(rc, null); else errorDialog('System not found', 'This system was removed from your list.'); break; }
    case 'focus': focusSession(btn.dataset.session); break;
    case 'disconnect': disconnectSession(btn.dataset.session); break;
    case 'favorite': toggleFavorite(id); break;
    case 'menu': systemMenu(btn, c); break;
    case 'details': navigate('details', id); break;
    case 'back': navigate(state.prevRoute && state.prevRoute.name !== 'details' ? state.prevRoute.name : 'systems'); break;
    case 'edit': openEditDialog(c); break;
    case 'duplicate': openEditDialog({ ...c, id: undefined, name: `${c.name} (copy)`, favorite: false, sample: false, lastConnectedAt: null }); break;
    case 'delete': deleteSystem(id); break;
    case 'export': exportSystem(id); break;
    case 'probe': {
      btn.disabled = true;
      btn.innerHTML = '<span class="spinner" style="width:16px;height:16px" aria-hidden="true"></span> Checking';
      try { c.status = await call(window.rdp.status.probe(id)); } catch { /* status:update covers it */ }
      render();
      break;
    }
    case 'save-cred': openCredentialDialog(c, () => hydrateCredentials(c)); break;
    case 'remove-cred': removeCredential(c, () => hydrateCredentials(c)); break;
    case 'add': openEditDialog(); break;
    case 'import': importRdpFiles(); break;
    case 'samples': {
      try { const n = await call(window.rdp.connections.loadSamples()); await loadData(); toast(n ? `${n} sample systems added (mock data)` : 'Sample systems are already in your list'); }
      catch (err) { errorDialog('The action could not be completed', err.message); }
      break;
    }
    case 'clear-filters':
      state.filters = { q: '', status: '', environment: '', os: '', favorites: false, recent: false };
      render();
      $('#search') && $('#search').focus();
      break;
    case 'dismiss': state.dismissed.add(btn.dataset.key); render(); break;
    case 'clear-ended': state.sessions = await call(window.rdp.sessions.clearEnded()); render(); break;
    case 'toggle-sidebar': {
      const sb = $('#sidebar');
      const next = sb.dataset.collapsed !== 'true';
      sb.dataset.collapsed = String(next);
      btn.setAttribute('aria-expanded', String(!next));
      btn.setAttribute('aria-label', next ? 'Expand navigation' : 'Collapse navigation');
      btn.dataset.tooltip = next ? 'Expand navigation' : 'Collapse navigation';
      writePref('sidebar', next ? 'collapsed' : 'expanded');
      break;
    }
    case 'user-menu': userMenu(btn); break;
    case 'notifications': notificationsPanel(); break;
    case 'quick-connect': quickConnect(); break;
    case 'quick-host': quickConnect(btn.dataset.host || ''); break;
    case 'toggle-filters':
      state.filtersOpen = !state.filtersOpen;
      writePref('filters', state.filtersOpen ? 'open' : 'closed');
      await render();
      if (state.filtersOpen) { const first = $('#f-status'); if (first) first.focus(); }
      else { const t = $('[data-action="toggle-filters"]'); if (t) t.focus(); }
      break;
    case 'probe-all': {
      btn.disabled = true;
      try { await call(window.rdp.status.probeAll()); } catch { /* status:update covers it */ }
      render();
      break;
    }
    case 'open-help': call(window.rdp.openHelp()); break;
    case 'refresh-central': {
      btn.disabled = true;
      try {
        await call(window.rdp.connections.refreshCentral());
        state.info = await call(window.rdp.appInfo());
        state.dismissed.delete('central');
        await loadData();
        toast('IT system list refreshed');
      } catch (err) { errorDialog('The IT system list could not be refreshed', err.message); }
      break;
    }
    case 'start-empty':
      await call(window.rdp.connections.acknowledgeCorrupt());
      await loadData();
      toast('Started with an empty list');
      break;
    case 'open-data': call(window.rdp.openDataFolder()); break;
    case 'retry-load': loadData(); break;
    default: break;
  }
}

let searchTimer = null;
function announceCount() {
  const el = $('.result-count');
  announce(el ? el.textContent : filtersActive() ? 'No systems found' : '');
}

function handleInput(e) {
  if (e.target.id === 'search') {
    state.filters.q = e.target.value;
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { render(); announceCount(); }, 250);
  }
}

function handleChange(e) {
  const f = e.target.dataset.filter;
  if (f) {
    state.filters[f] = e.target.type === 'checkbox' ? e.target.checked : e.target.value;
    render().then(announceCount);
  }
  const v = e.target.closest('[data-view]');
  if (v) { /* handled in click */ }
}

document.addEventListener('click', (e) => {
  const v = e.target.closest('[data-view]');
  if (v) { state.view = v.dataset.view; writePref('view', state.view); render(); return; }
  handleClick(e);
});
document.addEventListener('input', handleInput);
document.addEventListener('change', handleChange);
document.addEventListener('submit', (e) => {
  if (e.target.id === 'settings-form') { e.preventDefault(); saveSettings(e.target); }
});
document.addEventListener('keydown', (e) => {
  if ((e.ctrlKey && e.key.toLowerCase() === 'f') || (e.key === '/' && !/input|textarea|select/i.test(document.activeElement.tagName))) {
    const s = $('#search');
    if (s) { e.preventDefault(); s.focus(); s.select(); }
  }
  if (!state.info || !state.info.access.allowed || !state.loaded) return;
  if (e.ctrlKey && e.key.toLowerCase() === 'n' && !document.querySelector('.overlay')) { e.preventDefault(); openEditDialog(); }
  if (e.ctrlKey && e.key.toLowerCase() === 'k' && state.info.access.allowed) { e.preventDefault(); quickConnect(); }
});

// Live session durations
setInterval(() => {
  $$('[data-duration-since]').forEach((el) => { el.textContent = formatDuration((Date.now() - Date.parse(el.dataset.durationSince)) / 1000); });
}, 15000);

// ── Events from the main process ──────────────────────
window.rdp.on('status:update', (map) => {
  for (const c of state.connections) if (map[c.id]) c.status = map[c.id];
  softRender();
});

window.rdp.on('session:update', (s) => {
  const prev = state.sessions.find((x) => x.id === s.id);
  upsertSession(s);
  if (prev && prev.state !== s.state) {
    if (s.state === 'active') notify({ kind: 'success', title: `Connected to ${s.name}`, message: s.host });
    if (s.state === 'failed') notify({ kind: 'error', title: `Connection to ${s.name} failed`, message: s.result ? s.result.title : '' });
    if (s.state === 'ended') notify({ kind: 'info', title: `Session to ${s.name} ended`, message: s.result ? s.result.title : '' });
    if (['ended', 'failed'].includes(s.state)) refreshAudit().then(softRender);
  }
  softRender();
});

// ── Start ─────────────────────────────────────────────
window.rdp.on('app:connect-request', (id) => {
  if (!state.loaded) { state.pendingConnect = id; return; }
  handleConnectRequest(id);
});

window.rdp.on('connections:changed', async () => {
  try { state.info = await call(window.rdp.appInfo()); } catch { /* keep previous */ }
  await loadData();
});

/** Started outside the window (jump list, tray): always confirm in the connection dialog. */
function handleConnectRequest(id) {
  const c = connById(id);
  if (!c) { errorDialog('System not found', 'The system is no longer in your list.'); return; }
  if (c.activeSessionId) { focusSession(c.activeSessionId); return; }
  if (document.querySelector('.overlay')) { toast(`Finish the open dialog, then connect to ${c.name}.`, { kind: 'info' }); return; }
  openConnectDialog(c, { forceDialog: true });
}

(async function start() {
  try {
    state.info = await call(window.rdp.appInfo());
  } catch (err) {
    $('#app').innerHTML = `<div class="page"><div class="alert alert--error" role="alert">${icon('xCircle')}<div class="alert__body"><p class="alert__title">The app could not start</p><p class="alert__msg">${esc(err.message)}</p></div></div></div>`;
    return;
  }
  renderShell();
  render();
  await loadData();
  if (state.pendingConnect) {
    const id = state.pendingConnect;
    state.pendingConnect = null;
    handleConnectRequest(id);
  }
  call(window.rdp.appReady()).catch(() => {});
})();
