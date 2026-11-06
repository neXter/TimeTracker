'use strict';
const PERIODS = ['day', 'week', 'month', 'year'];
const dayKey = d => `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`;
let state = { period: 'week', anchor: dayKey(new Date()) };
let S = null, loading = false, INFO = { version: '' };

function shift(dir) {
  const d = parseDay(state.anchor);
  if (state.period === 'day') d.setDate(d.getDate() + dir);
  else if (state.period === 'week') d.setDate(d.getDate() + 7 * dir);
  else if (state.period === 'month') { d.setDate(1); d.setMonth(d.getMonth() + dir); }
  else { d.setDate(1); d.setMonth(0); d.setFullYear(d.getFullYear() + dir); }
  state.anchor = dayKey(d);
  load();
}

async function load() {
  if (loading) return;
  loading = true;
  try {
    S = await api('stats', { period: state.period, anchor: state.anchor });
    document.getElementById('lbl').textContent = periodLabel(S);
    document.getElementById('next').disabled = S.to >= dayKey(new Date());
    renderStats(document.getElementById('content'), S, { onRename: openRename, onSessions: openSessions });
  } finally { loading = false; }
}

function buildChrome(init) {
  INFO = init;
  document.title = 'TimeTracker · ' + init.device;
  document.getElementById('dev').textContent = init.device;
  document.getElementById('dev').title = t('dash.deviceTip');
  const p = document.getElementById('periods');
  p.innerHTML = '';
  for (const k of PERIODS) {
    const b = document.createElement('button');
    b.textContent = t('period.' + k);
    b.className = state.period === k ? 'on' : '';
    b.setAttribute('role', 'tab');
    b.onclick = () => { state.period = k; buildChrome(init); load(); };
    p.appendChild(b);
  }
  document.getElementById('prev').setAttribute('aria-label', t('nav.prev'));
  document.getElementById('next').setAttribute('aria-label', t('nav.next'));
  const tb = document.getElementById('todaybtn');
  tb.textContent = t('nav.today');
  tb.onclick = () => { state.anchor = dayKey(new Date()); load(); };
  const ex = document.getElementById('export');
  ex.textContent = t('export.btn') + ' ▾';
  ex.onclick = () => popMenu(ex, [
    { label: t('export.thisPeriod'), fn: exportReport },
    { label: t('export.openFolder'), fn: () => api('openFolder', { which: 'reports' }) },
  ], true);
  const ab = document.getElementById('about');
  ab.setAttribute('aria-label', t('about.btn')); ab.title = t('about.btn');
  ab.onclick = ev => { ev.stopPropagation(); toggleAbout(ab); };
}

/** Small "about" card under the (i) button: app icon, author, version and the Claude Code credit. */
function toggleAbout(anchor) {
  const open = document.querySelector('.menu.about');
  if (open) { open.remove(); anchor.setAttribute('aria-expanded', 'false'); return; }
  document.querySelectorAll('.menu').forEach(m => m.remove());
  const m = document.createElement('div');
  m.className = 'menu about'; m._born = Date.now(); m.setAttribute('role', 'dialog'); m.setAttribute('aria-label', t('about.btn'));
  m.innerHTML = `
    <div class="about-head"><img src="icon.png" alt=""><div><div class="about-name">TimeTracker</div><div class="about-ver">${esc(t('about.version', INFO.version))}</div></div></div>
    <div class="about-row"><span class="k">${esc(t('about.author'))}</span>${esc(t('about.authorName'))}</div>
    <div class="about-row"><span class="k">${esc(t('about.builtWith'))}</span>${esc(t('about.credit'))}</div>
    <div class="about-row"><span class="k">${esc(t('about.privacy'))}</span>${esc(t('about.local'))}</div>
    <div class="about-foot">${esc(t('about.rights', new Date().getFullYear()))}</div>`;
  document.body.appendChild(m);
  const r = anchor.getBoundingClientRect();
  m.style.left = Math.max(8, Math.min(r.right - m.offsetWidth, innerWidth - m.offsetWidth - 8)) + 'px';
  m.style.top = (r.bottom + 8) + 'px';
  anchor.setAttribute('aria-expanded', 'true');
  const obs = new MutationObserver(() => { if (!document.body.contains(m)) { anchor.setAttribute('aria-expanded', 'false'); obs.disconnect(); } });
  obs.observe(document.body, { childList: true });
}

async function exportReport() {
  await api('generateReport', { period: state.period, anchor: state.anchor });
  const tt = toast(`${esc(t('export.done'))} <a id="tf">${esc(t('export.showFolder'))}</a>`, 8000);
  tt.querySelector('#tf').onclick = () => api('openFolder', { which: 'reports' });
}

// ---------- modals ----------
function closeModal() { document.getElementById('modal').innerHTML = ''; }
function openModal(html, cls) {
  const m = document.getElementById('modal');
  m.innerHTML = `<div class="backdrop"><div class="dlg ${cls || ''}" role="dialog" aria-modal="true">${html}</div></div>`;
  m.querySelector('.backdrop').addEventListener('mousedown', e => { if (e.target.classList.contains('backdrop')) closeModal(); });
  return m.querySelector('.dlg');
}
document.addEventListener('keydown', e => { if (e.key === 'Escape') { closeModal(); document.querySelectorAll('.menu').forEach(m => m.remove()); } });

