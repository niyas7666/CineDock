const { ipcRenderer } = require('electron');

const mediaPattern = /\.(mkv|mp4|avi|mov|webm|m4v|ts)(?:$|[?#])/i;

document.addEventListener('click', (event) => {
  const anchor = event.target.closest?.('a[href]');
  if (!anchor || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;

  const url = new URL(anchor.href, window.location.href).href;
  if (!mediaPattern.test(url)) return;

  event.preventDefault();
  event.stopImmediatePropagation();
  ipcRenderer.sendToHost('open-movie', url);
}, true);
