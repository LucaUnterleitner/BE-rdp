'use strict';
// Import of connection lists from Remote Desktop Connection Manager (.rdg) and mRemoteNG (confCons.xml).
// Imported files are untrusted: stored passwords (DPAPI/AES encrypted) and start programs are never imported,
// local drive redirection is imported as off, and server identity checks are never weakened.

const { XMLParser } = require('fast-xml-parser');
const { ENABLED_PROTOCOLS } = require('../renderer/js/targets.js');

const MAX_XML_BYTES = 5 * 1024 * 1024;

function parseXml(text, arrays) {
  if (text.length > MAX_XML_BYTES) throw new Error('The file is too large.');
  // No DTDs: they enable entity expansion attacks and are never needed for these formats.
  if (/<!DOCTYPE|<!ENTITY/i.test(text)) throw new Error('The file contains a document type definition and is not imported.');
  const parser = new XMLParser({
    ignoreAttributes: false,
    attributeNamePrefix: '@_',
    processEntities: false,
    parseTagValue: false,
    parseAttributeValue: false,
    trimValues: true,
    isArray: (name) => arrays.includes(name),
  });
  return parser.parse(text);
}

const bool = (v) => String(v).toLowerCase() === 'true';
const str = (v) => (v === undefined || v === null ? '' : typeof v === 'object' ? String(v['#text'] ?? '') : String(v)).trim();

// ── RDCMan ────────────────────────────────────────────
const RDG_BLOCKS = ['logonCredentials', 'connectionSettings', 'gatewaySettings', 'remoteDesktop', 'localResources', 'securitySettings'];

/** Settings blocks of a node. Schema 3 keeps them next to <properties>, schema 1 inside it. */
function rdgBlocks(node) {
  const out = {};
  for (const b of RDG_BLOCKS) {
    const el = node[b] !== undefined ? node[b] : node.properties && node.properties[b];
    if (el !== undefined && el !== '' && !(el && el['@_inherit'] === 'FromParent')) out[b] = el;
  }
  return out;
}

function parseRdg(text) {
  const doc = parseXml(text, ['group', 'server']);
  const root = doc.RDCMan;
  if (!root || !root.file) throw new Error('This is not a Remote Desktop Connection Manager (.rdg) file.');
  const results = [];
  const fileWarnings = new Set();

  const walk = (node, path, inherited) => {
    const blocks = { ...inherited, ...rdgBlocks(node) };
    for (const server of node.server || []) {
      results.push(rdgServer(server, path, { ...blocks, ...rdgBlocks(server) }, fileWarnings));
    }
    for (const group of node.group || []) {
      const name = str(group.properties && group.properties.name) || 'Group';
      walk(group, [...path, name], blocks);
    }
  };
  walk(root.file, [], {});
  return { items: results, warnings: [...fileWarnings] };
}

