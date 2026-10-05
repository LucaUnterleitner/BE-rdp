// Dialogs: connect, add/edit system, import preview, saved credentials and connection progress.

import { icon } from './icons.js';
import { esc, $, $$, call, openDialog, confirmDialog, toast, errorDialog } from './ui.js';
import { PROTOCOLS, ENABLED_PROTOCOLS, parseTargets, defaultPort, describeTarget } from './targets.js';

let ctx = null;
export function setContext(c) { ctx = c; }

const RESOLUTIONS = [[1280, 720], [1366, 768], [1600, 900], [1920, 1080], [2560, 1440], [3840, 2160]];

function locked(key) {
  const p = ctx.state.info.policy || {};
  return p.redirect && Object.prototype.hasOwnProperty.call(p.redirect, key);
}
/** The value that applies: the IT policy value when the option is locked, otherwise the system's own value. */
function effective(key, value) {
  const p = ctx.state.info.policy || {};
  return locked(key) ? p.redirect[key] : value;
}
const policyNote = `<span class="policy-note">${icon('shield', 16)} Set by IT policy</span>`;

function check(name, checked, label, help = '', { disabled = false, note = '' } = {}) {
  return `<label class="check ${disabled ? 'check--disabled' : ''}">
    <input type="checkbox" name="${name}" ${checked ? 'checked' : ''} ${disabled ? 'disabled' : ''}>
    <span class="check__text"><span>${label}</span>${help ? `<span class="check__help">${help}</span>` : ''}${note}</span>
  </label>`;
}

function select(name, value, options, { disabled = false, id = '' } = {}) {
  return `<select class="select" name="${name}" ${id ? `id="${id}"` : ''} ${disabled ? 'disabled' : ''}>
    ${options.map(([v, l]) => `<option value="${esc(v)}" ${String(v) === String(value) ? 'selected' : ''}>${esc(l)}</option>`).join('')}
  </select>`;
}

function uid(p) { return `${p}-${Math.random().toString(36).slice(2, 8)}`; }

// ── Shared option sections ────────────────────────────
function displayHtml(c) {
  const d = c.display;
  const res = `${d.width}x${d.height}`;
  const known = RESOLUTIONS.some(([w, h]) => `${w}x${h}` === res);
  const resOptions = RESOLUTIONS.map(([w, h]) => [`${w}x${h}`, `${w} × ${h}`]);
  if (!known) resOptions.unshift([res, `${d.width} × ${d.height}`]);
  const modeId = uid('mode'); const resId = uid('res');
  return `
    <div class="field-row">
      <div class="field"><label class="field__label" for="${modeId}">Display</label>
        ${select('d.mode', d.mode, [['fullscreen', 'Full screen'], ['window', 'Window']], { id: modeId })}</div>
      <div class="field" data-res-field ${d.mode === 'window' ? '' : 'hidden'}><label class="field__label" for="${resId}">Window size</label>
        ${select('d.res', res, resOptions, { id: resId })}</div>
    </div>
    <div style="margin-top:8px">
      ${check('d.multimon', d.multimon, 'Use multiple monitors', 'The remote desktop spans all monitors of this computer.')}
      ${check('d.dynamicResolution', d.dynamicResolution, 'Adjust resolution when the window size changes')}
    </div>`;
}

function redirectHtml(c) {
  const r = c.redirect;
  const audioId = uid('audio');
  return `
    ${check('r.clipboard', effective('clipboard', r.clipboard), 'Enable clipboard', 'Copy and paste text and files between this computer and the remote system. Follow the data classification rules.', { disabled: locked('clipboard'), note: locked('clipboard') ? policyNote : '' })}
    ${check('r.drives', effective('drives', r.drives) === 'all', 'Redirect local drives', 'The remote system can read and write all drives of this computer. Switch on only to transfer files.', { disabled: locked('drives'), note: locked('drives') ? policyNote : '' })}
    ${check('r.printers', effective('printers', r.printers), 'Redirect printers', 'Print from the remote system to printers installed on this computer.', { disabled: locked('printers'), note: locked('printers') ? policyNote : '' })}
    ${check('r.microphone', effective('microphone', r.microphone), 'Use microphone in the remote session', '', { disabled: locked('microphone'), note: locked('microphone') ? policyNote : '' })}
    ${check('r.smartcards', effective('smartcards', r.smartcards), 'Use smart cards in the remote session', 'Needed for smart card sign-in inside the session.', { disabled: locked('smartcards'), note: locked('smartcards') ? policyNote : '' })}
    <div class="field" style="margin-top:12px"><label class="field__label" for="${audioId}">Remote audio</label>
      ${select('r.audio', effective('audio', r.audio), [['local', 'Play on this computer'], ['remote', 'Play on the remote computer'], ['none', 'Do not play']], { id: audioId, disabled: locked('audio') })}
    </div>`;
}

