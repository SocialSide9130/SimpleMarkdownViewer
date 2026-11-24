'use strict';

const webview = window.chrome && window.chrome.webview;
const panesEl = document.getElementById('panes');
const chkSync = document.getElementById('chkSync');
const selTheme = document.getElementById('selTheme');
const msgEl = document.getElementById('message');

const MIN_WIDTH = 140;
const IMAGE_RE = /\.(png|jpe?g|gif|webp|svg|bmp|ico|avif)$/i;

const ICONS = {
  back: '<svg viewBox="0 0 16 16"><path d="M7.78 12.53a.75.75 0 0 1-1.06 0L2.47 8.28a.75.75 0 0 1 0-1.06l4.25-4.25a.75.75 0 0 1 1.06 1.06L4.81 7h7.44a.75.75 0 0 1 0 1.5H4.81l2.97 2.97a.75.75 0 0 1 0 1.06Z"/></svg>',
  left: '<svg viewBox="0 0 16 16"><path d="M9.78 12.78a.75.75 0 0 1-1.06 0L4.47 8.53a.75.75 0 0 1 0-1.06l4.25-4.25a.75.75 0 0 1 1.06 1.06L6.06 8l3.72 3.72a.75.75 0 0 1 0 1.06Z"/></svg>',
  right: '<svg viewBox="0 0 16 16"><path d="M6.22 3.22a.75.75 0 0 1 1.06 0l4.25 4.25a.75.75 0 0 1 0 1.06l-4.25 4.25a.75.75 0 0 1-1.06-1.06L9.94 8 6.22 4.28a.75.75 0 0 1 0-1.06Z"/></svg>',
  reload: '<svg viewBox="0 0 16 16"><path d="M8 2.5a5.5 5.5 0 1 0 5.5 5.5.75.75 0 0 1 1.5 0A7 7 0 1 1 8 1a6.97 6.97 0 0 1 4.75 1.86V1.75a.75.75 0 0 1 1.5 0v3.5a.75.75 0 0 1-.75.75H10a.75.75 0 0 1 0-1.5h1.94A5.48 5.48 0 0 0 8 2.5Z"/></svg>',
  close: '<svg viewBox="0 0 16 16"><path d="M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.75.75 0 1 1 1.06 1.06L9.06 8l3.22 3.22a.75.75 0 1 1-1.06 1.06L8 9.06l-3.22 3.22a.75.75 0 0 1-1.06-1.06L6.94 8 3.72 4.78a.75.75 0 0 1 0-1.06Z"/></svg>',
};

/** 表示順に並んだペイン */
const panes = [];
let activePane = null;   // F5 / Ctrl+W の対象
let scrollSource = null; // スクロール同期の発信元（マウスが乗っている／操作中のペイン）
let lastTitle = null;

function post(msg) { if (webview) webview.postMessage(msg); }

function storageGet(store, key) { try { return store.getItem(key); } catch { return null; } }
function storageSet(store, key, value) { try { store.setItem(key, value); } catch { /* ignore */ } }

// ------------------------------------------------------------------ パス変換

/** https://mdview.example/D/dir/a.md → D:\dir\a.md */
function urlToPath(href) {
  let u;
  try { u = new URL(href); } catch { return null; }
  if (u.origin !== location.origin) return null;
  const segs = u.pathname.split('/').filter(Boolean).map(s => {
    try { return decodeURIComponent(s); } catch { return s; }
  });
  if (!segs.length) return null;
  if (segs[0] === 'UNC') return '\\\\' + segs.slice(1).join('\\');
  if (/^[A-Za-z]$/.test(segs[0])) return segs[0] + ':\\' + segs.slice(1).join('\\');
  return null;
}

function stripHash(href) { const i = href.indexOf('#'); return i < 0 ? href : href.slice(0, i); }

// ------------------------------------------------------------------ ペイン管理