function rdgServer(server, path, b, fileWarnings) {
  const p = server.properties || {};
  const host = str(p.name);
  const warnings = [];
  const cred = b.logonCredentials || {};
  const conn = b.connectionSettings || {};
  const gw = b.gatewaySettings || {};
  const rd = b.remoteDesktop || {};
  const lr = b.localResources || {};
  const sec = b.securitySettings || {};

  if (str(cred.password)) warnings.push('A stored password was found and was discarded.');
  if (str(gw.password)) warnings.push('A stored gateway password was found and was discarded.');
  if (str(conn.startProgram)) warnings.push('A start program was found and was not imported.');
  if (lr.redirectDrives !== undefined && bool(lr.redirectDrives)) warnings.push('The file requested local drive access. It is imported as switched off.');
  if (/none/i.test(str(sec.authentication))) warnings.push('The file disabled server identity checks. The app uses "warn" instead.');
  if (warnings.some((w) => w.includes('password'))) fileWarnings.add('Passwords from the file are never imported. Save them again in the app if needed.');

  const user = str(cred.userName);
  const domain = str(cred.domain);
  const size = /(\d+)\s*x\s*(\d+)/i.exec(str(rd.size));
  const audio = { Client: 'local', Remote: 'remote', NoSound: 'none' }[str(lr.audioRedirection)] || 'local';

  return {
    connection: {
      name: str(p.displayName) || host,
      host,
      port: Number(str(conn.port)) || 3389,
      username: user ? (domain && !user.includes('\\') && !user.includes('@') ? `${domain}\\${user}` : user) : '',
      folder: path.join(' / '),
      description: str(p.comment),
      display: {
        mode: bool(rd.fullScreen) ? 'fullscreen' : size ? 'window' : 'fullscreen',
        width: size ? Number(size[1]) : 1920,
        height: size ? Number(size[2]) : 1080,
        multimon: false,
        dynamicResolution: true,
        smartSizing: false,
      },
      redirect: {
        clipboard: lr.redirectClipboard === undefined ? true : bool(lr.redirectClipboard),
        drives: 'none',
        printers: bool(lr.redirectPrinters),
        audio,
        microphone: false,
        smartcards: bool(lr.redirectSmartCards),
      },
      gateway: bool(gw.enabled) && str(gw.hostName)
        ? { mode: bool(gw.localBypass) ? 'detect' : 'always', host: str(gw.hostName) }
        : { mode: 'none', host: '' },
      security: {
        adminSession: bool(conn.connectToConsole),
        credentialProtection: 'none',
        authLevel: /^(none|0)$/i.test(str(sec.authentication)) ? 2 : /^(fail|1)$/i.test(str(sec.authentication)) ? 1 : 2,
      },
    },
    warnings,
  };
}

// ── mRemoteNG ─────────────────────────────────────────
const MR_INHERITABLE = ['Username', 'Domain', 'Port', 'RDGatewayUsageMethod', 'RDGatewayHostname', 'Resolution',
  'RedirectClipboard', 'RedirectPrinters', 'RedirectSound', 'RedirectSmartCards', 'UseConsoleSession', 'RDPAuthenticationLevel'];

/** protocols: connection types to import; others are counted as skipped. */
function parseMremote(text, { protocols = ENABLED_PROTOCOLS } = {}) {
  const doc = parseXml(text, ['Node']);
  const root = doc.Connections;
  if (!root) throw new Error('This is not an mRemoteNG connection file (confCons.xml).');
  if (bool(root['@_FullFileEncryption'])) {
    throw new Error('The file is fully encrypted. Export it from mRemoteNG without full-file encryption, then import it again.');
  }
  const results = [];
  const skipped = {};
  let sawPassword = false;

  const resolve = (node, parent) => {
    const values = {};
    for (const f of MR_INHERITABLE) values[f] = bool(node[`@_Inherit${f}`]) && parent ? parent[f] : node[`@_${f}`];
    return values;
  };
  const walk = (nodes, path, parentValues) => {
    for (const node of nodes || []) {
      const values = resolve(node, parentValues);
      if (node['@_Type'] === 'Container') {
        walk(node.Node, [...path, str(node['@_Name']) || 'Folder'], values);
        continue;
      }
      if (node['@_Type'] !== 'Connection') continue;
      const proto = str(node['@_Protocol']).toUpperCase();
      const warnings = [];
      if (str(node['@_Password']) || str(node['@_RDGatewayPassword'])) { warnings.push('A stored password was found and was discarded.'); sawPassword = true; }
      if (proto === 'RDP') {
        if (str(node['@_RedirectDiskDrives']).toLowerCase() === 'true') warnings.push('The file requested local drive access. It is imported as switched off.');
        results.push({ connection: mremoteConnection(node, values, path), warnings });
      } else if ((proto === 'SSH2' || proto === 'SSH1') && protocols.includes('ssh')) {
        if (proto === 'SSH1') warnings.push('SSH version 1 is outdated and insecure. The connection uses SSH version 2.');
        if (str(node['@_PuttySession']) && str(node['@_PuttySession']) !== 'Default Settings') warnings.push('PuTTY session settings are not imported.');
        results.push({ connection: { ...mremoteBase(node, values, path), protocol: 'ssh', username: str(values.Username), port: Number(str(values.Port)) || 22 }, warnings });
      } else if ((proto === 'HTTP' || proto === 'HTTPS') && protocols.includes('web')) {
        const scheme = proto === 'HTTP' ? 'http' : 'https';
        results.push({ connection: { ...mremoteBase(node, values, path), protocol: 'web', username: '', port: Number(str(values.Port)) || (scheme === 'http' ? 80 : 443), web: { scheme, path: '' } }, warnings });
      } else {
        skipped[proto || 'unknown'] = (skipped[proto || 'unknown'] || 0) + 1;
      }
    }
  };
  walk(root.Node, [], null);

  const warnings = [];
  if (sawPassword) warnings.push('Passwords from the file are never imported. Save them again in the app if needed.');
  const skippedTotal = Object.values(skipped).reduce((a, b) => a + b, 0);
  if (skippedTotal) warnings.push(`${skippedTotal} connection${skippedTotal === 1 ? '' : 's'} with unsupported types were skipped (${Object.entries(skipped).map(([k, n]) => `${k}: ${n}`).join(', ')}).`);
  return { items: results, warnings };
}

