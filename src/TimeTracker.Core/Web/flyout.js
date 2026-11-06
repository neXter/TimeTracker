'use strict';
async function render() {
  const F = await api('flyout'), S = F.stats, TL = S.timeline, D = TL.daySeconds;
  const p = x => x / D * 100;
  const clock = sec => fmtDT(TL.dayStartMs + sec * 1000, { hour: '2-digit', minute: '2-digit' });
  const segs = TL.device.filter(s => s.state === 'active' || s.state === 'idle').map(s => {
    const tip = `<div class="t">${esc(t('state.' + s.state))}</div><div class="m">${clock(s.s)} – ${clock(s.e)} · ${dur(s.e - s.s)}</div>`;
    return `<div class="seg" data-tip="${esc(tip)}" style="left:calc(${p(s.s)}% + 1px);width:max(2px,calc(${p(s.e - s.s)}% - 2px));background:${s.state === 'active' ? 'var(--s1)' : 'var(--idle)'}"></div>`;
  }).join('');
  const nowP = Math.min(100, Math.max(0, (F.nowMs - TL.dayStartMs) / 1000 / D * 100));
  const top = S.groups.filter(g => g.fg > 0).slice(0, 5);
  const maxFg = Math.max(1, ...top.map(g => g.fg));
  const rows = top.map(g => `<div class="toprow"><span class="nm" title="${esc(g.name)}"><i style="background:${SLOT(g)}"></i>${esc(g.name)}</span>
      <span><div class="bar" style="width:${g.fg / maxFg * 100}%;background:${SLOT(g)}"></div></span><span class="v">${dur(g.fg)}</span></div>`).join('');
  const un = F.unnamed;
  const hint = un.length ? `<div class="hint-line">⚠ <span>${esc(un.length === 1 ? t('fl.unnamed1') : t('fl.unnamedN', un.length))} <span class="mono">${un.length === 1 ? esc(un[0].exe) : ''}</span></span>
      <a id="nameit">${esc(t('fl.nameIt'))}</a><span class="x" id="dismiss" title="${esc(t('fl.keep'))}">✕</span></div>` : '';
  const state = F.paused
    ? `<span class="state paused"><i></i>${esc(t('fl.paused'))} · <a id="resume">${esc(t('fl.resume'))}</a></span>`
    : `<span class="state"><i></i>${esc(t('fl.tracking'))}</span>`;
  document.getElementById('root').innerHTML = `
    <div class="fl-head"><span class="fl-device">${esc(S.device)}</span><span>${esc(t('fl.today'))} · ${esc(fmtDT(F.nowMs, { weekday: 'short', day: 'numeric', month: 'short' }))}</span></div>
    <div class="fl-big">${dur(S.tiles.activeSec)}</div>
    <div class="fl-sub">${esc(t('fl.activeToday'))}${F.firstActiveMs ? ' · ' + esc(t('fl.firstActivity', hhmm(F.firstActiveMs))) : ''}</div>
    <div class="strip" role="img" aria-label="${esc(t('fl.stripLabel'))}">${segs}<div class="now" style="left:${nowP}%"></div></div>
    <div class="strip-ticks"><span>00</span><span>06</span><span>12</span><span>18</span><span>24</span></div>
    <div class="legend" style="margin:8px 0 0"><span class="k"><i style="background:var(--s1)"></i>${esc(t('state.active'))}</span><span class="k"><i style="background:var(--idle)"></i>${esc(t('state.idle'))}</span><span class="k"><i style="background:var(--track);outline:1px solid var(--axis)"></i>${esc(t('state.locked'))}</span></div>
    <div class="fl-sec">${esc(t('fl.mostUsed'))}</div>
    ${rows || `<div class="muted" style="font-size:12px">${esc(t('fl.noApps'))}</div>`}
    ${hint}
    <div class="fl-foot"><button class="primary" id="opendash">${esc(t('fl.openDashboard'))}</button><button id="rep">${esc(t('fl.report'))} ▾</button><span class="sp"></span>${state}</div>`;

  document.getElementById('opendash').onclick = () => api('openDashboard');
  document.getElementById('rep').onclick = ev => popMenu(ev.currentTarget, [
    { label: t('rep.thisWeek'), fn: () => api('report', { kind: 'thisWeek' }) },
    { label: t('rep.lastWeek'), fn: () => api('report', { kind: 'lastWeek' }) },
    { label: t('rep.thisMonth'), fn: () => api('report', { kind: 'thisMonth' }) },
    { label: t('rep.lastMonth'), fn: () => api('report', { kind: 'lastMonth' }) },
    { label: t('rep.custom'), fn: () => api('report', { kind: 'custom' }) },
  ], false, true);
  const ni = document.getElementById('nameit');
  if (ni) ni.onclick = () => api('openDashboard', { rename: un[0].appIds });
  const dm = document.getElementById('dismiss');
  if (dm) dm.onclick = async () => { await api('dismissUnnamed', { path: un[0].path }); render(); };
  const rs = document.getElementById('resume');
  if (rs) rs.onclick = async () => { await api('resume'); render(); };
  setTimeout(() => api('resize', { height: Math.ceil(document.getElementById('root').getBoundingClientRect().height) }), 0);
}
document.addEventListener('keydown', e => { if (e.key === 'Escape') api('close'); });
boot(async () => {
  await render();
  // The window stays loaded between clicks: on every "refresh" re-read the language (it may have changed) and the numbers.
  window.addEventListener('host-refresh', async () => { const i = await api('init'); setLang(i.lang, i.strings); await render(); });
});
