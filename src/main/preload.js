'use strict';
// Exposes a narrow, typed API to the renderer. The renderer has no Node.js access.

const { contextBridge, ipcRenderer } = require('electron');

const invoke = (channel) => (...args) => ipcRenderer.invoke(channel, ...args);

contextBridge.exposeInMainWorld('rdp', {
  appInfo: invoke('app:info'),
  appReady: invoke('app:ready'),
  openDataFolder: invoke('app:openDataFolder'),
  openHelp: invoke('app:openHelp'),
  connections: {
    list: invoke('connections:list'),
    acknowledgeCorrupt: invoke('connections:acknowledgeCorrupt'),
    refreshCentral: invoke('connections:refreshCentral'),
    save: invoke('connections:save'),
    remove: invoke('connections:delete'),
    toggleFavorite: invoke('connections:favorite'),
    importRdp: invoke('connections:import'),
    confirmImport: invoke('connections:confirmImport'),
    exportRdp: invoke('connections:export'),
    loadSamples: invoke('connections:samples'),
  },
  settings: {
    get: invoke('settings:get'),
    save: invoke('settings:save'),
  },
  status: {
    probe: invoke('status:probe'),
    probeAll: invoke('status:probeAll'),
  },
  credentials: {
    // All calls take the connection id; the main process resolves the host.
    get: invoke('credentials:get'),
    save: invoke('credentials:save'),
    remove: invoke('credentials:delete'),
  },
  sessions: {
    list: invoke('sessions:list'),
    connect: invoke('sessions:connect'),
    quickTarget: invoke('sessions:quickTarget'),
    focus: invoke('sessions:focus'),
    disconnect: invoke('sessions:disconnect'),
    clearEnded: invoke('sessions:clearEnded'),
  },
  audit: { list: invoke('audit:list') },
  help: { errorCodes: invoke('help:errorCodes') },
  on(channel, handler) {
    const allowed = ['session:update', 'connect:progress', 'status:update', 'app:connect-request', 'connections:changed'];
    if (!allowed.includes(channel)) throw new Error(`Channel not allowed: ${channel}`);
    const listener = (_event, payload) => handler(payload);
    ipcRenderer.on(channel, listener);
    return () => ipcRenderer.removeListener(channel, listener);
  },
});