function advancedHtml(c) {
  const g = c.gateway; const s = c.security;
  const gmId = uid('gm'); const ghId = uid('gh'); const cpId = uid('cp'); const alId = uid('al');
  const requiredProtection = (ctx.state.info.policy || {}).requireCredentialProtection;
  return `
    <div class="field-row">
      <div class="field"><label class="field__label" for="${gmId}">RD Gateway</label>
        ${select('g.mode', g.mode, [['none', 'Do not use a gateway'], ['always', 'Always use this gateway'], ['detect', 'Use only if direct connection fails']], { id: gmId })}</div>
      <div class="field" data-gw-field ${g.mode === 'none' ? 'hidden' : ''}><label class="field__label" for="${ghId}">Gateway address</label>
        <input class="input" id="${ghId}" name="g.host" value="${esc(g.host)}" placeholder="rdgw.example.com" autocomplete="off" spellcheck="false"></div>
    </div>
    <div class="field" style="margin-top:16px"><label class="field__label" for="${cpId}">Protect my credentials</label>
      ${select('s.credentialProtection', s.credentialProtection, [['none', 'Standard sign-in'], ['remoteGuard', 'Remote Credential Guard (recommended for admin access)'], ['restrictedAdmin', 'Restricted Admin mode']], { id: cpId })}
      <span class="field__help">Remote Credential Guard keeps your password on this computer. It requires Kerberos and support on the remote system.${requiredProtection ? ` IT policy requires ${esc(requiredProtection)}.` : ''}</span>
    </div>
    <div class="field" style="margin-top:16px"><label class="field__label" for="${alId}">If the identity of the remote system cannot be verified</label>
      ${select('s.authLevel', s.authLevel, [[2, 'Warn me and let me decide'], [1, 'Do not connect']], { id: alId })}
    </div>
    <div style="margin-top:8px">${check('s.adminSession', s.adminSession, 'Connect to the administrative (console) session', 'For server administration only.')}</div>`;
}

function readOptions(form, base) {
  const v = (n) => form.elements[n];
  const out = structuredClone({ display: base.display, redirect: base.redirect, gateway: base.gateway, security: base.security });
  if (v('d.mode')) {
    out.display.mode = v('d.mode').value;
    const [w, h] = v('d.res').value.split('x').map(Number);
    out.display.width = w; out.display.height = h;
    out.display.multimon = v('d.multimon').checked;
    out.display.dynamicResolution = v('d.dynamicResolution').checked;
  }
  if (v('r.clipboard')) {
    out.redirect.clipboard = v('r.clipboard').checked;
    out.redirect.drives = v('r.drives').checked ? 'all' : 'none';
    out.redirect.printers = v('r.printers').checked;
    out.redirect.microphone = v('r.microphone').checked;
    out.redirect.smartcards = v('r.smartcards').checked;
    out.redirect.audio = v('r.audio').value;
  }
  if (v('g.mode')) {
    out.gateway.mode = v('g.mode').value;
    out.gateway.host = v('g.host').value.trim();
    out.security.credentialProtection = v('s.credentialProtection').value;
    out.security.authLevel = Number(v('s.authLevel').value);
    out.security.adminSession = v('s.adminSession').checked;
  }
  return out;
}

function wireDependentFields(root) {
  root.addEventListener('change', (e) => {
    if (e.target.name === 'd.mode') $('[data-res-field]', root).hidden = e.target.value !== 'window';
    if (e.target.name === 'g.mode') $('[data-gw-field]', root).hidden = e.target.value === 'none';
  });
}

function showFormError(form, message) {
  let box = $('[data-form-error]', form);
  if (!box) {
    box = document.createElement('div');
    box.dataset.formError = '';
    box.setAttribute('role', 'alert');
    form.prepend(box);
  }
  box.innerHTML = `<div class="alert alert--error" style="margin-bottom:16px">${icon('xCircle')}<div class="alert__body"><p class="alert__title">The system could not be saved</p><p class="alert__msg">${esc(message)}</p></div></div>`;
  box.scrollIntoView({ block: 'nearest' });
}

// ── Connect dialog ────────────────────────────────────
export async function openConnectDialog(conn, { forceDialog = false } = {}) {
  const running = ctx.state.sessions.find((s) => s.connectionId === conn.id && ['connecting', 'active', 'reconnecting'].includes(s.state));
  if (running) return alreadyConnected(conn, running);
  // SSH asks for sign-in in its terminal and web pages in the browser: nothing to set here.
  if ((conn.protocol || 'rdp') !== 'rdp') return runConnect(conn, null);
  if (!forceDialog && ctx.state.settings && ctx.state.settings.showConnectDialog === false) return runConnect(conn, null);

  const userId = uid('user');
  const allowSaved = (ctx.state.info.policy || {}).allowSavedCredentials !== false;
  const dlg = openDialog({
    title: `Connect to ${conn.name}`,
    subtitle: `<span class="mono">${esc(conn.host)}${conn.port && conn.port !== 3389 ? `:${conn.port}` : ''}</span>`,
    body: `<form id="connect-form" novalidate>
      <div class="field"><label class="field__label" for="${userId}">Username</label>
        <input class="input" id="${userId}" name="username" value="${esc(conn.username)}" placeholder="DOMAIN\\username" autocomplete="username" spellcheck="false" ${conn.username ? '' : 'autofocus'}>
        <span class="field__help">Leave empty to use the account Windows suggests.</span></div>
      <div class="field" data-cred-status><span class="field__label">Password</span><span class="field__help">Checking Windows Credential Manager…</span></div>
      <div class="form-section">${displayHtml(conn)}</div>
      <div class="form-section">${redirectHtml(conn)}</div>
      <details class="details form-section"><summary>Advanced settings</summary><div style="margin-top:12px">${advancedHtml(conn)}</div></details>
      <div class="form-section">${check('promptAlways', false, 'Sign in as a different user', 'Windows asks for a username and password. A saved password is not used for this connection.')}
        ${check('remember', false, 'Remember these options for this system')}</div>
    </form>`,
    footer: `<button class="btn btn--secondary" type="button" data-close>Cancel</button>
             <button class="btn btn--primary" type="submit" form="connect-form" ${conn.username ? 'autofocus' : ''}>Connect ${icon('arrowRight')}</button>`,
  });
  const form = $('#connect-form', dlg.el);
  wireDependentFields(form);
  form.elements.promptAlways.addEventListener('change', (e) => { form.elements.username.disabled = e.target.checked; });

  // Credential status (never shows the password itself)
  call(window.rdp.credentials.get(conn.id)).then((cred) => {
    const box = $('[data-cred-status]', dlg.el);
    if (!box) return;
    box.innerHTML = cred.saved
      ? `<span class="field__label">Password</span><span class="field__help">${icon('key', 16)} A password for <strong>${esc(cred.username)}</strong> is saved in Windows Credential Manager and will be used.</span>`
      : `<span class="field__label">Password</span><span class="field__help">Windows will ask for your password when the connection starts.${allowSaved ? ' You can save it in the system details.' : ''}</span>`;
  }).catch(() => {
    const box = $('[data-cred-status]', dlg.el);
    if (box) box.innerHTML = '<span class="field__label">Password</span><span class="field__help">Windows will ask for your password when the connection starts.</span>';
  });

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const opts = readOptions(form, conn);
    const overrides = { ...opts, username: form.elements.username.value.trim(), promptAlways: form.elements.promptAlways.checked };
    if (overrides.gateway.mode !== 'none' && !overrides.gateway.host) {
      form.elements['g.host'].setAttribute('aria-invalid', 'true');
      $('details', form).open = true;
      form.elements['g.host'].focus();
      return;
    }
    if (form.elements.remember.checked) {
      try { await call(window.rdp.connections.save({ ...conn, ...overrides })); await ctx.reload(); }
      catch (err) { showFormError(form, err.message); return; }
    }
    dlg.close();
    runConnect(conn, overrides);
  });
}

