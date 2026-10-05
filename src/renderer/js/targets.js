// Connection types and parsing of typed addresses ("ssh user@host -p 2222", "https://ilo01", "srv01:3389").
// Shared by the renderer (detection, previews) and the main process (authoritative validation before launch).

export const PROTOCOLS = ['rdp', 'ssh', 'web'];
export const PROTOCOL_LABELS = { rdp: 'RDP', ssh: 'SSH', web: 'Web' };

// Host names, IPv4 and IPv6. Never starting with "-" or ".", so a host can never be read as a program option.
const HOST_RE = /^(?![-.])[A-Za-z0-9._-]{1,253}$/;
const IPV6_RE = /^[0-9A-Fa-f:.]+$/;
// SSH user names: letters, digits and . _ @ \ - only (DOMAIN\user and user@domain work). No spaces, quotes or shell characters.
const SSH_USER_RE = /^(?!-)[A-Za-z0-9._@\\-]{1,104}$/;

export function isHost(h) {
  const s = String(h || '');
  return HOST_RE.test(s) || (IPV6_RE.test(s) && (s.match(/:/g) || []).length > 1);
}

export function isSshUser(u) {
  const s = String(u || '');
  return SSH_USER_RE.test(s) && !s.endsWith('\\');
}

export function defaultPort(protocol, scheme = 'https') {
  if (protocol === 'ssh') return 22;
  if (protocol === 'web') return scheme === 'http' ? 80 : 443;
  return 3389;
}

/** "host", "host:port", "[v6]:port" or a bare IPv6 address. Returns null when the text is not an address. */
function hostPort(text) {
  const s = String(text || '');
  let m = /^\[([0-9A-Fa-f:.]+)\](?::(\d{1,5}))?$/.exec(s);
  if (m) return isHost(m[1]) ? { host: m[1], port: m[2] ? Number(m[2]) : null } : null;
  if (IPV6_RE.test(s) && (s.match(/:/g) || []).length > 1) return isHost(s) ? { host: s, port: null } : null;
  m = /^([^:]+)(?::(\d{1,5}))?$/.exec(s);
  if (!m || !isHost(m[1])) return null;
  const port = m[2] ? Number(m[2]) : null;
  if (port !== null && (port < 1 || port > 65535)) return null;
  return { host: m[1], port };
}

/** "user@host[:port]" (the last @ separates the host, so user@domain@host works). */
function userHostPort(text) {
  const s = String(text || '');
  const at = s.lastIndexOf('@');
  if (at <= 0) return null;
  const user = s.slice(0, at);
  const hp = hostPort(s.slice(at + 1));
  return hp && isSshUser(user) ? { ...hp, username: user } : null;
}

function target(protocol, { host, port = null, username = '', scheme = 'https', path = '' }, explicit) {
  return { protocol, host, port: port || defaultPort(protocol, scheme), username, scheme, path, explicit };
}

/** The tokens of "ssh [-p N] [-l user] [user@]host[:port]". */
function parseSshCommand(rest) {
  const tokens = rest.split(/\s+/).filter(Boolean);
  let port = null;
  let user = '';
  let dest = null;
  for (let i = 0; i < tokens.length; i += 1) {
    const t = tokens[i];
    if (t === '-p' && /^\d{1,5}$/.test(tokens[i + 1] || '')) { port = Number(tokens[i += 1]); continue; }
    if (t === '-l' && isSshUser(tokens[i + 1])) { user = tokens[i += 1]; continue; }
    if (dest !== null || t.startsWith('-')) return null;
    dest = t;
  }
  if (!dest) return null;
  const parsed = userHostPort(dest) || hostPort(dest);
  if (!parsed) return null;
  return target('ssh', { host: parsed.host, port: port || parsed.port, username: user || parsed.username || '' }, true);
}

function parseUrl(text) {
  let url;
  try { url = new URL(text); } catch { return null; }
  const scheme = url.protocol.replace(':', '').toLowerCase();
  const host = url.hostname.replace(/^\[|\]$/g, '');
  if (!isHost(host)) return null;
  const port = url.port ? Number(url.port) : null;
  if (scheme === 'http' || scheme === 'https') {
    const path = `${url.pathname === '/' ? '' : url.pathname}${url.search}`;
    return target('web', { host, port, scheme, path: path.slice(0, 500) }, true);
  }
  const user = decodeURIComponent(url.username || '');
  if (scheme === 'ssh') return user && !isSshUser(user) ? null : target('ssh', { host, port, username: user }, true);
  if (scheme === 'rdp') return target('rdp', { host, port }, true);
  return null;
}

/**
 * Possible targets for typed text, most likely first. Explicit forms (ssh …, rdp …, web …, URLs) give one
 * target; a bare host gives RDP first with SSH and Web as alternatives.
 */
export function parseTargets(input) {
  const text = String(input || '').trim();
  if (!text || /[\u0000-\u001f\u007f]/.test(text)) return [];
  const kw = /^(ssh|rdp|web)\s+(.+)$/i.exec(text);
  if (kw) {
    const p = kw[1].toLowerCase();
    if (p === 'ssh') { const t = parseSshCommand(kw[2]); return t ? [t] : []; }
    const inner = parseTargets(kw[2]).find((t) => t.protocol === p) || null;
    if (inner) return [{ ...inner, explicit: true }];
    const hp = hostPort(kw[2]);
    return hp ? [target(p, hp, true)] : [];
  }
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(text)) { const t = parseUrl(text); return t ? [t] : []; }

  const uhp = userHostPort(text);
  if (uhp) return [target('ssh', uhp, false), target('rdp', { ...uhp, port: null }, false)];
  const hp = hostPort(text);
  if (!hp) return [];
  if (hp.port === 22) return [target('ssh', hp, false)];
  if ([80, 8080].includes(hp.port)) return [target('web', { ...hp, scheme: 'http' }, false)];
  if ([443, 8443].includes(hp.port)) return [target('web', hp, false)];
  if (hp.port) return [target('rdp', hp, false)];
  return [target('rdp', hp, false), target('ssh', hp, false), target('web', hp, false)];
}

/** The URL a web connection opens. Built from validated fields, never from free text. */
export function webUrl({ host, port, web = {} }) {
  const scheme = web.scheme === 'http' ? 'http' : 'https';
  const h = host.includes(':') ? `[${host}]` : host;
  const p = port && port !== defaultPort('web', scheme) ? `:${port}` : '';
  const path = String(web.path || '');
  return `${scheme}://${h}${p}${path.startsWith('/') || path.startsWith('?') ? path : path ? `/${path}` : ''}`;
}

/** One line that says what a connection will do, for previews and screen readers. */
export function describeTarget({ protocol, host, port, username, web }) {
  if (protocol === 'web') return `Opens ${webUrl({ host, port, web })} in your browser`;
  const p = port && port !== defaultPort(protocol) ? `:${port}` : '';
  const as = username ? ` as ${username}` : '';
  return protocol === 'ssh' ? `SSH terminal to ${host}${p}${as}` : `Remote Desktop to ${host}${p}${as}`;
}