function createPane(url, grow, index) {
  const el = document.createElement('section');
  el.className = 'pane';
  el.innerHTML =
    '<div class="pane-header">' +
      '<span class="pane-title">読み込み中…</span>' +
      '<span class="pane-btns">' +
        `<button type="button" data-act="back" title="前の文書に戻る">${ICONS.back}</button>` +
        `<button type="button" data-act="left" title="左へ移動">${ICONS.left}</button>` +
        `<button type="button" data-act="right" title="右へ移動">${ICONS.right}</button>` +
        `<button type="button" data-act="reload" title="再読み込み (F5)">${ICONS.reload}</button>` +
        `<button type="button" data-act="close" title="閉じる (Ctrl+W)">${ICONS.close}</button>` +
      '</span>' +
    '</div>';

  const frame = document.createElement('iframe');
  frame.className = 'pane-frame';
  frame.src = url;
  el.appendChild(frame);

  const p = {
    el, frame,
    titleEl: el.querySelector('.pane-title'),
    btns: Object.fromEntries([...el.querySelectorAll('.pane-btns button')].map(b => [b.dataset.act, b])),
    url,
    path: urlToPath(url),
    grow: grow || 1,
    history: [],
    goingBack: false,
    restoreY: null,
  };

  el.querySelector('.pane-btns').addEventListener('click', e => {
    const b = e.target.closest('button');
    if (b) paneAction(p, b.dataset.act);
  });
  const header = el.querySelector('.pane-header');
  header.addEventListener('pointerdown', () => setActive(p));
  // ホイールクリック（中ボタン）で閉じる。mousedown を止めて自動スクロールモードに入らないようにする
  header.addEventListener('mousedown', e => { if (e.button === 1) e.preventDefault(); });
  header.addEventListener('auxclick', e => {
    if (e.button !== 1) return;
    e.preventDefault();
    closePane(p);
  });
  frame.addEventListener('load', () => onFrameLoad(p));

  if (index == null || index < 0 || index > panes.length) panes.push(p);
  else panes.splice(index, 0, p);

  // iframe は DOM 上で移動すると再読み込みされるため、追加後は動かさず CSS の order で並べ替える
  panesEl.appendChild(el);
  return p;
}

function layout() {
  panesEl.querySelectorAll('.splitter').forEach(s => s.remove());
  panes.forEach((p, i) => {
    p.el.style.order = String(i * 2);
    p.el.style.flex = `${p.grow} 1 0px`;
    p.btns.left.disabled = i === 0;
    p.btns.right.disabled = i === panes.length - 1;
    p.btns.back.disabled = p.history.length < 2;
    if (i < panes.length - 1) {
      const s = document.createElement('div');
      s.className = 'splitter';
      s.title = 'ドラッグで幅を変更 / ダブルクリックで均等割り';
      s.style.order = String(i * 2 + 1);
      s.addEventListener('pointerdown', e => startResize(e, i, s));
      s.addEventListener('dblclick', equalize);
      panesEl.appendChild(s);
    }
  });
  document.body.classList.toggle('no-panes', panes.length === 0);
  updateWindowTitle();
  saveSession();
}

function setActive(p) {
  activePane = p;
  panes.forEach(q => q.el.classList.toggle('active', q === p));
}

function paneAction(p, act) {
  const i = panes.indexOf(p);
  switch (act) {
    case 'close': closePane(p); break;
    case 'reload': reloadPane(p); break;
    case 'back': goBack(p); break;
    case 'left':
      if (i > 0) { [panes[i - 1], panes[i]] = [panes[i], panes[i - 1]]; layout(); }
      break;
    case 'right':
      if (i >= 0 && i < panes.length - 1) { [panes[i + 1], panes[i]] = [panes[i], panes[i + 1]]; layout(); }
      break;
  }
}

function closePane(p) {
  const i = panes.indexOf(p);
  if (i < 0) return;
  panes.splice(i, 1);
  p.el.remove();
  if (scrollSource === p) scrollSource = null;
  if (activePane === p) setActive(panes[Math.min(i, panes.length - 1)] || null);
  layout();
}

function reloadPane(p) {
  try {
    p.restoreY = p.frame.contentWindow.scrollY;
    p.frame.contentWindow.location.reload();
  } catch {
    p.frame.src = p.url;
  }
}

function goBack(p) {
  if (p.history.length < 2) return;
  p.history.pop();
  p.goingBack = true;
  try { p.frame.contentWindow.location.replace(p.history[p.history.length - 1]); }
  catch { p.frame.src = p.history[p.history.length - 1]; }
}

function equalize() {
  panes.forEach(p => { p.grow = 1; });
  layout();
}

function avgGrow() {
  if (!panes.length) return 1;
  return panes.reduce((s, p) => s + p.grow, 0) / panes.length;
}

function openFiles(files, at) {
  let idx = (at == null || at < 0) ? panes.length : Math.min(at, panes.length);
  const grow = avgGrow();
  let last = null;
  for (const f of files) last = createPane(f.url, grow, idx++);
  if (last) setActive(last);
  layout();
}