function alreadyConnected(conn, session) {
  const dlg = openDialog({
    title: 'Already connected', size: 'narrow',
    body: `<p>You already have a session to <strong>${esc(conn.name)}</strong>. Opening a second connection would take over the same remote session.</p>`,
    footer: `<button class="btn btn--secondary" type="button" data-close>Cancel</button>
             <button class="btn btn--primary" type="button" data-front>${icon('window')} Show session window</button>`,
  });
  $('[data-front]', dlg.el).addEventListener('click', () => { dlg.close(); ctx.focusSession(session.id); });
}

// ── Connection progress ───────────────────────────────
const RDP_STEPS = [
  ['check', 'Checking system availability'],
  ['validate', 'Validating connection settings'],
  ['secure', 'Preparing secure sign-in'],
  ['start', 'Starting Remote Desktop'],
  ['session', 'Waiting for the remote session'],
];

const SSH_STEPS = [
  ['check', 'Checking system availability'],
  ['validate', 'Checking the SSH client and settings'],
  ['start', 'Opening the terminal window'],
];

/** Web systems open in the default browser; there is no session to follow. */
async function openWeb(conn) {
  try {
    const res = await call(window.rdp.sessions.connect(conn.id, {}));
    if (res.outcome === 'opened') {
      toast(`${conn.name} opened in your browser`);
      await ctx.reload();
    }
  } catch (err) {
    errorDialog(`${conn.name} could not be opened`, err.message);
  }
}

