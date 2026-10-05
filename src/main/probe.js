'use strict';
// TCP reachability check (ICMP is often blocked, so we open the RDP/gateway port).

const net = require('node:net');

function probe(host, port = 3389, timeoutMs = 3000) {
  return new Promise((resolve) => {
    const started = Date.now();
    const socket = new net.Socket();
    let done = false;
    const finish = (result) => {
      if (done) return;
      done = true;
      socket.destroy();
      resolve({ checkedAt: new Date().toISOString(), ...result });
    };
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => finish({ reachable: true, latencyMs: Date.now() - started }));
    socket.once('timeout', () => finish({ reachable: false, reason: 'timeout' }));
    socket.once('error', (err) => {
      const code = err && err.code;
      let reason = 'error';
      if (code === 'ENOTFOUND' || code === 'EAI_AGAIN') reason = 'dns';
      else if (code === 'ECONNREFUSED') reason = 'refused';
      else if (code === 'ETIMEDOUT') reason = 'timeout';
      else if (code === 'ENETUNREACH' || code === 'EHOSTUNREACH') reason = 'unreachable';
      finish({ reachable: false, reason, detail: code });
    });
    try {
      socket.connect(port, host);
    } catch (err) {
      finish({ reachable: false, reason: 'error', detail: String(err.message || err) });
    }
  });
}

/** Run probes with limited concurrency so servers are not hammered. */
async function probeMany(targets, concurrency = 6) {
  const results = {};
  let index = 0;
  async function worker() {
    while (index < targets.length) {
      const t = targets[index++];
      results[t.id] = await probe(t.host, t.port);
    }
  }
  await Promise.all(Array.from({ length: Math.min(concurrency, targets.length) }, worker));
  return results;
}

module.exports = { probe, probeMany };
