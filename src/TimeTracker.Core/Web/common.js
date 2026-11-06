'use strict';
/* Shared helpers: host bridge, translations, formatting, tooltips. */

const host = window.chrome && window.chrome.webview;
const pending = new Map();
let seq = 0;
if (host) {
  host.addEventListener('message', e => {
    const m = e.data;
    if (m && m.id && pending.has(m.id)) {
      const p = pending.get(m.id); pending.delete(m.id);
      if (m.ok) p.res(m.result); else p.rej(new Error(m.error || 'error'));
    } else if (m && m.event) {
      window.dispatchEvent(new CustomEvent('host-' + m.event, { detail: m }));
    }
  });
}
function api(cmd, args) {
  if (!host) return window.__mockApi ? Promise.resolve(window.__mockApi(cmd, args || {})) : Promise.reject(new Error('no host'));
  return new Promise((res, rej) => { const id = ++seq; pending.set(id, { res, rej }); host.postMessage({ id, cmd, args: args || {} }); });
}

// ---------- translations ----------
let LANG = 'en', STR = {};
function setLang(lang, strings) { LANG = lang; STR = strings || {}; document.documentElement.lang = lang; }
function t(key, ...a) {
  const s = STR[key] !== undefined ? STR[key] : key;
  return s.replace(/\{(\d+)\}/g, (m, i) => (a[+i] !== undefined ? a[+i] : ''));
}
const LOCALE = () => (LANG === 'de' ? 'de-DE' : 'en-GB');
const fmtDT = (ms, opts) => new Intl.DateTimeFormat(LOCALE(), opts).format(new Date(ms));
const parseDay = key => { const [y, m, d] = key.split('-').map(Number); return new Date(y, (m || 1) - 1, d || 1); };
const fmtDay = (d, opts) => new Intl.DateTimeFormat(LOCALE(), opts).format(d);

function esc(s) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); }

function dur(sec) {
  sec = Math.max(0, Math.round(sec));
  const m = Math.round(sec / 60);
  if (sec > 0 && m === 0) return t('fmt.lt1m');
  if (m < 60) return t('fmt.m', m);
  return t('fmt.hm', Math.floor(m / 60), String(m % 60).padStart(2, '0'));
}
function pad2(n) { return String(n).padStart(2, '0'); }
function hhmm(ms) { return fmtDT(ms, { hour: '2-digit', minute: '2-digit' }); }
function secToClock(sec) { const m = Math.round(sec / 60); return pad2(Math.floor(m / 60) % 24) + ':' + pad2(m % 60); }

// ---------- tooltip (data-tip on any element) ----------
function initTips() {
  let tip = document.getElementById('tip');
  if (!tip) { tip = document.createElement('div'); tip.id = 'tip'; tip.setAttribute('role', 'tooltip'); document.body.appendChild(tip); }
  const show = (html, x, y) => {
    tip.innerHTML = html; tip.style.display = 'block';
    const w = tip.offsetWidth, h = tip.offsetHeight;
    tip.style.left = Math.max(4, Math.min(x + 14, innerWidth - w - 8)) + 'px';
    tip.style.top = Math.max(4, Math.min(y + 14, innerHeight - h - 8)) + 'px';
  };
  document.addEventListener('mousemove', e => {
    const el = e.target.closest && e.target.closest('[data-tip]');
    if (el) show(el.getAttribute('data-tip'), e.clientX, e.clientY); else tip.style.display = 'none';
  });
  document.addEventListener('mouseleave', () => (tip.style.display = 'none'));
  document.addEventListener('focusin', e => {
    const el = e.target.closest && e.target.closest('[data-tip]');
    if (el) { const r = el.getBoundingClientRect(); show(el.getAttribute('data-tip'), r.right, r.top); }
  });
  document.addEventListener('focusout', () => (tip.style.display = 'none'));
}

function toast(html, ms) {
  const d = document.createElement('div'); d.className = 'toast'; d.innerHTML = html;
  document.body.appendChild(d);
  setTimeout(() => d.remove(), ms || 6000);
  return d;
}

/** Loads the language and calls back with the init payload. */
async function boot(fn) {
  initTips();
  const init = await api('init');
  setLang(init.lang, init.strings);
  await fn(init);
}

/** Pop-up menu under an anchor element. items: [{label, fn}] */
function popMenu(anchor, items, alignRight, openUp) {
  document.querySelectorAll('.menu').forEach(m => m.remove());
  const m = document.createElement('div'); m.className = 'menu'; m._born = Date.now();
  for (const it of items) {
    const b = document.createElement('button'); b.textContent = it.label;
    b.onclick = () => { m.remove(); it.fn(); };
    m.appendChild(b);
  }
  document.body.appendChild(m);
  const r = anchor.getBoundingClientRect();
  const left = alignRight ? r.right - m.offsetWidth : r.left;
  m.style.left = Math.max(4, Math.min(left, innerWidth - m.offsetWidth - 4)) + 'px';
  m.style.top = (openUp ? r.top - m.offsetHeight - 4 : r.bottom + 4) + 'px';
  return m;
}
document.addEventListener('click', e => {
  document.querySelectorAll('.menu').forEach(m => { if (Date.now() - m._born > 50 && !m.contains(e.target)) m.remove(); });
});