export async function runConnect(conn, overrides, { force = false, quick = false } = {}) {
  const protocol = conn.protocol || 'rdp';
  if (protocol === 'web') return openWeb(conn);
  const STEPS = protocol === 'ssh' ? SSH_STEPS : RDP_STEPS;
  const stepState = Object.fromEntries(STEPS.map(([k]) => [k, 'pending']));
  let sessionId = null;
  let slowTimer = null;
  let finished = false;

  const dlg = openDialog({
    title: `Connecting to ${conn.name}`,
    subtitle: `<span class="mono">${esc(conn.host)}</span>`,
    body: `<ol class="steps" aria-live="polite">${STEPS.map(([k, l]) => `<li class="step" data-step="${k}"><span class="step__icon"></span><span>${l}</span><span class="visually-hidden" data-sr></span></li>`).join('')}</ol>
           <div data-note style="margin-top:20px"></div>`,
    footer: `<button class="btn btn--secondary" type="button" data-cancel>Run in background</button>`,
    onClose: () => { finished = true; clearTimeout(slowTimer); unsubscribe(); unsubSession(); },
  });

  const render = () => {
    for (const [k] of STEPS) {
      const li = $(`[data-step="${k}"]`, dlg.el);
      const st = stepState[k];
      li.className = `step step--${st}`;
      li.querySelector('.step__icon').innerHTML =
        st === 'running' ? '<span class="spinner" aria-hidden="true"></span>'
        : st === 'done' ? icon('checkCircle')
        : st === 'failed' ? icon('xCircle')
        : st === 'skipped' ? icon('warning')
        : '<span class="step__pending"></span>';
      li.setAttribute('aria-current', st === 'running' ? 'step' : 'false');
      li.querySelector('[data-sr]').textContent = { pending: ': not started', running: ': in progress', done: ': completed', failed: ': failed', skipped: ': skipped' }[st] || '';
    }
  };
  render();
  const note = (html) => { $('[data-note]', dlg.el).innerHTML = html; };
  const footer = (html) => { $('.dialog__footer', dlg.el).innerHTML = html; };

  const unsubscribe = window.rdp.on('connect:progress', (p) => {
    if (p.connectionId !== conn.id || finished || !(p.step in stepState)) return;
    stepState[p.step] = p.state;
    render();
  });
  let shownHint = null;
  const onSession = (s) => {
    if (s.id !== sessionId || finished) return;
    if (s.state === 'connecting' && s.result && s.result.hint && shownHint !== s.result.code) {
      // For example a wrong password: Remote Desktop lets the user try again in its own window.
      shownHint = s.result.code;
      const box = document.createElement('div');
      box.className = 'alert alert--warning';
      box.style.marginTop = '12px';
      box.setAttribute('role', 'status');
      box.innerHTML = `${icon('warning')}<div class="alert__body"><p class="alert__title">${esc(s.result.title)}</p><p class="alert__msg">${esc(s.result.message)} You can try again in the Remote Desktop window.</p></div>`;
      $('[data-note]', dlg.el).append(box);
      return;
    }
    if (s.state === 'active') {
      if ('session' in stepState) { stepState.session = 'done'; render(); }
      clearTimeout(slowTimer);
      toast(protocol === 'ssh' ? `Terminal for ${conn.name} opened` : `Connected to ${conn.name}`);
      dlg.close();
    } else if (s.state === 'failed' || s.state === 'ended') {
      if ('session' in stepState) { stepState.session = s.state === 'failed' ? 'failed' : 'skipped'; render(); }
      clearTimeout(slowTimer);
      showResult(s.result, s);
    }
  };
  const unsubSession = window.rdp.on('session:update', (s) => {
    // Updates may arrive before the connect call returns; keep the newest one per session.
    if (!sessionId) { earlyUpdates.set(s.id, s); return; }
    onSession(s);
  });
  const earlyUpdates = new Map();

  $('[data-cancel]', dlg.el).addEventListener('click', () => dlg.close());

  function showResult(result, s) {
    const r = result || { title: 'Connection could not be established', message: '' };
    const kind = r.kind === 'normal' ? 'info' : 'error';
    note(`<div class="alert alert--${kind}" role="alert">${icon(kind === 'error' ? 'xCircle' : 'info')}<div class="alert__body">
      <p class="alert__title">${esc(r.title)}</p><p class="alert__msg">${esc(r.message)}</p>
      ${r.code ? `<details class="details"><summary>Technical details</summary><dl class="kv"><dt>Code</dt><dd class="mono">${esc(r.code)}</dd><dt>Host</dt><dd class="mono">${esc(s.host)}</dd><dt>Started</dt><dd>${esc(new Date(s.startedAt).toLocaleString())}</dd></dl></details>` : ''}
      ${r.retry ? '<p class="small muted" style="margin-top:8px">Trying again is safe.</p>' : ''}
    </div></div>`);
    footer(`<button class="btn btn--secondary" type="button" data-close-final>Close</button>
            <button class="btn btn--primary" type="button" data-retry>${icon('refresh')} Try again</button>`);
    $('[data-close-final]', dlg.el).addEventListener('click', () => dlg.close());
    $('[data-retry]', dlg.el).addEventListener('click', () => { dlg.close(); runConnect(conn, overrides, { quick }); });
    $('[data-retry]', dlg.el).focus();
  }

  let res;
  try {
    res = await call(window.rdp.sessions.connect(conn.id, { force, overrides, quick }));
  } catch (err) {
    stepState.start = 'failed'; render();
    showResult({ title: 'Connection could not be established', message: err.message }, { host: conn.host, startedAt: new Date().toISOString() });
    return;
  }
  if (finished) return;

  if (res.outcome === 'alreadyActive') { dlg.close(); alreadyConnected(conn, res.session); return; }
  if (res.outcome === 'inProgress') { dlg.close(); toast(`A connection to ${conn.name} is already being started`, { kind: 'info' }); return; }
  if (res.outcome === 'blocked') {
    note(`<div class="alert alert--warning" role="alert">${icon('wrench')}<div class="alert__body"><p class="alert__title">${esc(res.title)}</p><p class="alert__msg">${esc(res.message)}</p></div></div>`);
    return;
  }
  if (res.outcome === 'unreachable') {
    note(`<div class="alert alert--error" role="alert">${icon('xCircle')}<div class="alert__body">
      <p class="alert__title">${esc(res.title)}</p><p class="alert__msg">${esc(res.message)}</p>
      <details class="details"><summary>Technical details</summary><p class="mono small">${esc(res.technical)}</p></details>
      <p class="small muted" style="margin-top:8px">Trying again is safe. "Connect anyway" starts the connection without the availability check, for example when only ${protocol === 'ssh' ? 'a jump host' : 'the gateway'} can reach the system.</p>
    </div></div>`);
    footer(`<button class="btn btn--secondary" type="button" data-close-final>Close</button>
            <button class="btn btn--secondary" type="button" data-force>Connect anyway</button>
            <button class="btn btn--primary" type="button" data-retry>${icon('refresh')} Try again</button>`);
    $('[data-close-final]', dlg.el).addEventListener('click', () => dlg.close());
    $('[data-retry]', dlg.el).addEventListener('click', () => { dlg.close(); runConnect(conn, overrides, { quick }); });
    $('[data-force]', dlg.el).addEventListener('click', () => { dlg.close(); runConnect(conn, overrides, { force: true, quick }); });
    $('[data-retry]', dlg.el).focus();
    return;
  }

  // Started: mstsc is open. Wait for the session event.
  sessionId = res.session.id;
  const newer = earlyUpdates.get(sessionId) || ctx.state.sessions.find((x) => x.id === sessionId);
  if (!newer) ctx.upsertSession(res.session);
  if (protocol === 'ssh') {
    // ssh runs as soon as the terminal opens; sign-in happens there.
    onSession(newer || res.session);
    return;
  }
  stepState.session = 'running'; render();
  const launchNote = res.session.launchMode === 'file' && !res.session.signed
    ? 'Windows shows a security confirmation for the connection. Check the computer name and the listed permissions, then select Connect.'
    : 'The Remote Desktop window opens separately.';
  note(`<div class="alert alert--info">${icon('info')}<div class="alert__body"><p class="alert__title">Continue in the Remote Desktop window</p>
    <p class="alert__msg">${launchNote} ${res.credentialSaved ? 'Your saved password is used.' : 'Windows asks for your password.'}</p>${res.launchNote ? `<p class="small muted" style="margin-top:8px">${esc(res.launchNote)}</p>` : ''}</div></div>`);
  footer(`<button class="btn btn--secondary" type="button" data-hide>Run in background</button>
          <button class="btn btn--danger" type="button" data-abort>Cancel connection</button>`);
  $('[data-hide]', dlg.el).addEventListener('click', () => dlg.close());
  $('[data-abort]', dlg.el).addEventListener('click', async () => { await call(window.rdp.sessions.disconnect(sessionId)); dlg.close(); });
  // The session may already have connected or failed while the connect call was returning.
  if (newer && newer.state !== 'connecting') onSession(newer);

  slowTimer = setTimeout(() => {
    if (finished) return;
    const extra = document.createElement('p');
    extra.className = 'small muted';
    extra.style.marginTop = '12px';
    extra.textContent = 'This is taking longer than usual. Remote Desktop may be waiting for your confirmation or password in its own window. You can keep this running in the background.';
    $('[data-note]', dlg.el).append(extra);
  }, 25000);
}

