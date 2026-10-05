'use strict';
// Launch details for connection types other than RDP.
// SSH runs the OpenSSH client that ships with Windows in its own console window; web connections open in
// the default browser. Values reach these functions only after normalizeConnection has validated them,
// and every value is checked again here before it is put on a command line (defense in depth).

const fs = require('node:fs');
const path = require('node:path');
const { webUrl, isHost, isSshUser } = require('../renderer/js/targets.js');

const SYSTEM32 = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32');
const SSH_EXE = path.join(SYSTEM32, 'OpenSSH', 'ssh.exe');
const CMD_EXE = path.join(SYSTEM32, 'cmd.exe');

// Characters that cmd.exe or ssh would treat specially. None of them can pass the field validation;
// this check makes sure that stays true even if validation changes.
const UNSAFE = /["%&|<>^!()\u0000-\u001f\u007f]/;

function sshAvailable() {
  return fs.existsSync(SSH_EXE);
}

/** ssh.exe arguments: options first, then "--" so the host can never be read as an option. */
function sshArgs(conn, policy = {}) {
  if (!isHost(conn.host)) throw new Error('The computer name is not valid.');
  if (conn.username && !isSshUser(conn.username)) throw new Error('The username is not valid for SSH.');
  const args = [];
  if (conn.username) args.push('-l', conn.username);
  args.push('-p', String(conn.port || 22));
  const o = conn.ssh || {};
  if (o.identityFile) {
    if (!fs.existsSync(o.identityFile)) throw new Error(`The key file ${o.identityFile} was not found.`);
    args.push('-i', o.identityFile, '-o', 'IdentitiesOnly=yes');
  }
  if (o.jumpHost) args.push('-J', o.jumpHost);
  const strict = policy.ssh && policy.ssh.strictHostKeyChecking;
  if (strict && strict !== 'ask') args.push('-o', `StrictHostKeyChecking=${strict}`);
  args.push('--', conn.host);
  for (const a of args) if (UNSAFE.test(a)) throw new Error('A connection value contains characters that are not allowed.');
  return args;
}

/**
 * The full command line. ssh runs inside cmd.exe only so that the window stays open after a failed
 * connection (exit code 255) and the user can read ssh's message. cmd runs without AutoRun scripts (/d)
 * and without delayed expansion (/v:off); no value contains a character that cmd interprets.
 */
function sshCommandLine(conn, policy) {
  const args = sshArgs(conn, policy).map((a) => (/\s/.test(a) ? `"${a}"` : a)).join(' ');
  const onError = 'if errorlevel 255 (echo. & echo The SSH connection ended with an error. Read the message above. & pause & exit /b 255)';
  return { exe: CMD_EXE, commandLine: `"${CMD_EXE}" /d /v:off /s /c ""${SSH_EXE}" ${args} & ${onError}"` };
}

/** The URL for a web connection, limited to http and https. */
function webTarget(conn) {
  const url = new URL(webUrl(conn));
  if (!['http:', 'https:'].includes(url.protocol)) throw new Error('Only http and https addresses can be opened.');
  if (url.username || url.password) throw new Error('The web address must not contain a username or password.');
  return url.href;
}

module.exports = { SSH_EXE, sshAvailable, sshArgs, sshCommandLine, webTarget };