function onFrameLoad(p) {
  let win, doc;
  try { win = p.frame.contentWindow; doc = p.frame.contentDocument; } catch { /* cross-origin */ }
  if (!doc || !win) { p.titleEl.textContent = '(外部ページ)'; return; }
  const href = win.location.href;
  if (href === 'about:blank') return;

  p.url = href;
  const meta = doc.querySelector('meta[name="mdview-path"]');
  p.path = meta ? meta.content : urlToPath(href);
  const name = p.path ? p.path.split('\\').pop() : (doc.title || href);
  p.titleEl.textContent = name;
  p.titleEl.title = p.path || href;

  const key = stripHash(href);
  if (p.goingBack) p.goingBack = false;
  else if (p.history[p.history.length - 1] !== key) p.history.push(key);

  if (p.restoreY != null) {
    win.scrollTo(0, p.restoreY);
    p.restoreY = null;
  }

  hookDocument(p, win, doc);
  layout();
}

/** iframe 内ドキュメントにイベントを仕掛ける（同一オリジンなので直接アクセス可能） */
function hookDocument(p, win, doc) {
  const markSource = () => { scrollSource = p; };
  doc.addEventListener('pointerdown', () => { scrollSource = p; setActive(p); }, true);
  doc.addEventListener('mousemove', markSource, { passive: true });
  doc.addEventListener('wheel', markSource, { passive: true, capture: true });
  doc.addEventListener('keydown', e => { scrollSource = p; onKeyDown(e, p); }, true);
  doc.addEventListener('dragover', e => onDragOver(e, p));
  doc.addEventListener('drop', e => onDrop(e, p));
  win.addEventListener('scroll', () => onPaneScroll(p), { passive: true });
}

// ------------------------------------------------------------------ 幅の調整

function startResize(e, i, splitter) {
  if (e.button !== 0) return;
  e.preventDefault();
  // 全ペインの現在幅(px)を flex-grow に固定してから、隣接 2 ペインだけ調整する
  // （読み取りを先に全部済ませる。書き込みと交互にするとレイアウトが途中で変わってしまう）
  const widths = panes.map(p => p.el.getBoundingClientRect().width);
  panes.forEach((p, k) => {
    p.grow = widths[k];
    p.el.style.flex = `${p.grow} 1 0px`;
  });
  const a = panes[i], b = panes[i + 1];
  const startX = e.clientX, wa = a.grow, wb = b.grow;

  splitter.setPointerCapture(e.pointerId);
  splitter.classList.add('dragging');
  document.body.classList.add('resizing');

  const move = ev => {
    let dx = ev.clientX - startX;
    dx = Math.max(Math.min(0, MIN_WIDTH - wa), Math.min(dx, Math.max(0, wb - MIN_WIDTH)));
    a.grow = wa + dx;
    b.grow = wb - dx;
    a.el.style.flex = `${a.grow} 1 0px`;
    b.el.style.flex = `${b.grow} 1 0px`;
  };
  const up = () => {
    splitter.removeEventListener('pointermove', move);
    splitter.removeEventListener('pointerup', up);
    splitter.removeEventListener('pointercancel', up);
    splitter.classList.remove('dragging');
    document.body.classList.remove('resizing');
    saveSession();
  };
  splitter.addEventListener('pointermove', move);
  splitter.addEventListener('pointerup', up);
  splitter.addEventListener('pointercancel', up);
}

// ------------------------------------------------------------------ スクロール同期

function scrollRatio(p) {
  const d = p.frame.contentDocument.scrollingElement;
  const max = d.scrollHeight - d.clientHeight;
  return max > 0 ? d.scrollTop / max : 0;
}

function onPaneScroll(p) {
  if (!chkSync.checked || scrollSource !== p) return;
  let r;
  try { r = scrollRatio(p); } catch { return; }
  for (const q of panes) {
    if (q === p) continue;
    try {
      const d = q.frame.contentDocument.scrollingElement;
      d.scrollTop = r * (d.scrollHeight - d.clientHeight);
    } catch { /* ignore */ }
  }
}

function setSync(on) {
  chkSync.checked = on;
  storageSet(localStorage, 'sync', on ? '1' : '0');
  // オンにした時点でアクティブなペインに位置を揃える
  if (on && activePane) { scrollSource = activePane; onPaneScroll(activePane); }
}

// ------------------------------------------------------------------ ドラッグ＆ドロップ

let dragTimer = null;

function hasFiles(e) {
  return !!e.dataTransfer && Array.from(e.dataTransfer.types || []).includes('Files');
}

function setDropTarget(p) {
  panes.forEach(q => q.el.classList.toggle('drop-target', q === p));
}

function clearDrag() {
  clearTimeout(dragTimer);
  setDropTarget(null);
  document.body.classList.remove('drag-over');
}

function onDragOver(e, p) {
  if (!hasFiles(e)) return;
  e.preventDefault();
  e.dataTransfer.dropEffect = 'copy';
  setDropTarget(p);
  document.body.classList.add('drag-over');
  clearTimeout(dragTimer);
  dragTimer = setTimeout(clearDrag, 200);
}

