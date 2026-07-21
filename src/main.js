const { app, BrowserWindow, ipcMain, session, screen, Menu, globalShortcut, nativeImage } = require('electron');
const crypto = require('node:crypto');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const { spawn } = require('node:child_process');

const MEDIA_URL = /\.(mkv|mp4|avi|mov|webm|m4v|ts)(?:$|[?#])/i;
const isPackaged = app.isPackaged;
const VlcHostPath = isPackaged
  ? path.join(process.resourcesPath, 'VlcHost', 'VlcHost.exe')
  : path.join(__dirname, '..', 'native', 'VlcHost', 'bin', 'Debug', 'net10.0-windows', 'VlcHost.exe');
const VlcDirectory = isPackaged
  ? path.join(process.resourcesPath, 'vlc')
  : path.join(process.env.ProgramFiles || 'C:\\Program Files', 'VideoLAN', 'VLC');
const browserPreload = path.join(__dirname, 'browser-preload.js');
const AppIcon = nativeImage.createFromDataURL(`data:image/svg+xml;base64,${fs.readFileSync(path.join(__dirname, 'assets', 'cinedock.svg')).toString('base64')}`);
app.setName('CineDock');
process.title = 'CineDock';
let mainWindow;
let splashWindow;
let splashOpenedAt = 0;
let fullscreenRequested = false;
let playbackActive = false;

class VlcHost {
  constructor(parentWindow) {
    this.parentWindow = parentWindow;
    this.socket = null;
    this.buffer = '';
    this.starting = null;
    this.process = null;
  }

  async ensureRunning() {
    if (this.socket?.writable) return;
    if (this.starting) return this.starting;
    this.starting = this.start();
    try { await this.starting; } finally { this.starting = null; }
  }

  async start() {
    if (!fs.existsSync(VlcHostPath)) {
      throw new Error(isPackaged ? 'The bundled native VLC host is missing. Reinstall CineDock.' : 'Native VLC host is missing. Run npm run build:native first.');
    }
    if (!fs.existsSync(path.join(VlcDirectory, 'libvlc.dll'))) {
      throw new Error(isPackaged ? 'The bundled VLC runtime is missing. Reinstall CineDock.' : 'VLC 64-bit was not found in Program Files. Install VLC, then restart the app.');
    }
    const hwnd = this.parentWindow.getNativeWindowHandle().readBigUInt64LE(0).toString(16);
    const pipe = `vlc-movie-browser-${process.pid}-${crypto.randomUUID()}`;
    this.process = spawn(VlcHostPath, ['--parent', hwnd, '--pipe', pipe, '--vlc-dir', VlcDirectory], { windowsHide: true });
    this.process.on('exit', (code) => { this.socket?.destroy(); this.socket = null; this.process = null; this.emit({ type: 'error', error: `Native VLC host closed (${code ?? 'unknown'}).` }); });
    this.process.on('error', (error) => this.emit({ type: 'error', error: error.message }));
    await this.connect(`\\\\.\\pipe\\${pipe}`);
  }

  connect(pipePath) {
    return new Promise((resolve, reject) => {
      let attempts = 0;
      const attempt = () => {
        const socket = net.createConnection(pipePath);
        socket.once('connect', () => { this.socket = socket; this.attachSocket(socket); resolve(); });
        socket.once('error', (error) => {
          socket.destroy();
          if (++attempts < 50) setTimeout(attempt, 100); else reject(new Error(`Could not connect to native VLC host: ${error.message}`));
        });
      };
      attempt();
    });
  }

  attachSocket(socket) {
    socket.on('data', (chunk) => {
      this.buffer += chunk.toString('utf8');
      let newline;
      while ((newline = this.buffer.indexOf('\n')) >= 0) {
        const line = this.buffer.slice(0, newline);
        this.buffer = this.buffer.slice(newline + 1);
        if (!line) continue;
        try { this.emit(JSON.parse(line)); } catch { this.emit({ type: 'error', error: 'Invalid response from native VLC host.' }); }
      }
    });
    socket.on('error', (error) => this.emit({ type: 'error', error: error.message }));
    socket.on('close', () => { if (this.socket === socket) this.socket = null; });
  }

  send(message) {
    if (!this.socket?.writable) throw new Error('Native VLC host is not connected.');
    this.socket.write(`${JSON.stringify(message)}\n`);
  }

  emit(message) {
    if (!mainWindow?.isDestroyed()) mainWindow.webContents.send('player:event', message);
  }

  stop() { if (this.socket?.writable) this.send({ action: 'stop' }); }
  close() {
    try { if (this.socket?.writable) this.send({ action: 'quit' }); } catch { /* process is already gone */ }
    this.socket?.destroy();
    this.process?.kill();
  }
}

let player;
let shortcutsRegistered = false;
const playbackShortcuts = new Map([
  ['Space', 'play-pause'], ['S', 'stop'], ['F', 'fullscreen'], ['Escape', 'escape'],
  ['Left', 'seek:-10'], ['Right', 'seek:10'],
  ['Shift+Left', 'seek:-3'], ['Shift+Right', 'seek:3'],
  ['Alt+Left', 'seek:-10'], ['Alt+Right', 'seek:10'],
  ['CommandOrControl+Left', 'seek:-60'], ['CommandOrControl+Right', 'seek:60'],
  ['CommandOrControl+Alt+Left', 'seek:-300'], ['CommandOrControl+Alt+Right', 'seek:300'],
  ['Up', 'volume:5'], ['Down', 'volume:-5'], ['M', 'mute'],
  ['B', 'cycle-audio'], ['V', 'cycle-subtitle'], ['E', 'next-frame'],
  [']', 'rate:0.25'], ['[', 'rate:-0.25'], ['Shift+=', 'rate:0.25'], ['-', 'rate:-0.25'], ['=', 'rate-normal'],
  ['G', 'subtitle-delay:-50000'], ['H', 'subtitle-delay:50000'],
  ['J', 'audio-delay:-50000'], ['K', 'audio-delay:50000']
]);

function registerPlaybackShortcuts() {
  if (shortcutsRegistered) return;
  for (const [accelerator, action] of playbackShortcuts) {
    try {
      globalShortcut.register(accelerator, () => {
        if (mainWindow && !mainWindow.isDestroyed() && mainWindow.isFocused()) {
          mainWindow.webContents.send('shortcut:event', action);
        }
      });
    } catch { /* An unavailable accelerator must not break playback. */ }
  }
  shortcutsRegistered = true;
}

function unregisterPlaybackShortcuts() {
  if (!shortcutsRegistered) return;
  for (const accelerator of playbackShortcuts.keys()) globalShortcut.unregister(accelerator);
  shortcutsRegistered = false;
}

function validMediaUrl(value) {
  try {
    const url = new URL(value);
    return ['http:', 'https:'].includes(url.protocol) && MEDIA_URL.test(url.href);
  } catch { return false; }
}

function createSplashWindow() {
  splashOpenedAt = Date.now();
  splashWindow = new BrowserWindow({
    width: 430, height: 280, resizable: false, frame: false, show: false,
    backgroundColor: '#16191f', icon: AppIcon, alwaysOnTop: true,
    webPreferences: { contextIsolation: true, nodeIntegration: false }
  });
  splashWindow.once('ready-to-show', () => splashWindow?.show());
  splashWindow.loadFile(path.join(__dirname, 'splash.html'));
}

function revealMainWindow() {
  const delay = Math.max(0, 850 - (Date.now() - splashOpenedAt));
  setTimeout(() => {
    if (mainWindow && !mainWindow.isDestroyed()) mainWindow.show();
    if (splashWindow && !splashWindow.isDestroyed()) splashWindow.close();
    splashWindow = null;
  }, delay);
}

function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1400, height: 900, minWidth: 900, minHeight: 600,
    title: 'CineDock', icon: AppIcon,
    frame: false, backgroundColor: '#16181d', show: false,
    webPreferences: { preload: path.join(__dirname, 'preload.js'), contextIsolation: true, nodeIntegration: false, webviewTag: true }
  });
  mainWindow.once('ready-to-show', revealMainWindow);
  mainWindow.webContents.on('will-attach-webview', (event, webPreferences, params) => {
    if (!params.src.startsWith('http://172.16.50.7/')) { event.preventDefault(); return; }
    webPreferences.preload = browserPreload;
    webPreferences.nodeIntegration = false;
    webPreferences.contextIsolation = true;
    webPreferences.sandbox = false;
  });
  mainWindow.loadFile(path.join(__dirname, 'index.html'));
  mainWindow.on('closed', () => { player?.close(); mainWindow = null; });
  const notifyWindowState = () => mainWindow.webContents.send('window:state', {
    maximized: mainWindow.isMaximized(), fullscreen: fullscreenRequested
  });
  // Windows emits maximize events while moving into fullscreen. Those events must
  // not overwrite the requested fullscreen state before enter-full-screen fires.
  mainWindow.on('maximize', notifyWindowState);
  mainWindow.on('unmaximize', notifyWindowState);
  mainWindow.on('enter-full-screen', () => { fullscreenRequested = true; notifyWindowState(); });
  mainWindow.on('leave-full-screen', () => { fullscreenRequested = false; notifyWindowState(); });
  // Media keys are only bound while our window is focused. Otherwise globalShortcut
  // would swallow keys like E, S, F system-wide from every other application.
  mainWindow.on('focus', () => { if (playbackActive) registerPlaybackShortcuts(); });
  mainWindow.on('blur', unregisterPlaybackShortcuts);
  player = new VlcHost(mainWindow);
}