// ── Add / edit system ─────────────────────────────────
const TYPE_TEXT = {
  rdp: { label: 'Remote Desktop', icon: 'monitor', host: 'Computer name or IP address', hostPh: 'server01.domain.local', userPh: 'DOMAIN\\username' },
  ssh: { label: 'SSH', icon: 'terminal', host: 'Host name or IP address', hostPh: 'linux01.domain.local', userPh: 'for example admin' },
  web: { label: 'Web', icon: 'globe', host: 'Host name or IP address', hostPh: 'ilo01.domain.local', userPh: '' },
};

export function openEditDialog(existing = null) {
  const defaults = ctx.state.settings.defaults;
  const c = existing ? structuredClone(existing) : {
    name: '', host: '', port: 3389, username: '', folder: '', os: '', location: '', tags: [], description: '',
    credentialMode: 'prompt', favorite: false, protocol: 'rdp',
    ...structuredClone(defaults),
  };
  c.protocol = c.protocol || 'rdp';
  c.ssh = { identityFile: '', jumpHost: '', ...(c.ssh || {}) };
  c.web = { scheme: 'https', path: '', ...(c.web || {}) };
  const allowed = (ctx.state.info.policy || {}).allowedProtocols || PROTOCOLS;
  // Only switched-on types are offered; an existing system keeps its own type so it can still be edited.
  const types = PROTOCOLS.filter((p) => (ENABLED_PROTOCOLS.includes(p) && allowed.includes(p)) || p === c.protocol);
  const ids = Object.fromEntries(['name', 'host', 'port', 'user', 'folder', 'os', 'loc', 'tags', 'desc', 'key', 'jump', 'scheme', 'path', 'detect', 'preview'].map((k) => [k, uid(k)]));
  const folders = [...new Set(ctx.state.connections.map((x) => x.folder).filter(Boolean))].sort();
  const tabs = [['general', 'General'], ['display', 'Display'], ['redirect', 'Devices and clipboard'], ['advanced', 'Gateway and security']];
  const T = TYPE_TEXT[c.protocol];

  const dlg = openDialog({
    title: existing ? `Edit ${existing.name}` : 'Add system',
    size: 'wide',
    body: `<form id="edit-form" novalidate>
      ${types.length > 1 ? `<fieldset class="segmented" aria-describedby="${ids.detect}"><legend class="field__label">Connection type</legend>
        <div class="segmented__items">${types.map((p) => `<label class="segmented__item"><input type="radio" name="protocol" value="${p}" ${p === c.protocol ? 'checked' : ''}>${icon(TYPE_TEXT[p].icon, 16)}<span>${TYPE_TEXT[p].label}</span></label>`).join('')}</div>
      </fieldset>` : `<input type="hidden" name="protocol" value="${esc(c.protocol)}">`}
      <p class="small muted" id="${ids.detect}" role="status" aria-live="polite" data-detect style="margin:-8px 0 12px"></p>
      <div class="tabs" role="tablist" aria-label="System settings" data-for="rdp" ${c.protocol === 'rdp' ? '' : 'hidden'}>
        ${tabs.map(([k, l], i) => `<button class="tab" type="button" role="tab" id="tab-${k}" aria-controls="pane-${k}" aria-selected="${i === 0}" tabindex="${i === 0 ? 0 : -1}" data-tab="${k}">${l}</button>`).join('')}
      </div>
      <div role="tabpanel" id="pane-general" aria-labelledby="tab-general" data-pane="general">
        <div class="field-row field-row--host">
          <div class="field"><label class="field__label" for="${ids.host}"><span data-host-label>${T.host}</span> (required)</label>
            <input class="input mono" id="${ids.host}" name="host" value="${esc(c.host)}" required autocomplete="off" spellcheck="false" placeholder="${T.hostPh}" autofocus>
            ${types.length > 1 ? '<span class="field__help">You can also paste an address such as <span class="mono">ssh admin@linux01</span> or <span class="mono">https://ilo01</span>.</span>' : ''}
            <span class="field__error" data-err="host" hidden></span></div>
          <div class="field"><label class="field__label" for="${ids.port}">Port</label>
            <input class="input" id="${ids.port}" name="port" type="number" min="1" max="65535" value="${esc(c.port || defaultPort(c.protocol, c.web.scheme))}"></div>
        </div>
        <div class="field-row" style="margin-top:16px" data-for="web" ${c.protocol === 'web' ? '' : 'hidden'}>
          <div class="field"><label class="field__label" for="${ids.scheme}">Protocol</label>
            ${select('web.scheme', c.web.scheme, [['https', 'HTTPS (recommended)'], ['http', 'HTTP (not encrypted)']], { id: ids.scheme })}</div>
          <div class="field"><label class="field__label" for="${ids.path}">Path (optional)</label>
            <input class="input mono" id="${ids.path}" name="web.path" value="${esc(c.web.path)}" placeholder="/admin" autocomplete="off" spellcheck="false"></div>
        </div>
        <div class="field-row" style="margin-top:16px">
          <div class="field"><label class="field__label" for="${ids.name}">Display name</label>
            <input class="input" id="${ids.name}" name="name" value="${esc(c.name)}" placeholder="Finance Test Server"></div>
          <div class="field" data-for="rdp ssh" ${c.protocol === 'web' ? 'hidden' : ''}><label class="field__label" for="${ids.user}">Username</label>
            <input class="input" id="${ids.user}" name="username" value="${esc(c.username)}" placeholder="${T.userPh}" autocomplete="off" spellcheck="false"></div>
        </div>
        <div data-for="ssh" ${c.protocol === 'ssh' ? '' : 'hidden'}>
          <p class="small muted" style="margin-top:12px">${icon('info', 16)} SSH opens in a terminal window. The password is asked there and never stored by the app.</p>
          <details class="details form-section" ${c.ssh.identityFile || c.ssh.jumpHost ? 'open' : ''}><summary>SSH options</summary>
            <div class="field-row" style="margin-top:12px">
              <div class="field"><label class="field__label" for="${ids.key}">Key file (optional)</label>
                <input class="input mono" id="${ids.key}" name="ssh.identityFile" value="${esc(c.ssh.identityFile)}" placeholder="C:\\Users\\name\\.ssh\\id_ed25519" autocomplete="off" spellcheck="false">
                <span class="field__help">Leave empty to use your SSH agent or ~/.ssh/config.</span></div>
              <div class="field"><label class="field__label" for="${ids.jump}">Jump host (optional)</label>
                <input class="input mono" id="${ids.jump}" name="ssh.jumpHost" value="${esc(c.ssh.jumpHost)}" placeholder="admin@bastion01:22" autocomplete="off" spellcheck="false">
                <span class="field__help">Connect through this host first.</span></div>
            </div>
          </details>
        </div>
        <p class="small preview-line" id="${ids.preview}" data-preview></p>
        <div class="form-section">${check('favorite', c.favorite, 'Show in favorites')}</div>
        <details class="details form-section" ${c.folder || c.os || c.location || (c.tags || []).length || c.description ? 'open' : ''}><summary>Advanced settings</summary>
        <div class="field-row" style="margin-top:12px">
          <div class="field"><label class="field__label" for="${ids.folder}">Group</label>
            <input class="input" id="${ids.folder}" name="folder" value="${esc(c.folder)}" list="${ids.folder}-list" placeholder="For example Finance">
            <datalist id="${ids.folder}-list">${folders.map((f) => `<option value="${esc(f)}">`).join('')}</datalist></div>
          <div class="field"><label class="field__label" for="${ids.os}">Operating system</label>
            <input class="input" id="${ids.os}" name="os" value="${esc(c.os)}" placeholder="Windows Server 2025" list="${ids.os}-list">
            <datalist id="${ids.os}-list"><option value="Windows Server 2025"><option value="Windows Server 2022"><option value="Windows Server 2019"><option value="Windows 11 Enterprise"><option value="Windows 10 Enterprise"></datalist></div>
        </div>
        <div class="field-row" style="margin-top:16px">
          <div class="field"><label class="field__label" for="${ids.loc}">Location</label>
            <input class="input" id="${ids.loc}" name="location" value="${esc(c.location)}" placeholder="Graz"></div>
          <div class="field"><label class="field__label" for="${ids.tags}">Tags</label>
            <input class="input" id="${ids.tags}" name="tags" value="${esc((c.tags || []).join(', '))}" placeholder="sap, finance">
            <span class="field__help">Separate tags with commas.</span></div>
        </div>
        <div class="field" style="margin-top:16px"><label class="field__label" for="${ids.desc}">Description</label>
          <textarea class="textarea" id="${ids.desc}" name="description">${esc(c.description)}</textarea></div>
        </details>
      </div>
      <div role="tabpanel" id="pane-display" aria-labelledby="tab-display" data-pane="display" hidden>${displayHtml(c)}</div>
      <div role="tabpanel" id="pane-redirect" aria-labelledby="tab-redirect" data-pane="redirect" hidden>
        <p class="muted" style="margin-bottom:12px">Allow only what you need. Your administrators may restrict these options on the server.</p>${redirectHtml(c)}</div>
      <div role="tabpanel" id="pane-advanced" aria-labelledby="tab-advanced" data-pane="advanced" hidden>${advancedHtml(c)}</div>
    </form>`,
    footer: `<button class="btn btn--secondary" type="button" data-close>Cancel</button>
             <button class="btn btn--primary" type="submit" form="edit-form">${existing ? 'Save changes' : 'Add system'}</button>`,
  });
  const form = $('#edit-form', dlg.el);
  wireDependentFields(form);

  // Connection type: show only what the type needs; the port follows the type until the user changes it.
  const f0 = form.elements;
  const typeOf = () => (form.querySelector('[name="protocol"]:checked') || f0.protocol).value;
  let portTouched = Boolean(existing) && Number(f0.port.value) !== defaultPort(c.protocol, c.web.scheme);
  f0.port.addEventListener('input', () => { portTouched = true; });
  const preview = () => {
    const p = typeOf();
    const host = f0.host.value.trim();
    $('[data-preview]', form).textContent = host ? `Starts: ${describeTarget({ protocol: p, host, port: Number(f0.port.value) || defaultPort(p, f0['web.scheme'].value), username: f0.username.value.trim(), web: { scheme: f0['web.scheme'].value, path: f0['web.path'].value.trim() } })}` : '';
  };
  const applyType = (p) => {
    $$('[data-for]', form).forEach((el) => { el.hidden = !el.dataset.for.split(' ').includes(p); });
    if (p !== 'rdp') selectTab(tabEls[0], false);
    const t = TYPE_TEXT[p];
    $('[data-host-label]', form).textContent = t.host;
    f0.host.placeholder = t.hostPh;
    f0.username.placeholder = t.userPh;
    if (!portTouched) f0.port.value = defaultPort(p, f0['web.scheme'].value);
    preview();
  };
  form.addEventListener('change', (e) => {
    if (e.target.name === 'protocol') applyType(e.target.value);
    if (e.target.name === 'web.scheme' && !portTouched) f0.port.value = defaultPort('web', e.target.value);
  });
  form.addEventListener('input', preview);

  // Paste or type a full address ("ssh admin@host -p 2222", "https://ilo01/admin"): fill the fields from it.
  const detect = () => {
    const raw = f0.host.value.trim();
    if (types.length < 2 || !/[@\s]|:\/\/|:\d+$/.test(raw)) return;
    const t = parseTargets(raw)[0];
    if (!t || !types.includes(t.protocol)) return;
    const radio = form.querySelector(`[name="protocol"][value="${t.protocol}"]`);
    if (radio) radio.checked = true; else f0.protocol.value = t.protocol;
    f0.host.value = t.host;
    if (t.username) f0.username.value = t.username;
    if (t.protocol === 'web') { f0['web.scheme'].value = t.scheme; f0['web.path'].value = t.path; }
    portTouched = t.port !== defaultPort(t.protocol, t.scheme);
    f0.port.value = t.port;
    applyType(t.protocol);
    $('[data-detect]', form).textContent = `Recognized as ${TYPE_TEXT[t.protocol].label}. The fields were filled in from the address.`;
  };
  f0.host.addEventListener('change', detect);
  f0.host.addEventListener('paste', () => setTimeout(detect, 0));

  // Tabs with arrow-key navigation
  const tabEls = $$('[role="tab"]', form);
  function selectTab(tab, focus = true) {
    tabEls.forEach((t) => { const on = t === tab; t.setAttribute('aria-selected', on); t.tabIndex = on ? 0 : -1; $(`[data-pane="${t.dataset.tab}"]`, form).hidden = !on; });
    if (focus) tab.focus();
  }
  tabEls.forEach((t, i) => {
    t.addEventListener('click', () => selectTab(t));
    t.addEventListener('keydown', (e) => {
      if (e.key === 'ArrowRight') { e.preventDefault(); selectTab(tabEls[(i + 1) % tabEls.length]); }
      if (e.key === 'ArrowLeft') { e.preventDefault(); selectTab(tabEls[(i - 1 + tabEls.length) % tabEls.length]); }
    });
  });

  preview();

  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const f = form.elements;
    const protocol = typeOf();
    const hostErr = $('[data-err="host"]', form);
    if (!f.host.value.trim()) {
      selectTab(tabEls[0]);
      f.host.setAttribute('aria-invalid', 'true');
      hostErr.hidden = false;
      hostErr.innerHTML = `${icon('xCircle', 16)} Enter the computer name or IP address of the system.`;
      f.host.setAttribute('aria-describedby', hostErr.id = uid('err'));
      f.host.focus();
      return;
    }
    const opts = readOptions(form, c);
    if (protocol === 'rdp' && opts.gateway.mode !== 'none' && !opts.gateway.host) {
      selectTab(tabEls[3]);
      f['g.host'].setAttribute('aria-invalid', 'true');
      f['g.host'].focus();
      return;
    }
    const payload = {
      ...c, ...opts,
      protocol,
      host: f.host.value.trim(), port: Number(f.port.value) || defaultPort(protocol, f['web.scheme'].value), name: f.name.value.trim(),
      username: protocol === 'web' ? '' : f.username.value.trim(),
      folder: f.folder.value.trim(), os: f.os.value.trim(), location: f.location.value.trim(),
      tags: f.tags.value, description: f.description.value.trim(), favorite: f.favorite.checked,
      ssh: { identityFile: f['ssh.identityFile'].value.trim(), jumpHost: f['ssh.jumpHost'].value.trim() },
      web: { scheme: f['web.scheme'].value, path: f['web.path'].value.trim() },
    };
    // Gateway settings only apply to RDP; a hidden gateway must not block saving other types.
    if (protocol !== 'rdp') payload.gateway = { mode: 'none', host: '' };
    try {
      const saved = await call(window.rdp.connections.save(payload));
      dlg.close();
      await ctx.reload();
      toast(existing ? 'Changes saved' : `${saved.name} added`);
    } catch (err) {
      showFormError(form, err.message);
    }
  });
}

