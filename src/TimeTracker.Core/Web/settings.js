'use strict';
let st = null;

const sw = (id, on, label) => `<button class="switch" id="${id}" role="switch" aria-checked="${on ? 'true' : 'false'}" aria-label="${esc(label)}"></button>`;
const RUNKEY = 'HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\TimeTracker';

function fmtBytes(n) { return n < 1048576 ? Math.max(1, Math.round(n / 1024)) + ' KB' : (n / 1048576).toFixed(1) + ' MB'; }

function ruleText(a) {
  let s = a.matchKind === 'exe_path' ? a.matchValue : a.matchValue;
  if (a.titleContains) s += ' · ' + t('set.names.titleRule', a.titleContains);
  return s;
}

function render() {
  const a = st.autostart, on = a.state === 'enabled';
  const status = a.state === 'enabled'
    ? `<div class="status">${esc(t('set.as.on'))}<span class="mono">${RUNKEY} = ${esc(t('set.as.present'))}</span></div>`
    : a.state === 'blocked'
      ? `<div class="status warn">⚠ ${esc(t('set.as.blocked'))} <a id="openwin">${esc(t('set.as.openWin'))}</a><span class="mono">${RUNKEY} = ${esc(t('set.as.present'))}</span></div>`
      : `<div class="status warn">⚠ ${esc(t('set.as.off'))}<span class="mono">${RUNKEY} = ${esc(t('set.as.removed'))}</span></div>`;
  const idleOpts = [2, 5, 10, 15, 30].map(m => `<option value="${m}" ${m === st.idleMinutes ? 'selected' : ''}>${esc(t('set.minutes', m))}</option>`).join('');
  const nCustom = st.aliases.length;
  const names = st.aliases.map(x => `<div class="nr"><div class="a"><b>${esc(x.displayName)}</b>${x.hidden ? ` <span class="tag">${esc(t('set.names.hidden'))}</span>` : ''}<small>${esc(ruleText(x))}</small></div>
      ${x.hidden ? `<button data-show="${x.id}">${esc(t('set.names.show'))}</button>` : ''}<button data-del="${x.id}">${esc(t('set.names.remove'))}</button></div>`).join('');
  document.getElementById('root').innerHTML = `
  <div class="sgroup"><h5>${esc(t('set.general'))}</h5><div class="sbox">
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.language'))}</div><div class="ds">${esc(t('set.language.d'))}</div></div>
      <div class="ctl"><select class="sel" id="lang" aria-label="${esc(t('set.language'))}">
        <option value="auto" ${st.language === 'auto' ? 'selected' : ''}>${esc(t('set.langAuto', st.resolvedLanguage === 'de' ? 'Deutsch' : 'English'))}</option>
        <option value="en" ${st.language === 'en' ? 'selected' : ''}>English</option><option value="de" ${st.language === 'de' ? 'selected' : ''}>Deutsch</option></select></div></div>
    <div class="srow"><div class="tx"><div class="tt" id="as-t">${esc(t('set.autostart'))}</div><div class="ds">${esc(t('set.autostart.d'))}</div></div>
      <div class="ctl"><span class="swlab">${esc(on ? t('set.on') : t('set.off'))}</span>${sw('as', on, t('set.autostart'))}</div></div>
    ${status}
  </div></div>

  <div class="sgroup"><h5>${esc(t('set.tracking'))}</h5><div class="sbox">
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.idle'))}</div><div class="ds">${esc(t('set.idle.d'))}</div></div><div class="ctl"><select class="sel" id="idle" aria-label="${esc(t('set.idle'))}">${idleOpts}</select></div></div>
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.fullscreen'))}</div><div class="ds">${esc(t('set.fullscreen.d'))}</div></div><div class="ctl">${sw('fs', st.fullscreenActive, t('set.fullscreen'))}</div></div>
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.titles'))}</div><div class="ds">${esc(t('set.titles.d'))}</div></div><div class="ctl">${sw('ti', st.recordTitles, t('set.titles'))}</div></div>
  </div></div>

  <div class="sgroup"><h5>${esc(t('set.apps'))}</h5><div class="sbox">
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.names'))}</div><div class="ds">${esc(t('set.names.count', nCustom, st.aliases.filter(x => x.hidden).length))}</div></div>
      <div class="ctl"><button id="manage">${esc(t('set.manage'))}</button></div></div>
    <div class="names-list hidden" id="nameslist">${names || `<div class="muted" style="font-size:12px;padding-top:8px">${esc(t('set.names.none'))}</div>`}</div>
  </div></div>

  <div class="sgroup"><h5>${esc(t('set.reports'))}</h5><div class="sbox">
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.reportDir'))}</div><div class="ds mono">${esc(st.reportDir)}</div></div><div class="ctl"><button id="chdir">${esc(t('set.change'))}</button></div></div>
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.autoReports'))}</div><div class="ds">${esc(t('set.autoReports.d'))}</div></div><div class="ctl">${sw('ar', st.autoReports, t('set.autoReports'))}</div></div>
  </div></div>

  <div class="sgroup"><h5>${esc(t('set.protection'))}</h5><div class="sbox">
    <div class="srow"><div class="tx"><div class="tt">${esc(t('set.pin'))}</div><div class="ds">${esc(t('set.pin.d'))}</div></div>
      <div class="ctl"><button id="setpin">${esc(st.pinSet ? t('set.pin.change') : t('set.pin.set'))}</button>${sw('pin', st.pinSet, t('set.pin'))}</div></div>
  </div></div>

  <div class="setfoot"><span>TimeTracker ${esc(st.version)} · ${esc(st.device)} · ${esc(t('set.dataLocal'))}: <span class="mono">${esc(st.dbPath)}</span> (${fmtBytes(st.dbSize)})</span>
    <span><button id="openData">${esc(t('set.openFolder'))}</button> <button id="delData" class="danger">${esc(t('set.deleteAll'))}</button> ${st.installed ? `<button id="uninst">${esc(t('set.uninstall'))}</button>` : ''}</span></div>`;
  wire();
}