function onDrop(e, p) {
  if (!hasFiles(e)) return;
  e.preventDefault();
  clearDrag();
  const files = e.dataTransfer.files;
  if (!files || !files.length || !webview) return;
  // ペイン上にドロップしたらそのペインの右に、それ以外は末尾に追加
  const at = p ? panes.indexOf(p) + 1 : -1;
  webview.postMessageWithAdditionalObjects({ type: 'drop', at }, files);
}

function paneFromEvent(e) {
  const el = e.target instanceof Element ? e.target.closest('.pane') : null;
  return el ? panes.find(p => p.el === el) || null : null;
}

document.addEventListener('dragover', e => onDragOver(e, paneFromEvent(e)));
document.addEventListener('drop', e => onDrop(e, paneFromEvent(e)));

// ------------------------------------------------------------------ キーボード

function onKeyDown(e, p) {
  const ctrl = e.ctrlKey || e.metaKey;
  const k = e.key.length === 1 ? e.key.toLowerCase() : e.key;
  const target = p || activePane;
  if (ctrl && !e.shiftKey && !e.altKey && k === 'o') {
    e.preventDefault();
    post({ type: 'openDialog', at: -1 });
  } else if (k === 'F5' || (ctrl && k === 'r')) {
    e.preventDefault();
    if (target) reloadPane(target);
  } else if (ctrl && k === 'w') {
    e.preventDefault();
    if (target) closePane(target);
  } else if (ctrl && k === 'l') {
    e.preventDefault();
    setSync(!chkSync.checked);
  }
}
document.addEventListener('keydown', e => onKeyDown(e, null));

// ------------------------------------------------------------------ ホストからのメッセージ

function onChanged(path) {
  const lp = path.toLowerCase();
  const isImg = IMAGE_RE.test(path);
  for (const p of panes) {
    if (p.path && p.path.toLowerCase() === lp) { reloadPane(p); continue; }
    if (!isImg) continue;
    try {
      for (const img of p.frame.contentDocument.images) {
        const ip = urlToPath(img.src);
        if (ip && ip.toLowerCase() === lp) { reloadPane(p); break; }
      }
    } catch { /* ignore */ }
  }
}

let msgTimer = null;
function showMessage(text) {
  msgEl.textContent = text;
  msgEl.title = text;
  clearTimeout(msgTimer);
  msgTimer = setTimeout(() => { msgEl.textContent = ''; msgEl.title = ''; }, 6000);
}

if (webview) {
  webview.addEventListener('message', e => {
    const m = e.data;
    if (!m || typeof m !== 'object') return;
    switch (m.type) {
      case 'open': openFiles(m.files || [], m.at); break;
      case 'changed': onChanged(m.path); break;
      case 'error': showMessage(m.message); break;
    }
  });
}

// ------------------------------------------------------------------ ウィンドウタイトル・セッション

function updateWindowTitle() {
  const t = panes.map(p => p.titleEl.textContent).join(' | ');
  if (t === lastTitle) return;
  lastTitle = t;
  post({ type: 'title', value: t });
}

/** シェル自体が再読み込みされてもペインを復元できるよう sessionStorage に保存 */
function saveSession() {
  storageSet(sessionStorage, 'panes', JSON.stringify({
    panes: panes.map(p => ({ url: p.url, grow: p.grow })),
    active: panes.indexOf(activePane),
  }));
}

function restoreSession() {
  let s = null;
  try { s = JSON.parse(storageGet(sessionStorage, 'panes') || 'null'); } catch { /* ignore */ }
  if (!s || !Array.isArray(s.panes)) return;
  for (const x of s.panes) {
    if (typeof x.url === 'string' && x.url.startsWith(location.origin + '/')) createPane(x.url, x.grow, -1);
  }
  setActive(panes[s.active] || panes[0] || null);
}

// ------------------------------------------------------------------ 初期化

document.getElementById('btnOpen').addEventListener('click', () => post({ type: 'openDialog', at: -1 }));
document.getElementById('btnOpenEmpty').addEventListener('click', () => post({ type: 'openDialog', at: -1 }));
chkSync.addEventListener('change', () => setSync(chkSync.checked));
selTheme.addEventListener('change', () => {
  storageSet(localStorage, 'theme', selTheme.value);
  post({ type: 'theme', value: selTheme.value });
});

chkSync.checked = storageGet(localStorage, 'sync') === '1';
selTheme.value = storageGet(localStorage, 'theme') || 'auto';
post({ type: 'theme', value: selTheme.value });

restoreSession();
layout();
post({ type: 'ready' });