// ── Import .rdp ───────────────────────────────────────
export async function importRdpFiles() {
  let items;
  try { items = await call(window.rdp.connections.importRdp()); } catch (err) { errorDialog('Files could not be imported', err.message); return; }
  if (!items.length) return;
  const valid = items.filter((i) => i.connection && i.connection.host);
  const PREVIEW = 60;
  let shown = 0;
  const dlg = openDialog({
    title: `Import ${valid.length} ${valid.length === 1 ? 'connection' : 'connections'}`,
    subtitle: 'Check the target systems and requested permissions before you import. Files from email or downloads can be used for phishing.',
    size: 'wide',
    body: items.map((i) => {
      if (i.notice) {
        return `<div class="alert alert--info" style="margin-bottom:12px">${icon('info')}<div class="alert__body"><p class="alert__title">${esc(i.file)}</p><p class="alert__msg">${esc(i.notice)}</p></div></div>`;
      }
      if (i.error || !i.connection || !i.connection.host) {
        return `<div class="alert alert--error">${icon('xCircle')}<div class="alert__body"><p class="alert__title">${esc(i.file)} cannot be imported</p><p class="alert__msg">${esc(i.error || 'The file does not contain a computer address.')}</p></div></div>`;
      }
      if (++shown > PREVIEW) return '';
      const c = i.connection;
      const perms = [c.redirect.clipboard && 'Clipboard', c.redirect.printers && 'Printers', c.redirect.microphone && 'Microphone', c.redirect.smartcards && 'Smart cards'].filter(Boolean);
      return `<div class="target" style="align-items:flex-start;margin-bottom:12px">${icon('file')}
        <div style="flex:1;min-width:0">
          <p class="card__title">${esc(c.name)}</p>
          <p class="card__host">${esc(c.host)}${c.port !== 3389 ? `:${c.port}` : ''}${c.gateway.mode !== 'none' ? ` via gateway ${esc(c.gateway.host)}` : ''}</p>
          <p class="small" style="margin-top:4px">Permissions: ${perms.length ? esc(perms.join(', ')) : 'none'}${c.username ? ` · User: ${esc(c.username)}` : ''}</p>
          ${i.warnings.length ? `<ul class="small" style="margin:8px 0 0;padding-left:20px;color:var(--color-warning)">${i.warnings.map((w) => `<li>${esc(w)}</li>`).join('')}</ul>` : ''}
        </div></div>`;
    }).join('') + (valid.length > PREVIEW ? `<p class="muted">… and ${valid.length - PREVIEW} more ${valid.length - PREVIEW === 1 ? 'connection' : 'connections'}.</p>` : ''),
    footer: `<button class="btn btn--secondary" type="button" data-close>Cancel</button>
             <button class="btn btn--primary" type="button" data-import ${valid.length ? '' : 'disabled'}>Import ${valid.length} ${valid.length === 1 ? 'system' : 'systems'}</button>`,
  });
  $('[data-import]', dlg.el).addEventListener('click', async () => {
    try {
      const n = await call(window.rdp.connections.confirmImport(valid.map((v) => v.connection)));
      dlg.close();
      await ctx.reload();
      toast(`${n} ${n === 1 ? 'system' : 'systems'} imported`);
    } catch (err) { errorDialog('Systems could not be imported', err.message); }
  });
}

