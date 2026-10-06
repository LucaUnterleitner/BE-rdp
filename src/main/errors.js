'use strict';
// User-facing explanations for RDP disconnect reasons and pre-connect probe results.
// Each entry says what happened, why it matters and what to do next.

const DISCONNECT_REASONS = {
  0x1: { kind: 'normal', title: 'Session closed', message: 'You closed the connection. The remote session may still be running on the server.' },
  0x2: { kind: 'normal', title: 'Disconnected remotely', message: 'The session was disconnected by the remote side or by another sign-in. Reconnect to continue where you left off.', retry: true },
  0x3: { kind: 'error', title: 'The remote computer ended the session', message: 'An administrator signed you out, a time limit was reached, or the server restarted. Reconnect. If this keeps happening, contact the system owner.', retry: true },
  0x104: dns(), 0x208: dns(), 0x508: dns(), 0x604: dns(),
  0x204: { kind: 'error', title: 'The remote computer could not be reached', message: 'Check that the system is switched on and that you are connected to the BearingPoint network or VPN. Remote Desktop may be disabled or a firewall may block port 3389.', retry: true },
  0x108: timeout(), 0x704: timeout(),
  0x904: { kind: 'error', title: 'The connection was closed unexpectedly', message: 'The network connection dropped, for example because the VPN reconnected. Reconnecting is safe. If it happens repeatedly, try the RD Gateway or contact IT.', retry: true },
  0x406: secure(), 0x606: secure(), 0x706: secure(), 0x906: secure(), 0xA06: secure(), 0xB06: secure(), 0xC06: secure(), 0xC08: secure(),
  0x507: { kind: 'error', title: 'Sign-in failed', message: 'Retry with your username and password, or check that your smart card is inserted.', retry: true },
  0x807: { kind: 'error', title: 'Wrong username or password', message: 'Check your username (DOMAIN\\username) and password. If you saved a password, it may be outdated. Remove it in the system details and try again.', retry: true },
  0xA07: { kind: 'error', title: 'Account not found', message: 'The user account does not exist on the remote system or domain. Check the username.' },
  0xB07: { kind: 'error', title: 'Account disabled', message: 'Your account is disabled. Contact the IT Service Desk.' },
  0xD07: { kind: 'error', title: 'Account locked', message: 'Your account is locked after too many failed sign-ins. Wait or contact the IT Service Desk. Retrying now may extend the lock.' },
  0xE07: { kind: 'error', title: 'Account expired', message: 'Your account has expired. Contact the IT Service Desk.' },
  0xF07: pwdExpired(), 0x1207: pwdExpired(),
  0x1807: { kind: 'error', title: 'Domain controller not reachable', message: 'Your sign-in could not be verified. Connect to the VPN and check the domain part of your username.', retry: true },
  0x1607: delegation(), 0x1707: delegation(),
  0x2107: { kind: 'error', title: 'Fresh credentials required', message: 'The server does not accept saved passwords. Type your password when Windows asks for it.', retry: true },
  0x1B07: { kind: 'error', title: 'Server certificate expired', message: 'The identity of the remote system cannot be confirmed. Do not continue. Report this to the system owner.' },
  0x1C07: { kind: 'error', title: 'Wrong smart card PIN', message: 'Check your PIN and try again. Several wrong attempts block the card.', retry: true },
  0x2207: { kind: 'error', title: 'Smart card blocked', message: 'Your smart card is blocked. Contact the IT Service Desk.' },
  0x808: lic(), 0x908: lic(),
  0x112F: { kind: 'error', title: 'Protocol error', message: 'The server may be low on memory or graphics resources. Try a lower resolution or a single monitor. If it persists, ask the system owner to check the server.', retry: true },
};

function dns() { return { kind: 'error', title: 'Computer name not found', message: 'The name could not be resolved. Check the spelling, use the full name (for example host.domain.local) or connect to the VPN.', retry: true }; }
function timeout() { return { kind: 'error', title: 'The connection timed out', message: 'The network may be slow or a firewall is dropping traffic. Try again, or connect via the RD Gateway.', retry: true }; }
function secure() { return { kind: 'error', title: 'A secure connection could not be established', message: 'Encryption or certificate negotiation failed. Both computers may need current Windows updates. Contact the system owner.' }; }
function pwdExpired() { return { kind: 'error', title: 'Password expired', message: 'Your password must be changed before you can sign in remotely. Change it on your BearingPoint laptop (Ctrl+Alt+Del, Change a password), then try again.' }; }
function delegation() { return { kind: 'error', title: 'Saved credentials not allowed', message: 'Policy does not allow saved or default credentials for this server. Use the full computer name so Kerberos is used, or type your credentials when asked.', retry: true }; }
function lic() { return { kind: 'error', title: 'No Remote Desktop licenses available', message: 'The server has no free Remote Desktop licenses. Contact IT.' }; }

function hex(code) {
  return '0x' + Number(code).toString(16).toUpperCase();
}

function explainDisconnect(code) {
  if (code === null || code === undefined) return null;
  const entry = DISCONNECT_REASONS[code];
  if (entry) return { ...entry, code: hex(code) };
  return { kind: 'error', title: 'Connection could not be established', message: 'The Remote Desktop client reported an error that this app does not know yet. Try again. If it repeats, send the technical code to IT.', code: hex(code), retry: true };
}

const PROBE_RESULTS = {
  dns: { title: 'Computer name not found', message: 'The name could not be resolved. Check the spelling or connect to the VPN.' },
  refused: { title: 'Remote Desktop is not accepting connections', message: 'The computer answered, but Remote Desktop is disabled or uses a different port.' },
  timeout: { title: 'The remote computer could not be reached', message: 'No answer within 3 seconds. Check that the system is online and that the VPN is active. A firewall may block port 3389.' },
  unreachable: { title: 'Network unreachable', message: 'Your computer has no route to this system. Check your network or VPN connection.' },
  error: { title: 'Availability could not be checked', message: 'The check failed for an unexpected reason.' },
};

function explainProbe(reason) {
  return PROBE_RESULTS[reason] || PROBE_RESULTS.error;
}

function allReasons() {
  return Object.entries(DISCONNECT_REASONS)
    .filter(([, v]) => v.kind === 'error')
    .map(([code, v]) => ({ code: hex(code), title: v.title, message: v.message }));
}

/** True when the app has its own explanation for this disconnect reason. */
function isKnownReason(code) {
  return Object.prototype.hasOwnProperty.call(DISCONNECT_REASONS, code);
}

module.exports = { explainDisconnect, explainProbe, allReasons, hex, isKnownReason };
