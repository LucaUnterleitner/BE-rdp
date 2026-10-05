'use strict';
// Central, read-only system list published by IT.
// The list (systems.json) and a detached Ed25519 signature (systems.json.sig, base64) are loaded from an
// HTTPS URL (Windows Integrated Authentication) or a file/UNC path configured in the policy. The public key
// comes from the policy. A verified copy is cached locally so the list is available offline.

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { normalizeConnection } = require('./store');

const SCHEMA = 'bp-rdp-systems/1';
const MAX_BYTES = 2 * 1024 * 1024;
const FETCH_TIMEOUT_MS = 8000;
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

/** Verify the detached signature and parse the document. Throws with a user-facing message. */
function verifyDocument(bytes, signatureB64, publicKeyB64) {
  let key;
  try {
    key = crypto.createPublicKey({ key: Buffer.from(publicKeyB64, 'base64'), format: 'der', type: 'spki' });
  } catch {
    throw new Error('The public key for the central system list in the policy is not valid.');
  }
  const ok = crypto.verify(null, bytes, key, Buffer.from(String(signatureB64).trim(), 'base64'));
  if (!ok) throw new Error('The signature of the central system list is not valid. The list was not used.');
  let doc;
  try {
    doc = JSON.parse(bytes.toString('utf8'));
  } catch {
    throw new Error('The central system list is not valid JSON.');
  }
  if (doc.schema !== SCHEMA) throw new Error('The central system list has an unknown format.');
  if (!Number.isInteger(doc.version) || doc.version < 1) throw new Error('The central system list has no valid version.');
  if (doc.expiresAt && Date.parse(doc.expiresAt) < Date.now()) throw new Error('The central system list has expired. Contact IT.');
  if (!Array.isArray(doc.systems)) throw new Error('The central system list contains no systems.');
  return doc;
}

/** Turn verified entries into read-only connections with namespaced ids. Invalid entries are skipped. */
function toConnections(doc, defaults) {
  const out = [];
  const seen = new Set();
  for (const raw of doc.systems.slice(0, 5000)) {
    if (!raw || typeof raw !== 'object' || !UUID.test(String(raw.id)) || seen.has(raw.id)) continue;
    seen.add(raw.id);
    try {
      const c = normalizeConnection({ ...raw, id: undefined, favorite: false, sample: false, lastConnectedAt: null }, defaults);
      out.push({ ...c, id: `central-${raw.id.toLowerCase()}`, source: 'central' });
    } catch { /* skip invalid entry */ }
  }
  return out;
}

class CentralList {
  constructor({ config, cacheDir, defaults, fetchImpl }) {
    this.config = config || null;
    this.cacheDir = cacheDir;
    this.defaults = defaults;
    this.fetchImpl = fetchImpl;
    this.systems = [];
    this.info = { configured: Boolean(config), state: config ? 'loading' : 'off' };
  }

  async refresh() {
    if (!this.config) return this.info;
    const cached = this.readCache();
    try {
      const { bytes, signature } = await this.download();
      const doc = verifyDocument(bytes, signature, this.config.publicKey);
      if (cached && doc.version < cached.doc.version) {
        throw new Error(`The downloaded list (version ${doc.version}) is older than the cached one (version ${cached.doc.version}). It was not used.`);
      }
      this.writeCache(bytes, signature);
      this.apply(doc, { state: 'current', fetchedAt: new Date().toISOString() });
    } catch (err) {
      if (cached) {
        this.apply(cached.doc, { state: 'offline', fetchedAt: cached.fetchedAt, error: String(err.message || err) });
      } else {
        this.systems = [];
        this.info = { configured: true, state: 'error', error: String(err.message || err) };
      }
    }
    return this.info;
  }

  apply(doc, meta) {
    this.systems = toConnections(doc, this.defaults);
    const maxAgeHours = Number(this.config.maxAgeHours) || 72;
    const stale = meta.fetchedAt && Date.now() - Date.parse(meta.fetchedAt) > maxAgeHours * 3600000;
    this.info = { configured: true, version: doc.version, issuedAt: doc.issuedAt || null, count: this.systems.length, stale: Boolean(stale), ...meta };
  }

  async download() {
    if (this.config.url) {
      const get = async (url) => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS);
        try {
          const res = await this.fetchImpl(url, { signal: controller.signal, cache: 'no-store' });
          if (!res.ok) throw new Error(`The central system list could not be loaded (HTTP ${res.status}).`);
          const buf = Buffer.from(await res.arrayBuffer());
          if (buf.length > MAX_BYTES) throw new Error('The central system list is too large.');
          return buf;
        } catch (err) {
          if (err.name === 'AbortError') throw new Error('The central system list did not respond in time. Check the VPN.');
          throw err;
        } finally {
          clearTimeout(timer);
        }
      };
      const [bytes, sig] = await Promise.all([get(this.config.url), get(`${this.config.url}.sig`)]);
      return { bytes, signature: sig.toString('utf8') };
    }
    const file = this.config.path;
    const stat = await fs.promises.stat(file);
    if (stat.size > MAX_BYTES) throw new Error('The central system list is too large.');
    const [bytes, sig] = await Promise.all([fs.promises.readFile(file), fs.promises.readFile(`${file}.sig`, 'utf8')]);
    return { bytes, signature: sig };
  }

  readCache() {
    try {
      const bytes = fs.readFileSync(path.join(this.cacheDir, 'systems.json'));
      const signature = fs.readFileSync(path.join(this.cacheDir, 'systems.json.sig'), 'utf8');
      const doc = verifyDocument(bytes, signature, this.config.publicKey);
      const fetchedAt = fs.statSync(path.join(this.cacheDir, 'systems.json')).mtime.toISOString();
      return { doc, fetchedAt };
    } catch {
      return null;
    }
  }

  writeCache(bytes, signature) {
    try {
      fs.mkdirSync(this.cacheDir, { recursive: true });
      fs.writeFileSync(path.join(this.cacheDir, 'systems.json'), bytes);
      fs.writeFileSync(path.join(this.cacheDir, 'systems.json.sig'), signature);
    } catch { /* cache is optional */ }
  }

  get(id) {
    return this.systems.find((s) => s.id === id) || null;
  }
}

module.exports = { CentralList, verifyDocument, toConnections, SCHEMA };
