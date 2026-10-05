'use strict';
// Direct Win32 calls via koffi (prebuilt N-API, no compiler, works under PowerShell Constrained Language Mode).
// - Credential Manager: generic credentials "TERMSRV/<host>", which mstsc uses and Credential Guard allows.
// - Window focus for mstsc session windows.

const koffi = require('koffi');

const CRED_TYPE_GENERIC = 1;
const CRED_PERSIST_LOCAL_MACHINE = 2;
const ERROR_NOT_FOUND = 1168;
const SW_RESTORE = 9;

const advapi32 = koffi.load('advapi32.dll');
const user32 = koffi.load('user32.dll');
const kernel32 = koffi.load('kernel32.dll');

const FILETIME = koffi.struct('BP_FILETIME', { dwLowDateTime: 'uint32', dwHighDateTime: 'uint32' });
const CREDENTIALW = koffi.struct('BP_CREDENTIALW', {
  Flags: 'uint32',
  Type: 'uint32',
  TargetName: 'str16',
  Comment: 'str16',
  LastWritten: FILETIME,
  CredentialBlobSize: 'uint32',
  CredentialBlob: 'void *',
  Persist: 'uint32',
  AttributeCount: 'uint32',
  Attributes: 'void *',
  TargetAlias: 'str16',
  UserName: 'str16',
});

const CredWriteW = advapi32.func('bool __stdcall CredWriteW(const BP_CREDENTIALW *Credential, uint32 Flags)');
const CredReadW = advapi32.func('bool __stdcall CredReadW(str16 TargetName, uint32 Type, uint32 Flags, _Out_ void **Credential)');
const CredDeleteW = advapi32.func('bool __stdcall CredDeleteW(str16 TargetName, uint32 Type, uint32 Flags)');
const CredFree = advapi32.func('void __stdcall CredFree(void *Buffer)');
const GetLastError = kernel32.func('uint32 __stdcall GetLastError()');

const EnumWindowsProc = koffi.proto('bool __stdcall BP_EnumWindowsProc(void *hwnd, intptr lParam)');
const EnumWindows = user32.func('bool __stdcall EnumWindows(BP_EnumWindowsProc *cb, intptr lParam)');
const GetWindowThreadProcessId = user32.func('uint32 __stdcall GetWindowThreadProcessId(void *hwnd, _Out_ uint32 *pid)');
const IsWindowVisible = user32.func('bool __stdcall IsWindowVisible(void *hwnd)');
const IsIconic = user32.func('bool __stdcall IsIconic(void *hwnd)');
const GetWindowTextLengthW = user32.func('int __stdcall GetWindowTextLengthW(void *hwnd)');
const ShowWindow = user32.func('bool __stdcall ShowWindow(void *hwnd, int cmd)');
const SetForegroundWindow = user32.func('bool __stdcall SetForegroundWindow(void *hwnd)');

function targetFor(host) {
  return `TERMSRV/${String(host).trim()}`;
}

function getCredential(host) {
  const out = [null];
  if (!CredReadW(targetFor(host), CRED_TYPE_GENERIC, 0, out)) {
    const err = GetLastError();
    if (err === ERROR_NOT_FOUND) return { saved: false };
    throw new Error(`Windows Credential Manager could not be read (error ${err}).`);
  }
  try {
    const cred = koffi.decode(out[0], CREDENTIALW);
    return { saved: true, username: cred.UserName || '' };
  } finally {
    CredFree(out[0]);
  }
}

