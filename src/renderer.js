const $ = (selector) => document.querySelector(selector);
const $$ = (selector) => [...document.querySelectorAll(selector)];
const HOME_URL = 'http://172.16.50.7/DHAKA-FLIX-7/';
const body = document.body;
const browser = $('#browser');
const stage = $('#videoStage');
const dock = $('#playerDock');
const timeline = $('#timeline');
let playerOpen = false;
let playerState = null;
let seeking = false;
let hideDockTimer;
let toastTimer;

function formatTime(milliseconds) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  const hours = Math.floor(seconds / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  const remainder = seconds % 60;
  return hours ? `${hours}:${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}` : `${String(minutes).padStart(2, '0')}:${String(remainder).padStart(2, '0')}`;
}

function send(command, payload = {}) {
  return window.movieApp.command(command, payload).catch((error) => toast(error.message));
}

function toast(message) {
  const element = $('#toast');
  element.textContent = message;
  element.classList.remove('hidden');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => element.classList.add('hidden'), 1250);
}

function setIcon(button, icon) {
  button.innerHTML = `<svg><use href="#${icon}"/></svg>`;
}

function showBrowser() {
  playerOpen = false;
  playerState = null;
  body.className = 'browser-mode';
  closeMenus();
}

function showPlayer() {
  playerOpen = true;
  body.className = 'player-mode';
}

function scheduleNativeLayout() {
  requestAnimationFrame(() => requestAnimationFrame(layoutNativeVideo));
  setTimeout(layoutNativeVideo, 60);
  setTimeout(layoutNativeVideo, 180);
}

function layoutNativeVideo() {
  if (!playerOpen) return;
  const stageRect = stage.getBoundingClientRect();
  const dockVisible = getComputedStyle(dock).display !== 'none' && dock.offsetHeight > 0;
  const dockRect = dockVisible ? dock.getBoundingClientRect() : null;
  // The VLC HWND always ends above the dock. Native windows cannot be layered
  // below/above Chromium reliably, so reserving this physical strip is required.
  const bottom = dockRect ? Math.min(stageRect.bottom, dockRect.top) : stageRect.bottom;
  const dpr = window.devicePixelRatio || 1;
  send('layout', {
    x: Math.round(stageRect.left * dpr), y: Math.round(stageRect.top * dpr),
    width: Math.max(1, Math.round(stageRect.width * dpr)),
    height: Math.max(1, Math.round((bottom - stageRect.top) * dpr))
  });
}

new ResizeObserver(scheduleNativeLayout).observe(stage);
new ResizeObserver(scheduleNativeLayout).observe(dock);
window.addEventListener('resize', scheduleNativeLayout);

async function openMovie(url) {
  try {
    showPlayer();
    const fileName = decodeURIComponent(url.split('/').pop() || 'Untitled media');
    $('#nowPlaying').textContent = fileName;
    $('#windowTitle').textContent = fileName;
    $('#mediaDetail').textContent = 'Loading via embedded VLC…';
    $('#loadingOverlay').classList.remove('hidden');
    await window.movieApp.openMovie(url);
    [0, 60, 220, 600, 1200].forEach((delay) => setTimeout(scheduleNativeLayout, delay));
    setTimeout(() => $('#loadingOverlay').classList.add('hidden'), 850);
  } catch (error) {
    $('#loadingOverlay').classList.add('hidden');
    toast(error.message);
    showBrowser();
  }
}

function closeMovie() {
  if (!playerOpen) return;
  playerOpen = false;
  clearTimeout(hideDockTimer);
  send('stop');
  showBrowser();
  $('#windowTitle').textContent = '';
}

$('#browserBack').addEventListener('click', () => browser.canGoBack() && browser.goBack());
$('#browserForward').addEventListener('click', () => browser.canGoForward() && browser.goForward());
$('#reload').addEventListener('click', () => browser.reload());
$('#home').addEventListener('click', () => browser.loadURL(HOME_URL));
$('#go').addEventListener('click', () => browser.loadURL($('#address').value));
$('#address').addEventListener('keydown', (event) => { if (event.key === 'Enter') browser.loadURL(event.target.value); });
browser.addEventListener('ipc-message', (event) => { if (event.channel === 'open-movie') openMovie(event.args[0]); });
browser.addEventListener('did-navigate', (event) => { $('#address').value = event.url; });
browser.addEventListener('did-navigate-in-page', (event) => { $('#address').value = event.url; });