/** Fields every connection type shares. */
function mremoteBase(node, v, path) {
  const host = str(node['@_Hostname']);
  const user = str(v.Username);
  const domain = str(v.Domain);
  return {
    name: str(node['@_Name']) || host,
    host,
    username: user ? (domain && !user.includes('\\') && !user.includes('@') ? `${domain}\\${user}` : user) : '',
    folder: path.join(' / '),
    description: str(node['@_Descr']),
  };
}

function mremoteConnection(node, v, path) {
  const host = str(node['@_Hostname']);
  const user = str(v.Username);
  const domain = str(v.Domain);
  const res = str(v.Resolution);
  const size = /^Res(\d+)x(\d+)$/i.exec(res) || /^(\d+)x(\d+)$/i.exec(res);
  const gwMode = { Always: 'always', Detect: 'detect' }[str(v.RDGatewayUsageMethod)] || 'none';
  const audio = { BringToThisComputer: 'local', LeaveAtRemoteComputer: 'remote', DoNotPlay: 'none' }[str(v.RedirectSound)] || 'local';
  const auth = str(v.RDPAuthenticationLevel);
  return {
    name: str(node['@_Name']) || host,
    host,
    port: Number(str(v.Port)) || 3389,
    username: user ? (domain && !user.includes('\\') && !user.includes('@') ? `${domain}\\${user}` : user) : '',
    folder: path.join(' / '),
    description: str(node['@_Descr']),
    display: {
      mode: size ? 'window' : 'fullscreen',
      width: size ? Number(size[1]) : 1920,
      height: size ? Number(size[2]) : 1080,
      multimon: false,
      dynamicResolution: true,
      smartSizing: /SmartSize/i.test(res),
    },
    redirect: {
      clipboard: v.RedirectClipboard === undefined ? true : bool(v.RedirectClipboard),
      drives: 'none',
      printers: bool(v.RedirectPrinters),
      audio,
      microphone: false,
      smartcards: bool(v.RedirectSmartCards),
    },
    gateway: gwMode !== 'none' && str(v.RDGatewayHostname) ? { mode: gwMode, host: str(v.RDGatewayHostname) } : { mode: 'none', host: '' },
    security: {
      adminSession: bool(v.UseConsoleSession),
      credentialProtection: 'none',
      authLevel: auth === 'AuthRequired' ? 1 : 2,
    },
  };
}

/** Pick the importer by content. */
function detectAndParse(text) {
  if (/<RDCMan[\s>]/.test(text)) return { format: 'RDCMan', ...parseRdg(text) };
  if (/<Connections[\s>]/.test(text)) return { format: 'mRemoteNG', ...parseMremote(text) };
  throw new Error('The file format is not supported. Use .rdp, RDCMan (.rdg) or mRemoteNG (confCons.xml) files.');
}

module.exports = { parseRdg, parseMremote, detectAndParse };