function openRename(g) {
  if (!g) return;
  const others = (S ? S.groups : []).filter(x => x.id !== g.id && !x.unnamed);
  const usage = g.fg !== undefined ? esc(t('rn.usage', dur(g.fg), g.sessions)) : '';
  const d = openModal(`
    <h4>${esc(t('rn.title'))}</h4>
    <div class="appbox"><span class="appico">${esc(g.unnamed ? '?' : (g.name[0] || '?').toUpperCase())}</span><div>
      <div><b>${esc(g.exe)}</b></div><div class="p">${esc(g.path)}</div><div class="u">${usage}</div></div></div>
    <div class="field"><label class="l" for="rn-name">${esc(t('rn.name'))}</label>
      <input type="text" id="rn-name" maxlength="80" autocomplete="off" placeholder="${esc(t('rn.placeholder'))}" value="${esc(g.unnamed ? '' : g.name)}">
      <div class="h">${esc(g.unnamed ? t('rn.detectedNone') : t('rn.detected', g.detectedName || g.name))}</div></div>
    <div class="preview" id="rn-prev"></div>
    <div class="field"><label class="l">${esc(t('rn.applyTo'))}</label><div class="radios">
      <label><input type="radio" name="rn-kind" value="exe_path" checked><span>${esc(t('rn.thisFile'))}</span></label>
      <label><input type="radio" name="rn-kind" value="exe_name"><span>${esc(t('rn.anyFile', g.exe))}</span></label></div></div>
    <div class="field"><label class="l" for="rn-title">${esc(t('rn.titleRule'))} <span class="muted" style="font-weight:400">${esc(t('rn.optional'))}</span></label>
      <input type="text" id="rn-title" autocomplete="off" placeholder="${esc(t('rn.titlePlaceholder'))}"><div class="h">${esc(t('rn.titleHelp'))}</div></div>
    <div class="field"><label class="l" for="rn-merge">${esc(t('rn.merge'))} <span class="muted" style="font-weight:400">${esc(t('rn.optional'))}</span></label>
      <select id="rn-merge"><option value="">${esc(t('rn.noMerge'))}</option>${others.map(o => `<option>${esc(o.name)}</option>`).join('')}</select><div class="h">${esc(t('rn.mergeHelp'))}</div></div>
    <label class="check"><input type="checkbox" id="rn-hide"> ${esc(t('rn.hide'))}</label>
    <div class="acts"><button class="primary" id="rn-save">${esc(t('btn.save'))}</button><button id="rn-cancel">${esc(t('btn.cancel'))}</button><span class="sp"></span>
      ${g.custom ? `<a id="rn-reset">${esc(t('rn.reset'))}</a>` : ''}</div>`);
  const name = d.querySelector('#rn-name'), prev = d.querySelector('#rn-prev');
  const upd = () => { prev.innerHTML = esc(t('rn.preview', '@@')).replace('@@', `<b>${esc(name.value.trim() || g.exe)}</b>`); };
  name.oninput = upd; upd(); name.focus(); name.select();
  d.querySelector('#rn-cancel').onclick = closeModal;
  const reset = d.querySelector('#rn-reset');
  if (reset) reset.onclick = async () => { await api('resetApps', { appIds: g.appIds }); closeModal(); load(); };
  d.querySelector('#rn-save').onclick = async () => {
    const merge = d.querySelector('#rn-merge').value;
    if (!name.value.trim() && !merge) { name.focus(); return; }
    await api('saveAlias', {
      appIds: g.appIds, name: name.value.trim(), mergeInto: merge || null,
      matchKind: d.querySelector('input[name=rn-kind]:checked').value,
      titleContains: d.querySelector('#rn-title').value.trim() || null, hidden: d.querySelector('#rn-hide').checked,
    });
    closeModal(); load();
  };
  d.addEventListener('keydown', e => { if (e.key === 'Enter' && e.target.tagName === 'INPUT') d.querySelector('#rn-save').click(); });
}

async function openSessions(g) {
  if (!g) return;
  const list = await api('sessions', { appIds: g.appIds, period: state.period, anchor: state.anchor });
  const anyTitle = list.some(x => x.title);
  const rows = list.map(x => `<tr><td>${esc(fmtDT(x.startMs, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }))}</td><td class="num">${dur((x.endMs - x.startMs) / 1000)}</td><td>${esc(x.title || '')}</td></tr>`).join('');
  const d = openModal(`<h4>${esc(g.name)}</h4><div class="muted" style="margin:-6px 0 10px;font-size:12px">${esc(t('sess.sub', periodLabel(S)))}${anyTitle ? '' : ' · ' + esc(t('sess.noTitles'))}</div>
    <table><thead><tr><th>${esc(t('sess.start'))}</th><th class="num">${esc(t('sess.length'))}</th><th>${esc(t('sess.title'))}</th></tr></thead><tbody>${rows || `<tr><td colspan="3" class="muted">${esc(t('sess.none'))}</td></tr>`}</tbody></table>
    <div class="acts" style="margin-top:14px"><span class="sp"></span><button id="sc">${esc(t('btn.close'))}</button></div>`, 'wide');
  d.querySelector('#sc').onclick = closeModal;
}

async function renameByIds(ids) {
  const g = await api('appInfo', { appIds: ids });
  if (g) openRename(g);
}

// ---------- start ----------
document.getElementById('prev').onclick = () => shift(-1);
document.getElementById('next').onclick = () => shift(1);

boot(async init => {
  buildChrome(init);
  await load();
  const q = new URLSearchParams(location.search).get('rename');
  if (q) renameByIds(q.split(',').map(Number));
  window.addEventListener('host-rename', e => renameByIds(e.detail.appIds));
  window.addEventListener('host-refresh', async () => { const i = await api('init'); setLang(i.lang, i.strings); buildChrome(i); load(); });
  setInterval(() => { if (!document.getElementById('modal').innerHTML && !document.hidden) load(); }, 30000);
});