$('#winMin').addEventListener('click', () => window.movieApp.windowControl('minimize'));
$('#winMax').addEventListener('click', () => window.movieApp.windowControl('maximize'));
$('#winClose').addEventListener('click', () => window.movieApp.windowControl('close'));

window.movieApp.onOpenMovie(openMovie);
window.movieApp.onWindowState(({ fullscreen }) => {
  const isFullscreenPlayer = fullscreen && playerOpen;
  body.classList.toggle('fullscreen', isFullscreenPlayer);
  body.classList.remove('dock-hidden');
  closeMenus();
  scheduleNativeLayout();
  if (isFullscreenPlayer) wakeDock();
});

$('#backButton').addEventListener('click', closeMovie);
$('#playPause').addEventListener('click', () => runAction('play-pause'));
$('#stopBtn').addEventListener('click', closeMovie);
$('#frameBtn').addEventListener('click', () => runAction('next-frame'));
$$('[data-seek]').forEach((button) => button.addEventListener('click', () => runAction(`seek:${button.dataset.seek}`)));
$('#mute').addEventListener('click', () => runAction('mute'));
$('#fullscreen').addEventListener('click', () => runAction('fullscreen'));
$('#volume').addEventListener('input', (event) => {
  const volume = Number(event.target.value);
  send('volume', { volume });
  $('#volLabel').textContent = `${volume}%`;
  toast(`Volume ${volume}%`);
});

timeline.addEventListener('input', (event) => {
  seeking = true;
  const fraction = Number(event.target.value) / 1000;
  $('#seekFill').style.width = `${fraction * 100}%`;
  $('#currentTime').textContent = formatTime(fraction * (playerState?.durationMs || 0));
});
timeline.addEventListener('change', (event) => {
  seeking = false;
  if (playerState?.durationMs) send('seek', { position: (Number(event.target.value) / 1000) * playerState.durationMs / 1000 });
});

function closeMenus() {
  $$('.popup').forEach((menu) => menu.classList.add('hidden'));
  $$('.compact-button').forEach((button) => button.classList.remove('active'));
  dock.classList.remove('dock-expanded');
  scheduleNativeLayout();
}

function openMenu(menuId, anchor) {
  const menu = $(`#${menuId}`);
  const wasOpen = !menu.classList.contains('hidden');
  closeMenus();
  if (wasOpen) return;

  // Every popup lives inside the dock tray. A native video HWND cannot be
  // covered by HTML, so no menu is allowed to float over the video region.
  if (menu.parentElement !== dock) dock.prepend(menu);
  menu.classList.add('dock-popup');
  dock.classList.add('dock-expanded');
  menu.classList.remove('hidden');
  anchor.classList.add('active');
  scheduleNativeLayout();
}

$$('[data-menu]').forEach((button) => button.addEventListener('click', (event) => {
  event.stopPropagation();
  openMenu(button.dataset.menu, button);
}));
document.addEventListener('click', (event) => { if (!event.target.closest('.popup') && !event.target.closest('[data-menu]')) closeMenus(); });

$('#speedMenu').addEventListener('click', (event) => {
  const button = event.target.closest('[data-rate]');
  if (!button) return;
  send('rate', { rate: Number(button.dataset.rate) });
  closeMenus();
});

$$('[data-adjust]').forEach((input) => input.addEventListener('input', () => {
  const value = Number(input.value);
  const property = input.dataset.adjust;
  $(`#${property}Out`).textContent = `${Math.round(value * 100)}%`;
  send('adjust', { property, value });
}));
$('#resetAdjust').addEventListener('click', () => {
  $$('[data-adjust]').forEach((input) => { input.value = '1'; $(`#${input.dataset.adjust}Out`).textContent = '100%'; });
  send('reset-adjustments');
  toast('Video adjustments reset');
});

function changeVolume(delta) {
  const next = Math.max(0, Math.min(200, (playerState?.volume ?? 100) + delta));
  send('volume', { volume: next });
  toast(`Volume ${next}%`);
}

function changeRate(delta) {
  const next = delta === 0 ? 1 : Math.max(.25, Math.min(4, +((playerState?.rate ?? 1) + delta).toFixed(2)));
  send('rate', { rate: next });
  toast(`Speed ${next.toFixed(2)}×`);
}