function saveCredential(host, username, password) {
  if (!username || !password) throw new Error('Username and password are required.');
  const blob = Buffer.from(String(password), 'utf16le');
  try {
    const ok = CredWriteW({
      Flags: 0,
      Type: CRED_TYPE_GENERIC,
      TargetName: targetFor(host),
      Comment: 'Saved by BearingPoint Remote Desktop',
      LastWritten: { dwLowDateTime: 0, dwHighDateTime: 0 },
      CredentialBlobSize: blob.length,
      CredentialBlob: blob,
      Persist: CRED_PERSIST_LOCAL_MACHINE,
      AttributeCount: 0,
      Attributes: null,
      TargetAlias: null,
      UserName: String(username),
    }, 0);
    if (!ok) throw new Error(`The password could not be saved (Windows error ${GetLastError()}). Saving passwords may be blocked by policy.`);
  } finally {
    blob.fill(0);
  }
}

function deleteCredential(host) {
  if (CredDeleteW(targetFor(host), CRED_TYPE_GENERIC, 0)) return true;
  if (GetLastError() === ERROR_NOT_FOUND) return false;
  throw new Error('The saved password could not be removed.');
}

/** Top-level visible windows of a process, largest title first (the session window has the host in its title). */
function windowsOfProcess(pid) {
  const found = [];
  EnumWindows((hwnd) => {
    const owner = [0];
    GetWindowThreadProcessId(hwnd, owner);
    if (owner[0] === pid && IsWindowVisible(hwnd)) found.push({ hwnd, titleLength: GetWindowTextLengthW(hwnd) });
    return true;
  }, 0);
  return found.sort((a, b) => b.titleLength - a.titleLength);
}

/** Bring the mstsc window of a session to the foreground. Works because the app itself is in the foreground when the user clicks. */
function focusProcessWindow(pid) {
  const [main] = windowsOfProcess(pid);
  if (!main) throw new Error('The session window is not open yet.');
  if (IsIconic(main.hwnd)) ShowWindow(main.hwnd, SW_RESTORE);
  return SetForegroundWindow(main.hwnd);
}

// ── Event log (wevtapi) ──────────────────────────────
// Reading the RDP client log in-process avoids starting PowerShell every few seconds,
// which endpoint protection products flag and which costs CPU.
const wevtapi = koffi.load('wevtapi.dll');
const EvtQuery = wevtapi.func('void * __stdcall EvtQuery(void *Session, str16 Path, str16 Query, uint32 Flags)');
const EvtNext = wevtapi.func('bool __stdcall EvtNext(void *ResultSet, uint32 EventsSize, _Out_ void **Events, uint32 Timeout, uint32 Flags, _Out_ uint32 *Returned)');
const EvtRender = wevtapi.func('bool __stdcall EvtRender(void *Context, void *Fragment, uint32 Flags, uint32 BufferSize, _Out_ uint8_t *Buffer, _Out_ uint32 *BufferUsed, _Out_ uint32 *PropertyCount)');
const EvtClose = wevtapi.func('bool __stdcall EvtClose(void *Object)');

const RDP_CLIENT_LOG = 'Microsoft-Windows-TerminalServices-RDPClient/Operational';
const EVT_QUERY_CHANNEL_PATH = 0x1;
const EVT_QUERY_FORWARD = 0x100;
const EVT_RENDER_EVENT_XML = 1;
const ERROR_NO_MORE_ITEMS = 259;
const ERROR_INSUFFICIENT_BUFFER = 122;

/**
 * RDP client events 1024/1026/1027 written by the given mstsc processes since a point in time.
 * Returns [{ id, pid, time, props }] in chronological order. Throws if the log cannot be read.
 */
