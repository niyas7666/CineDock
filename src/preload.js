const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('movieApp', {
  openMovie: (url) => ipcRenderer.invoke('player:open', url),
  command: (command, payload = {}) => ipcRenderer.invoke('player:command', { command, ...payload }),
  toggleFullscreen: () => ipcRenderer.invoke('window:fullscreen'),
  windowControl: (action) => ipcRenderer.invoke('window:control', action),
  queryWindow: () => ipcRenderer.invoke('window:query'),
  cursor: () => ipcRenderer.invoke('screen:cursor'),
  onShortcut: (callback) => ipcRenderer.on('shortcut:event', (_event, action) => callback(action)),
  onWindowState: (callback) => ipcRenderer.on('window:state', (_event, data) => callback(data)),
  onPlayerEvent: (callback) => ipcRenderer.on('player:event', (_event, data) => callback(data)),
  onOpenMovie: (callback) => ipcRenderer.on('player:open-url', (_event, url) => callback(url))
});