function runAction(action) {
  if (!playerOpen && action !== 'fullscreen') return;
  wakeDock();
  if (action === 'play-pause' || action === 'next-frame' || action === 'cycle-audio' || action === 'cycle-subtitle') return send(action);
  if (action === 'stop') return closeMovie();
  if (action === 'mute') return send('mute', { muted: !playerState?.muted });
  if (action === 'fullscreen') return window.movieApp.toggleFullscreen();
  if (action === 'escape') {
    if (body.classList.contains('fullscreen')) return window.movieApp.toggleFullscreen();
    return closeMovie();
  }
  if (action === 'rate-normal') return changeRate(0);
  if (action.startsWith('seek:')) return send('seek-relative', { seconds: Number(action.split(':')[1]) });
  if (action.startsWith('volume:')) return changeVolume(Number(action.split(':')[1]));
  if (action.startsWith('rate:')) return changeRate(Number(action.split(':')[1]));
  if (action.startsWith('subtitle-delay:')) return send('subtitle-delay', { deltaUs: Number(action.split(':')[1]) });
  if (action.startsWith('audio-delay:')) return send('audio-delay', { deltaUs: Number(action.split(':')[1]) });
}

// Shortcuts are delivered by the main process (focus-scoped globalShortcut) so
// they work even when the native VLC window holds keyboard focus. No DOM keydown
// handler here — that would double-fire and cannot see the native child anyway.
window.movieApp.onShortcut(runAction);

function updateState(next) {
  playerState = next;
  if (!seeking && next.durationMs > 0) {
    const fraction = Math.max(0, Math.min(1, next.positionMs / next.durationMs));
    timeline.value = String(Math.round(fraction * 1000));
    $('#seekFill').style.width = `${fraction * 100}%`;
  }
  $('#currentTime').textContent = formatTime(next.positionMs);
  $('#duration').textContent = formatTime(next.durationMs);
  $('#volume').value = String(Math.max(0, Math.min(200, next.volume)));
  $('#volLabel').textContent = `${next.volume}%`;
  setIcon($('#mute'), next.muted || next.volume === 0 ? 'i-muted' : 'i-volume');
  setIcon($('#playPause'), next.playing ? 'i-pause' : 'i-play');
  $('#speedBtn').textContent = `${next.rate.toFixed(2)}×`;
  const audio = next.audioTracks.find((track) => track.id === next.audioTrackId);
  const subtitle = next.subtitleTracks.find((track) => track.id === next.subtitleTrackId);
  const clean = (name) => (name || '').replace(/^Track\s*\d*\s*-?\s*/, '').trim();
  $('#audioName').textContent = clean(audio?.name) || 'Default';
  $('#subName').textContent = next.subtitleTrackId >= 0 && subtitle ? (clean(subtitle.name) || 'On') : 'Off';
  $('#mediaDetail').textContent = `${audio?.name || 'No audio'}  •  ${subtitle?.id >= 0 ? subtitle.name : 'Subtitles off'}  •  ${next.rate.toFixed(2)}×`;
}

window.movieApp.onPlayerEvent((event) => {
  if (event.type === 'toggle-fullscreen') {
    if (playerOpen) window.movieApp.toggleFullscreen();
    return;
  }
  if (event.type === 'error') { $('#loadingOverlay').classList.add('hidden'); toast(event.error); return; }
  if (event.type === 'state' && event.state) updateState(event.state);
});
setInterval(() => { if (playerOpen) send('status'); }, 500);

function wakeDock() {
  if (!body.classList.contains('fullscreen')) return;
  body.classList.remove('dock-hidden');
  clearTimeout(hideDockTimer);
  scheduleNativeLayout();
  hideDockTimer = setTimeout(() => {
    if (body.classList.contains('fullscreen')) {
      body.classList.add('dock-hidden');
      closeMenus();
      scheduleNativeLayout();
    }
  }, 2500);
}

let lastCursor = { x: -1, y: -1 };
setInterval(async () => {
  if (!body.classList.contains('fullscreen')) return;
  const cursor = await window.movieApp.cursor();
  if (!cursor?.inside) return;
  if (Math.abs(cursor.x - lastCursor.x) > 2 || Math.abs(cursor.y - lastCursor.y) > 2) {
    lastCursor = { x: cursor.x, y: cursor.y };
    wakeDock();
  }
  if (cursor.y > cursor.height - 90) wakeDock();
}, 250);

window.movieApp.queryWindow().then(({ fullscreen }) => {
  if (fullscreen && playerOpen) body.classList.add('fullscreen');
});