function toggle(el) { const v = el.getAttribute('aria-checked') !== 'true'; el.setAttribute('aria-checked', v); return v; }

function wire() {
  const $ = id => document.getElementById(id);
  $('lang').onchange = async e => { await api('setSetting', { key: 'language', value: e.target.value }); const i = await api('init'); setLang(i.lang, i.strings); await refresh(); };
  $('as').onclick = async e => { const v = toggle(e.currentTarget); await api('setAutostart', { enabled: v }); await refresh(); };
  const owow = $('openwin'); if (owow) owow.onclick = () => api('openWindowsStartup');
  $('idle').onchange = e => api('setSetting', { key: 'idleMinutes', value: +e.target.value });
  $('fs').onclick = e => api('setSetting', { key: 'fullscreenActive', value: toggle(e.currentTarget) });
  $('ti').onclick = e => api('setSetting', { key: 'recordTitles', value: toggle(e.currentTarget) });
  $('ar').onclick = e => api('setSetting', { key: 'autoReports', value: toggle(e.currentTarget) });
  $('manage').onclick = () => $('nameslist').classList.toggle('hidden');
  document.querySelectorAll('[data-del]').forEach(b => b.onclick = async () => { await api('deleteAlias', { id: +b.dataset.del }); await refresh(); $('nameslist').classList.remove('hidden'); });
  document.querySelectorAll('[data-show]').forEach(b => b.onclick = async () => { await api('showAlias', { id: +b.dataset.show }); await refresh(); $('nameslist').classList.remove('hidden'); });
  $('chdir').onclick = async () => { const d = await api('chooseReportDir'); if (d) { await refresh(); } };
  $('setpin').onclick = pinDialog;
  $('pin').onclick = async e => {
    if (st.pinSet) { await api('clearPin'); await refresh(); } else pinDialog();
  };
  $('openData').onclick = () => api('openFolder', { which: 'data' });
  $('delData').onclick = async () => { const r = await api('deleteAllData'); if (r) await refresh(); };
  const u = $('uninst'); if (u) u.onclick = () => api('uninstall');
}

function closeModal() { document.getElementById('modal').innerHTML = ''; }
function pinDialog() {
  const m = document.getElementById('modal');
  m.innerHTML = `<div class="backdrop"><div class="dlg" style="width:380px" role="dialog" aria-modal="true"><h4>${esc(t('pin.title'))}</h4>
    <div class="field"><label class="l" for="p1">${esc(t('pin.new'))}</label><input type="password" id="p1" inputmode="numeric" maxlength="12" autocomplete="off"></div>
    <div class="field"><label class="l" for="p2">${esc(t('pin.confirm'))}</label><input type="password" id="p2" inputmode="numeric" maxlength="12" autocomplete="off"><div class="h" id="perr">${esc(t('pin.help'))}</div></div>
    <div class="acts"><button class="primary" id="psave">${esc(t('btn.save'))}</button><button id="pcancel">${esc(t('btn.cancel'))}</button></div></div></div>`;
  const p1 = document.getElementById('p1'), p2 = document.getElementById('p2'), err = document.getElementById('perr');
  p1.focus();
  document.getElementById('pcancel').onclick = () => { closeModal(); refresh(); };
  document.getElementById('psave').onclick = async () => {
    if (p1.value.length < 4) { err.textContent = t('pin.tooShort'); err.style.color = 'var(--danger)'; p1.focus(); return; }
    if (p1.value !== p2.value) { err.textContent = t('pin.mismatch'); err.style.color = 'var(--danger)'; p2.focus(); return; }
    // false = the current PIN was not entered correctly: keep the dialog open, nothing was changed.
    if (await api('setPin', { pin: p1.value })) { closeModal(); await refresh(); }
  };
  m.onkeydown = e => { if (e.key === 'Enter') document.getElementById('psave').click(); if (e.key === 'Escape') document.getElementById('pcancel').click(); };
}

async function refresh() { st = await api('getSettings'); document.title = t('set.title'); render(); }

boot(async () => {
  await refresh();
  window.addEventListener('host-refresh', refresh);
});