ipcMain.handle('player:open', async (_event, url) => {
  if (!validMediaUrl(url)) throw new Error('Only direct HTTP(S) movie links are supported.');
  await player.ensureRunning();
  player.send({ action: 'load', url });
  playbackActive = true;
  if (mainWindow?.isFocused()) registerPlaybackShortcuts();
  return { ok: true };
});

ipcMain.handle('player:command', async (_event, command) => {
  const allowed = new Set([
    'layout', 'play-pause', 'seek', 'seek-relative', 'volume', 'mute', 'rate',
    'cycle-audio', 'cycle-subtitle', 'set-audio', 'set-subtitle', 'next-frame',
    'subtitle-delay', 'audio-delay', 'adjust', 'reset-adjustments', 'stop', 'status'
  ]);
  if (!command || !allowed.has(command.command)) throw new Error('Unsupported player command.');
  await player.ensureRunning();
  const { command: action, ...payload } = command;
  player.send({ action, ...payload });
  if (action === 'stop') { playbackActive = false; unregisterPlaybackShortcuts(); }
  return { ok: true };
});

ipcMain.handle('window:fullscreen', () => {
  fullscreenRequested = !fullscreenRequested;
  mainWindow.setFullScreen(fullscreenRequested);
  return fullscreenRequested;
});

