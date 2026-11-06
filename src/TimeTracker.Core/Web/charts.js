'use strict';
/* Renders a StatsDto into tiles, charts and the apps table. Used by the dashboard and by exported reports. */

const SLOT = g => (g.slot > 0 ? `var(--s${g.slot})` : 'var(--s-other)');
const TIPATTR = html => `data-tip="${esc(html)}"`;

function periodLabel(S) {
  const a = parseDay(S.from), b = parseDay(S.to);
  switch (S.period) {
    case 'day': return fmtDay(a, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' });
    case 'month': return fmtDay(a, { month: 'long', year: 'numeric' });
    case 'year': return String(a.getFullYear());
    default: return `${fmtDay(a, { day: 'numeric', month: 'short' })} – ${fmtDay(b, { day: 'numeric', month: 'short', year: 'numeric' })}`;
  }
}

function bucketLabel(S, b, long) {
  if (S.granularity === 'month') return fmtDay(parseDay(b.key + '-01'), { month: 'long', year: 'numeric' });
  return fmtDay(parseDay(b.key), long ? { weekday: 'long', day: 'numeric', month: 'long' } : { weekday: 'short', day: 'numeric', month: 'short' });
}

// ---------- tiles ----------
function tilesHtml(S) {
  const T = S.tiles, grp = id => S.groups.find(x => x.id === id);
  const vs = { day: 'tile.vsDay', week: 'tile.vsWeek', month: 'tile.vsMonth', year: 'tile.vsYear', range: 'tile.vsRange' }[S.period];
  const delta = T.prevActiveSec > 0 ? `${Math.round((T.activeSec / T.prevActiveSec - 1) * 100) >= 0 ? '+' : ''}${Math.round((T.activeSec / T.prevActiveSec - 1) * 100)}% ${t(vs)}` : t('tile.noPrev');
  const top = grp(T.topGroupId), lg = grp(T.longestGroupId);
  const tiles = [];
  tiles.push([t('tile.active'), dur(T.activeSec), delta]);
  if (S.period === 'day') {
    tiles.push([t('tile.idle'), dur(T.idleSec), t('tile.idleSub')]);
  } else {
    const busiest = S.buckets.slice().sort((a, b) => b.active - a.active)[0];
    tiles.push([t('tile.avg'), dur(T.avgDaySec), busiest && busiest.active > 0 ? t('tile.busiest', bucketLabel(S, busiest), dur(busiest.active)) : '']);
  }
  tiles.push([t('tile.top'), top ? esc(top.name) : '–', top ? t('tile.topSub', dur(top.fg), T.activeSec ? Math.round(top.fg / T.activeSec * 100) : 0) : '']);
  tiles.push([t('tile.longest'), T.longestSec > 0 ? dur(T.longestSec) : '–',
    T.longestSec > 0 ? `${fmtDT(T.longestStartMs, S.period === 'day' ? { hour: '2-digit', minute: '2-digit' } : { weekday: 'short', day: 'numeric', month: 'short' })}${lg ? ' · ' + esc(lg.name) : ''}` : '']);
  return tiles.map(([l, v, d]) => `<div class="card tile"><div class="lab">${esc(l)}</div><div class="val" title="${esc(String(v).replace(/<[^>]+>/g, ''))}">${v}</div><div class="dl">${d}</div></div>`).join('');
}

// ---------- stacked columns ----------
function seriesOf(S) {
  const top = S.groups.filter(g => g.slot > 0).sort((a, b) => a.slot - b.slot);
  const others = S.groups.filter(g => g.slot === 0 && g.fg > 0);
  const series = top.map(g => ({ id: g.id, name: g.name, col: SLOT(g), ids: [g.id] }));
  if (others.length) series.push({ id: 'other', name: t(others.length === 1 ? 'chart.other1' : 'chart.other', others.length), col: 'var(--s-other)', ids: others.map(o => o.id) });
  return series;
}

function niceStep(max) {
  const steps = [300, 600, 900, 1800, 3600, 7200, 10800, 14400, 21600, 28800, 43200, 86400, 172800, 360000, 720000, 1800000, 3600000];
  return steps.find(s => max / s <= 4) || 3600000;
}
function axisLabel(v, step) { return step < 3600 ? t('fmt.axisM', Math.round(v / 60)) : t('fmt.axisH', +(v / 3600).toFixed(v % 3600 ? 1 : 0)); }

function columnsHtml(S) {
  const series = seriesOf(S);
  const n = S.buckets.length;
  const vals = S.buckets.map(b => series.map(s => s.ids.reduce((a, id) => a + (b.fg[id] || 0), 0)));
  const totals = vals.map(v => v.reduce((a, x) => a + x, 0));
  const maxTotal = Math.max(1, ...totals);
  const step = niceStep(maxTotal * 1.02), yMax = Math.ceil(maxTotal * 1.02 / step) * step;

  const W = 560, H = 270, L = 44, R = 8, T = 10, B = 26;
  const pw = W - L - R, ph = H - T - B, slot = pw / n, cw = Math.min(24, Math.max(3, slot - 3)), gap = 2, rad = 4;
  const y = v => T + ph - v / yMax * ph;
  let s = `<svg viewBox="0 0 ${W} ${H}" role="img" aria-label="${esc(t('chart.title'))}">`;
  for (let v = 0; v <= yMax + 1; v += step) {
    s += `<line x1="${L}" x2="${W - R}" y1="${y(v)}" y2="${y(v)}" stroke="${v === 0 ? 'var(--axis)' : 'var(--grid)'}" stroke-width="1"/>`;
    s += `<text x="${L - 8}" y="${y(v) + 4}" text-anchor="end" font-size="11" fill="var(--muted)">${axisLabel(v, step)}</text>`;
  }
  const every = Math.ceil(n / 8);
  S.buckets.forEach((b, i) => {
    const cx = L + slot * (i + .5);
    let lab = '';
    if (S.granularity === 'month') lab = fmtDay(parseDay(b.key + '-01'), { month: 'short' });
    else if (n <= 8) lab = fmtDay(parseDay(b.key), { weekday: 'short', day: 'numeric' });
    else if (i % every === 0) lab = String(parseDay(b.key).getDate());
    if (lab) s += `<text x="${cx}" y="${H - 8}" text-anchor="middle" font-size="11" fill="var(--muted)">${esc(lab)}</text>`;
    let acc = 0;
    const parts = series.map((sr, k) => ({ sr, v: vals[i][k] })).filter(p => p.v >= 1);
    parts.forEach((p, pi) => {
      const y0 = y(acc), y1 = y(acc + p.v); acc += p.v;
      const isTop = pi === parts.length - 1;
      const top = y1, h = Math.max(y0 - y1 - (isTop ? 0 : gap), 1.5);
      const x = cx - cw / 2, r = isTop ? Math.min(rad, h / 2, cw / 2) : 0;
      const d = `M${x},${top + h} V${top + r} ${r ? `Q${x},${top} ${x + r},${top}` : ''} H${x + cw - r} ${r ? `Q${x + cw},${top} ${x + cw},${top + r}` : ''} V${top + h} Z`;
      s += `<path d="${d}" fill="${p.sr.col}" pointer-events="none"/>`;
      const tip = `<div class="t">${esc(bucketLabel(S, b, true))}</div><div class="r"><i style="background:${p.sr.col}"></i>${esc(p.sr.name)}<b>${dur(p.v)}</b></div>` +
        `<div class="m">${totals[i] ? Math.round(p.v / totals[i] * 100) : 0}% · ${esc(t('tip.ofActive', dur(totals[i])))}</div>`;
      s += `<rect class="seg-hit" ${TIPATTR(tip)} x="${cx - Math.max(cw, slot - 6) / 2}" y="${y1}" width="${Math.max(cw, slot - 6)}" height="${Math.max(y0 - y1, 6)}" fill="transparent" tabindex="0" role="img" aria-label="${esc(bucketLabel(S, b, true) + ', ' + p.sr.name + ', ' + dur(p.v))}"/>`;
    });
  });
  s += '</svg>';
  const legend = series.map(sr => `<span class="k"><i style="background:${sr.col}"></i>${esc(sr.name)}</span>`).join('');
  const rows = S.buckets.map((b, i) => `<tr><td>${esc(bucketLabel(S, b, true))}</td>${vals[i].map(v => `<td class="num">${v >= 1 ? dur(v) : '–'}</td>`).join('')}<td class="num"><b>${dur(totals[i])}</b></td></tr>`).join('');
  return `<div class="card-head"><div><h3>${esc(t('chart.title'))}</h3><div class="sub">${esc(t('chart.sub'))}</div></div><button id="tblbtn">${esc(t('chart.viewTable'))}</button></div>
    <div id="chartview"><div class="legend">${legend}</div>${s}</div>
    <div id="tableview" class="hidden"><table><thead><tr><th>${esc(t('table.day'))}</th>${series.map(sr => `<th class="num">${esc(sr.name)}</th>`).join('')}<th class="num">${esc(t('table.total'))}</th></tr></thead><tbody>${rows}</tbody></table></div>`;
}

// ---------- weekday x hour heatmap ----------
function heatHtml(S) {
  const bin = m => (m <= 0.05 ? 0 : m < 10 ? 1 : m < 20 ? 2 : m < 30 ? 3 : m < 40 ? 4 : 5);
  const wd = Array.from({ length: 7 }, (_, i) => fmtDay(new Date(2024, 0, 1 + i), { weekday: 'short' }));
  let h = '<span></span>' + Array.from({ length: 24 }, (_, i) => `<span class="hh">${i % 3 === 0 ? pad2(i) : ''}</span>`).join('');
  for (let d = 0; d < 7; d++) {
    h += `<span class="dl">${esc(wd[d])}</span>`;
    for (let k = 0; k < 24; k++) {
      const m = S.heat[d][k];
      const tip = `<div class="t">${esc(wd[d])}, ${pad2(k)}:00–${pad2((k + 1) % 24)}:00</div><div class="m">${m > 0.05 ? esc(t(S.period === 'week' || S.period === 'day' ? 'heat.tipMin' : 'heat.tipAvg', Math.round(m))) : esc(t('heat.none'))}</div>`;
      h += `<div class="cell" ${TIPATTR(tip)} tabindex="0" style="background:var(--h${bin(m)})"></div>`;
    }
  }
  const ramp = `<span>0</span>${[1, 2, 3, 4, 5].map(b => `<i style="background:var(--h${b})"></i>`).join('')}<span>${esc(t('heat.minPerHour'))}</span>`;
  return `<h3>${esc(t('heat.title'))}</h3><div class="sub">${esc(t(S.period === 'week' ? 'heat.subWeek' : 'heat.subAvg'))}</div><div class="heat">${h}</div><div class="ramp">${ramp}</div>`;
}

// ---------- day timeline ----------
function timelineHtml(S) {
  const TL = S.timeline, D = TL.daySeconds, p = x => x / D * 100;
  const stateName = st => t('state.' + st);
  const clock = sec => fmtDT(TL.dayStartMs + sec * 1000, { hour: '2-digit', minute: '2-digit' });
  const dev = TL.device.map(sg => {
    const cls = sg.state === 'untracked' ? 'seg hatch' : 'seg';
    const bg = sg.state === 'active' ? 'var(--h3)' : sg.state === 'idle' ? 'var(--idle)' : 'transparent';
    const tip = `<div class="t">${esc(stateName(sg.state))}</div><div class="m">${clock(sg.s)} – ${clock(sg.e)} · ${dur(sg.e - sg.s)}</div>`;
    return `<div class="${cls}" ${TIPATTR(tip)} tabindex="0" style="left:${p(sg.s)}%;width:max(2px,${p(sg.e - sg.s)}%);background:${bg}"></div>`;
  }).join('');
  const grp = id => S.groups.find(x => x.id === id);
  const fg = TL.fg.map(sg => {
    const g = grp(sg.g); if (!g) return '';
    const tip = `<div class="t">${esc(g.name)}</div><div class="m">${clock(sg.s)} – ${clock(sg.e)} · ${dur(sg.e - sg.s)}</div>`;
    return `<div class="seg" ${TIPATTR(tip)} tabindex="0" style="left:${p(sg.s)}%;width:max(2px,${p(sg.e - sg.s)}%);background:${SLOT(g)}"></div>`;
  }).join('');
  const ticks = Array.from({ length: 9 }, (_, i) => `<span style="left:${i * 12.5}%">${pad2(i * 3 % 24 || (i === 8 ? 24 : 0))}</span>`).join('');
  const series = seriesOf(S);
  const legend = `<div class="legend"><span class="k"><i style="background:var(--h3)"></i>${esc(t('state.active'))}</span><span class="k"><i style="background:var(--idle)"></i>${esc(t('state.idle'))}</span>` +
    `<span class="k"><i style="background:var(--track);outline:1px solid var(--axis)"></i>${esc(t('state.locked'))}</span><span class="k"><i class="hatch"></i>${esc(t('state.untracked'))}</span>` +
    series.map(sr => `<span class="k"><i style="background:${sr.col}"></i>${esc(sr.name)}</span>`).join('') + '</div>';
  return `<h3>${esc(t('tl.title'))}</h3><div class="sub">${esc(t('tl.sub'))}</div>${legend}
    <div class="tl-row"><span class="lab">${esc(t('tl.device'))}</span><div class="tl-bar">${dev}</div></div>
    <div class="tl-row"><span class="lab">${esc(t('tl.inFront'))}</span><div class="tl-bar">${fg}</div></div>
    <div class="tl-axis"><span></span><div class="ticks">${ticks}</div></div>`;
}

// ---------- apps table ----------
function appsHtml(S, ctx) {
  const ro = !!ctx.readOnly;
  const maxOpen = Math.max(1, ...S.groups.map(g => Math.max(g.open, g.fg)));
  const rows = S.groups.map(g => {
    const nameHtml = g.unnamed
      ? `<span class="unnamed">${esc(g.exe)}</span>${ro ? '' : `<button class="namebtn" data-act="rename" data-g="${esc(g.id)}">${esc(t('apps.nameIt'))}</button>`}`
      : `${esc(g.name)}${g.custom ? `<span class="tag" title="${esc(t('apps.customTip'))}">✎ ${esc(t('apps.custom'))}</span>` : ''}`;
    const second = g.unnamed ? esc(t('apps.noName')) : esc(g.exe);
    const last = g.lastMs ? fmtDT(g.lastMs, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' }) : '–';
    return `<tr data-g="${esc(g.id)}" ${ro ? '' : 'class="click"'}>
      <td><div class="appcell"><span class="appico">${esc((g.unnamed ? '?' : (g.name[0] || '?')).toUpperCase())}</span><div class="appname"><div>${nameHtml}</div><div class="exe" title="${esc(g.path)}">${second}</div></div></div></td>
      <td><div class="tbar"><div class="open" style="width:${g.open / maxOpen * 100}%"></div><div class="fg" style="width:${g.fg / maxOpen * 100}%"></div></div></td>
      <td class="num">${dur(g.fg)}</td><td class="num">${dur(g.open)}</td><td class="num">${g.sessions}</td><td class="num">${esc(last)}</td>
      ${ro ? '' : `<td><button class="iconbtn" data-act="rename" data-g="${esc(g.id)}" title="${esc(t('apps.rename'))}" aria-label="${esc(t('apps.rename') + ': ' + g.name)}">✎</button></td>`}</tr>`;
  }).join('');
  return `<div class="card-head"><div><h3>${esc(t('apps.title'))}</h3><div class="sub">${esc(t(ro ? 'apps.subReport' : 'apps.sub'))}</div></div></div>
    <table><thead><tr><th>${esc(t('apps.app'))}</th><th style="width:30%">${esc(t('apps.time'))}</th><th class="num">${esc(t('apps.inFront'))}</th><th class="num">${esc(t('apps.open'))}</th><th class="num">${esc(t('apps.sessions'))}</th><th class="num">${esc(t('apps.lastUsed'))}</th>${ro ? '' : '<th style="width:34px"></th>'}</tr></thead><tbody>${rows}</tbody></table>
    <div class="legend" style="margin:10px 0 0"><span class="k"><i class="bar" style="background:var(--s1)"></i>${esc(t('apps.inFront'))}</span><span class="k"><i class="bar" style="background:var(--open-track)"></i>${esc(t('apps.open'))}</span></div>`;
}

/** ctx: { readOnly, onRename(group), onSessions(group) } */
function renderStats(root, S, ctx) {
  ctx = ctx || {};
  if (!S.hasData || (S.groups.length === 0 && S.tiles.activeSec === 0 && S.tiles.idleSec === 0)) {
    root.innerHTML = `<div class="card empty">${esc(t('empty'))}</div>`;
    return;
  }
  let h = `<div class="tiles">${tilesHtml(S)}</div>`;
  if (S.period === 'day' && S.timeline) h += `<div class="card mb">${timelineHtml(S)}</div>`;
  else h += `<div class="row2"><div class="card chart">${columnsHtml(S)}</div><div class="card">${heatHtml(S)}</div></div>`;
  h += `<div class="card">${appsHtml(S, ctx)}</div>`;
  root.innerHTML = h;

  const tb = root.querySelector('#tblbtn');
  if (tb) tb.onclick = () => {
    const showTable = root.querySelector('#tableview').classList.contains('hidden');
    root.querySelector('#tableview').classList.toggle('hidden', !showTable);
    root.querySelector('#chartview').classList.toggle('hidden', showTable);
    tb.textContent = t(showTable ? 'chart.viewChart' : 'chart.viewTable');
  };
  const grp = id => S.groups.find(g => g.id === id);
  root.querySelectorAll('[data-act="rename"]').forEach(b => b.onclick = ev => { ev.stopPropagation(); ctx.onRename && ctx.onRename(grp(b.dataset.g)); });
  root.querySelectorAll('tr.click').forEach(r => r.onclick = () => ctx.onSessions && ctx.onSessions(grp(r.dataset.g)));
}