// ── Saved credentials ─────────────────────────────────
export function openCredentialDialog(conn, onDone) {
  const uId = uid('u'); const pId = uid('p');
  const dlg = openDialog({
    title: 'Save password',
    subtitle: `For <span class="mono">${esc(conn.host)}</span>`,
    body: `<form id="cred-form" novalidate autocomplete="off">
      <div class="alert alert--info" style="margin-bottom:16px">${icon('shield')}<div class="alert__body">
        <p class="alert__title">Where is the password stored?</p>
        <p class="alert__msg">In Windows Credential Manager on this computer, protected by your Windows account (entry <span class="mono">TERMSRV/${esc(conn.host)}</span>). This app never stores or shows it. Single sign-on or typing the password each time is safer on shared computers.</p></div></div>
      <div class="field"><label class="field__label" for="${uId}">Username</label>
        <input class="input" id="${uId}" name="username" value="${esc(conn.username)}" placeholder="DOMAIN\\username" spellcheck="false" ${conn.username ? '' : 'autofocus'}></div>
      <div class="field"><label class="field__label" for="${pId}">Password</label>
        <input class="input" id="${pId}" name="password" type="password" autocomplete="new-password" ${conn.username ? 'autofocus' : ''}>
        <span class="field__error" data-err hidden></span></div>
    </form>`,
    footer: `<button class="btn btn--secondary" type="button" data-close>Cancel</button>
             <button class="btn btn--primary" type="submit" form="cred-form">Save password</button>`,
  });
  const form = $('#cred-form', dlg.el);
  form.addEventListener('submit', async (e) => {
    e.preventDefault();
    const err = $('[data-err]', form);
    const username = form.elements.username.value.trim();
    const password = form.elements.password.value;
    if (!username || !password) {
      err.hidden = false;
      err.innerHTML = `${icon('xCircle', 16)} Enter both username and password.`;
      return;
    }
    const btn = $('button[type="submit"]', dlg.el);
    btn.disabled = true;
    btn.innerHTML = '<span class="spinner" aria-hidden="true"></span> Saving';
    try {
      await call(window.rdp.credentials.save({ id: conn.id, username, password }));
      form.elements.password.value = '';
      dlg.close();
      toast('Password saved in Windows Credential Manager');
      if (onDone) onDone();
    } catch (ex) {
      btn.disabled = false; btn.textContent = 'Save password';
      err.hidden = false;
      err.innerHTML = `${icon('xCircle', 16)} The password could not be saved. ${esc(ex.message)}`;
    }
  });
}

export async function removeCredential(conn, onDone) {
  const ok = await confirmDialog({
    title: 'Remove saved password?',
    message: `The saved password for ${conn.host} is deleted from Windows Credential Manager. Windows asks for your password at the next connection.`,
    confirmLabel: 'Remove password', danger: true,
  });
  if (!ok) return;
  try {
    await call(window.rdp.credentials.remove(conn.id));
    toast('Saved password removed');
    if (onDone) onDone();
  } catch (err) { errorDialog('Password could not be removed', err.message); }
}