ipcMain.handle('window:control', (_event, action) => {
  if (!mainWindow) return false;
  switch (action) {
    case 'minimize': mainWindow.minimize(); break;
    case 'maximize': mainWindow.isMaximized() ? mainWindow.unmaximize() : mainWindow.maximize(); break;
    case 'close': mainWindow.close(); break;
  }
  return mainWindow.isMaximized();
});

ipcMain.handle('window:query', () => ({
  maximized: mainWindow?.isMaximized() ?? false,
  fullscreen: fullscreenRequested
}));

// Global cursor position lets the renderer reveal auto-hidden controls even while
// the mouse is over the native VLC child window (which never emits DOM events).
ipcMain.handle('screen:cursor', () => {
  if (!mainWindow) return null;
  const point = screen.getCursorScreenPoint();
  const bounds = mainWindow.getContentBounds();
  return {
    x: point.x - bounds.x, y: point.y - bounds.y,
    width: bounds.width, height: bounds.height,
    inside: point.x >= bounds.x && point.x <= bounds.x + bounds.width &&
            point.y >= bounds.y && point.y <= bounds.y + bounds.height
  };
});

app.whenReady().then(() => {
  Menu.setApplicationMenu(null);
  session.defaultSession.on('will-download', (event, item) => {
    if (!validMediaUrl(item.getURL())) return;
    event.preventDefault();
    mainWindow?.webContents.send('player:open-url', item.getURL());
  });
  createSplashWindow();
  createWindow();
  app.on('activate', () => { if (BrowserWindow.getAllWindows().length === 0) createWindow(); });
});

app.on('window-all-closed', () => { if (process.platform !== 'darwin') app.quit(); });
app.on('before-quit', () => player?.close());
app.on('will-quit', () => globalShortcut.unregisterAll());