function queryRdpEvents(sinceIso, pids) {
  const ids = (pids || []).map(Number).filter((p) => Number.isInteger(p) && p > 0);
  if (!ids.length) return [];
  const since = new Date(sinceIso);
  if (Number.isNaN(since.getTime())) throw new Error('Invalid start time.');
  const xpath = `*[System[(EventID=1024 or EventID=1026 or EventID=1027)`
    + ` and TimeCreated[@SystemTime>='${new Date(since.getTime() - 2000).toISOString()}']`
    + ` and (${ids.map((p) => `Execution[@ProcessID=${p}]`).join(' or ')})]]`;
  const query = EvtQuery(null, RDP_CLIENT_LOG, xpath, EVT_QUERY_CHANNEL_PATH | EVT_QUERY_FORWARD);
  if (!query) throw new Error(`The Remote Desktop client log could not be read (error ${GetLastError()}).`);
  const events = [];
  try {
    for (;;) {
      const handles = [null];
      const returned = [0];
      if (!EvtNext(query, 1, handles, 0, 0, returned)) {
        const err = GetLastError();
        if (err === ERROR_NO_MORE_ITEMS) break;
        throw new Error(`Reading the Remote Desktop client log failed (error ${err}).`);
      }
      try {
        events.push(parseEventXml(renderXml(handles[0])));
      } finally {
        EvtClose(handles[0]);
      }
    }
  } finally {
    EvtClose(query);
  }
  return events;
}

function renderXml(handle) {
  const used = [0];
  const count = [0];
  if (!EvtRender(null, handle, EVT_RENDER_EVENT_XML, 0, null, used, count) && GetLastError() !== ERROR_INSUFFICIENT_BUFFER) {
    throw new Error('An event could not be read.');
  }
  const buf = Buffer.alloc(used[0]);
  if (!EvtRender(null, handle, EVT_RENDER_EVENT_XML, buf.length, buf, used, count)) throw new Error('An event could not be read.');
  return buf.toString('utf16le').replace(/\u0000+$/, '');
}

function decodeXml(s) {
  return s.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, "'").replace(/&amp;/g, '&');
}

/** Extract the fields the session tracker needs from an event's XML rendering. */
function parseEventXml(xml) {
  const id = Number((/<EventID[^>]*>(\d+)<\/EventID>/.exec(xml) || [])[1]);
  const time = (/<TimeCreated SystemTime=['"]([^'"]+)['"]/.exec(xml) || [])[1] || '';
  const pid = Number((/<Execution ProcessID=['"](\d+)['"]/.exec(xml) || [])[1]);
  const props = [...xml.matchAll(/<Data(?:\s[^>]*)?>([\s\S]*?)<\/Data>|<Data(?:\s[^>]*)?\/>/g)].map((m) => decodeXml(m[1] || ''));
  return { id, pid, time: time ? new Date(time).toISOString() : '', props };
}

// ── File owner check (policy file) ────────────────────
const SE_FILE_OBJECT = 1;
const OWNER_SECURITY_INFORMATION = 0x1;
const WinBuiltinAdministratorsSid = 26;
const WinLocalSystemSid = 22;
const GetNamedSecurityInfoW = advapi32.func('uint32 __stdcall GetNamedSecurityInfoW(str16 ObjectName, int ObjectType, uint32 SecurityInfo, _Out_ void **ppsidOwner, _Out_ void **ppsidGroup, _Out_ void **ppDacl, _Out_ void **ppSacl, _Out_ void **ppSecurityDescriptor)');
const IsWellKnownSid = advapi32.func('bool __stdcall IsWellKnownSid(void *pSid, int WellKnownSidType)');
const LocalFree = kernel32.func('void * __stdcall LocalFree(void *hMem)');

/** True when the file is owned by BUILTIN\Administrators or SYSTEM, i.e. it was not planted by a standard user. */
function isOwnedByAdministrators(file) {
  const owner = [null];
  const sd = [null];
  const rc = GetNamedSecurityInfoW(file, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, owner, [null], [null], [null], sd);
  if (rc !== 0) return false;
  try {
    return IsWellKnownSid(owner[0], WinBuiltinAdministratorsSid) || IsWellKnownSid(owner[0], WinLocalSystemSid);
  } finally {
    LocalFree(sd[0]);
  }
}

module.exports = {
  getCredential, saveCredential, deleteCredential, focusProcessWindow, windowsOfProcess,
  queryRdpEvents, parseEventXml, isOwnedByAdministrators,
};
