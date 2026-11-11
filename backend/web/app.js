'use strict';
/* Tilt Guard web UI - vanilla JS, hash router. All URLs are relative so the app works at / and /guard/. */
const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const money = (v, d = 0) => v == null || isNaN(v) ? '—' : (v < 0 ? '-' : '') + '$' + Math.abs(v).toLocaleString('en-US', { minimumFractionDigits: d, maximumFractionDigits: d });
const pl = (v, d = 0) => v == null ? '<span class="muted">—</span>' : `<span class="num ${v > 0 ? 'pos' : v < 0 ? 'neg' : ''}">${v > 0 ? '+' : ''}${money(v, d)}</span>`;
const n2 = v => v == null ? '—' : (+v).toLocaleString('en-US', { maximumFractionDigits: 2 });
const S = { me: null, day: null, tz: 'America/New_York', timers: [], accts: [] };

async function api(path, opts = {}) {
  const init = { credentials: 'same-origin', method: opts.method || 'GET', headers: {} };
  if (opts.body !== undefined) { init.headers['Content-Type'] = 'application/json'; init.body = JSON.stringify(opts.body); }
  const r = await fetch(path, init);
  if (r.status === 401 && !path.startsWith('api/login')) { showLogin(); throw new Error('Sign in required'); }
  const j = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(j.detail || `${r.status} ${r.statusText}`);
  return j;
}
function toast(msg, bad = false) {
  const t = $('#toast'); t.textContent = msg; t.className = 'toast' + (bad ? ' bad' : ''); t.hidden = false;
  clearTimeout(toast._t); toast._t = setTimeout(() => t.hidden = true, bad ? 6000 : 2600);
}
const fmtT = (ts, o) => new Date(ts * 1000).toLocaleString('en-US', { timeZone: S.tz, hour12: false, ...o });
const tHM = ts => ts ? fmtT(ts, { hour: '2-digit', minute: '2-digit' }) : '—';
const tDay = ts => ts ? fmtT(ts, { month: 'short', day: 'numeric' }) : '—';
function left(until) { const s = Math.max(0, until - Date.now() / 1000); const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), ss = Math.floor(s % 60); return h ? `${h}h ${m}m` : m ? `${m}m ${ss}s` : `${ss}s`; }
function addDays(d, n) { const x = new Date(d + 'T12:00:00Z'); x.setUTCDate(x.getUTCDate() + n); return x.toISOString().slice(0, 10); }
const ago = ts => { const s = Date.now() / 1000 - ts; return s < 90 ? `${Math.round(s)}s ago` : s < 5400 ? `${Math.round(s / 60)} min ago` : s < 172800 ? `${Math.round(s / 3600)} h ago` : `${Math.round(s / 86400)} d ago`; };

/* ---------------------------------------------------------------- shell */
function showLogin() { $('#app').hidden = true; $('#login').hidden = false; $('#login-pw').focus(); }
$('#login-form').addEventListener('submit', async e => {
  e.preventDefault(); $('#login-err').textContent = '';
  try { await api('api/login', { method: 'POST', body: { password: $('#login-pw').value } }); $('#login-pw').value = ''; boot(); }
  catch (err) { $('#login-err').textContent = err.message; }
});
$('#logout-btn').onclick = async () => { await api('api/logout', { method: 'POST' }).catch(() => { }); showLogin(); };
$('#theme-btn').onclick = () => {
  const t = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = t; try { localStorage.setItem('tg-theme', t); } catch (e) { }
};
$('#menu-btn').onclick = () => $('#app').classList.toggle('menu');

async function refreshLockPill() {
  try {
    const st = await api('api/guard/status');
    const locked = Object.values(st.accounts).filter(a => a.locked);
    const pill = $('#lock-pill');
    if (locked.length) {
      const l = locked[0].lock;
      pill.innerHTML = `<i class="ic ic-lock"></i>LOCKED · unlocks in ${left(l.until)}`;
      pill.hidden = false; $('#nav-lock').hidden = false;
    } else { pill.hidden = true; $('#nav-lock').hidden = true; }
  } catch (e) { }
}

const ROUTES = {
  '': ['guard', 'Live guard', pageGuard], rules: ['rules', 'Rules', pageRules], history: ['history', 'Lock history', pageHistory],
  accounts: ['accounts', 'Accounts', pageAccounts], setup: ['setup', 'Setup', pageSetup],
};
async function route() {
  S.timers.forEach(t => clearInterval(t)); S.timers = [];
  const parts = location.hash.replace(/^#\/?/, '').split('/');
  const [key, title, fn] = ROUTES[parts[0]] || ROUTES[''];
  $$('#nav a[data-r]').forEach(a => a.classList.toggle('on', a.dataset.r === key));
  $('#title').textContent = title; $('#app').classList.remove('menu');
  const v = $('#view'); v.innerHTML = '<div class="muted">Loading…</div>';
  try { await fn(v, parts.slice(1)); } catch (e) { if (e.message !== 'Sign in required') v.innerHTML = `<div class="banner red">${esc(e.message)}</div>`; }
}
window.addEventListener('hashchange', route);

async function boot() {
  try { S.me = await api('api/me'); } catch (e) { return; }
  S.day = S.me.day; S.tz = S.me.settings.tz || S.tz; $('#ver').textContent = 'v' + S.me.version;
  $('#journal-link').hidden = !S.me.journal;
  $('#login').hidden = true; $('#app').hidden = false;
  S.accts = await api('api/accounts');
  route(); refreshLockPill();
  clearInterval(boot._t); boot._t = setInterval(refreshLockPill, 15000);
}

/* ---------------------------------------------------------------- live guard */
async function pageGuard(v) {
  v.innerHTML = `<div class="banner"><div style="flex:1"><b>Done for today?</b><div class="muted sm">Lock every account and this PC until ${S.me.settings.lock_until ? esc(S.me.settings.lock_until) + ' ET tomorrow morning' : 'the ' + esc(S.me.settings.reset_time) + ' ET reset'} — closes positions, blocks orders, trading sites and apps. No early unlock.</div></div>
    <button class="btn danger" id="g-done">Lock me out</button></div><div id="g-body" class="grid"></div>`;
  $('#g-done').onclick = async () => {
    const why = prompt('Lock out every account until the next session. Reason (optional):', 'Done for today');
    if (why === null) return;
    try { await api('api/guard/manual-lock', { method: 'POST', body: { reason: why || 'Done for today' } }); toast('Locked until the reset'); refreshLockPill(); }
    catch (e) { toast(e.message, true); }
  };
  const draw = async () => {
    let st; try { st = await api('api/guard/live'); } catch (e) { return; }
    const accts = Object.values(st.accounts).sort((a, b) => (b.online - a.online) || ((b.updated || 0) - (a.updated || 0)));
    const r = st.rules, p = st.pending || {}, b = st.bridge;
    const anyLock = accts.find(a => a.locked);
    $('#g-body').innerHTML = `
      ${anyLock ? `<div class="banner red"><i class="ic ic-lock" style="width:22px;height:22px;color:var(--red)"></i><div><b>Locked out</b> — ${esc(anyLock.lock.reason)}<div class="muted sm">Unlocks at ${tHM(anyLock.lock.until)} ET (in ${left(anyLock.lock.until)}). There is no early unlock.</div></div></div>` : ''}
      ${Object.keys(p).length ? `<div class="banner amber"><div><b>Queued rule changes</b> start on ${esc(Object.values(p)[0].effective_day)}: ${Object.entries(p).map(([sc, x]) => `${sc === 'default' ? '' : esc(sc) + ': '}${Object.entries(x.rules).map(([k, val]) => `${esc(k)} → ${val ?? 'off'}`).join(', ')}`).join(' · ')}</div></div>` : ''}
      <div class="grid g3">${accts.length ? accts.map(acctCard).join('') : '<div class="empty" style="grid-column:1/-1">No account has reported in the last 3 days. Open TradingView with the extension, or start NT8 with the add-on (see <a href="#/setup">Setup</a>).</div>'}</div>
      <div class="grid g2">
        <div class="card"><div class="card-h"><h2>Rules per account</h2><a href="#/rules" class="sm">Edit →</a></div>${rulesTable(accts)}</div>
        <div class="card"><div class="card-h"><h2>Today's log</h2><span class="muted xs">session ${esc(st.day)} · resets ${tHM(st.reset_at)} ET</span></div>
          <div class="log">${(st.events_today || []).slice(0, 40).map(logRow).join('') || '<div class="muted">Nothing yet today.</div>'}</div></div>
      </div>
      ${b.enabled ? `<p class="muted xs">Journal sync: ${b.last_error ? `<span class="neg">error — ${esc(b.last_error)}</span>` : b.last_ok ? `ok ${ago(b.last_ok)}` : 'starting'} · ${b.queued} fills waiting · ${b.pushed} sent since restart</p>` : ''}`;
  };
  await draw(); S.timers.push(setInterval(draw, 2500));
}
function detail(e) {
  if (!e.type.startsWith('rules_') || !e.detail.startsWith('{')) return esc(e.detail);
  try {
    const d = JSON.parse(e.detail), ch = d.now || d.later || {};
    const list = Object.entries(ch).map(([k, v]) => `${k.replace(/_/g, ' ')} → ${v ?? 'off'}`).join(', ');
    return esc(`${d.scope === 'default' ? 'All accounts' : d.scope}: ${list}${d.from ? ` (from ${d.from})` : ''}`);
  } catch (x) { return esc(e.detail); }
}
const logRow = e => `<div><span class="num muted">${tHM(e.ts)}</span><span class="chip ${['lock', 'native_lockout'].includes(e.type) ? 'lock' : ['blocked_order', 'flatten', 'cooldown', 'native_freeze', 'hp_error'].includes(e.type) ? 'warn' : ''}">${esc(e.type.replace(/_/g, ' '))}</span><span>${e.account ? `<span class="mono xs">${esc(e.account)}</span> ` : ''}${detail(e)}</span></div>`;
function meter(label, used, limit) {
  if (!limit) return '';
  const p = Math.max(0, Math.min(100, used / limit * 100));
  return `<div class="meter"><div class="lbl"><span>${label}</span><span class="num">${money(used)} / ${money(limit)}</span></div><div class="track ${p >= 90 ? 'crit' : p >= 60 ? 'hot' : ''}"><i style="width:${p}%"></i></div></div>`;
}
function acctCard(a) {
  const s = a.stats, r = a.rules, lk = a.lock, cd = a.cooldown;
  return `<div class="card acct-card ${a.locked ? 'locked' : cd || a.window ? 'cool' : ''}">
    <div class="acct-top"><div style="min-width:0"><div class="acct-name">${esc(a.label || a.name)}</div><div class="muted xs">${a.label ? esc(a.name) + ' · ' : ''}${esc(a.firm || '')}${a.firm ? ' · ' : ''}${esc(a.platform || '')}</div></div>
      <span class="xs muted" style="white-space:nowrap"><i class="dot ${a.online ? 'on' : ''}"></i> ${a.online ? 'live' : a.updated ? 'seen ' + tHM(a.updated) : 'offline'}</span></div>
    <div><div class="muted sm">Day P&L</div><div class="big">${pl(s.day_pl)}</div><div class="sm muted">open ${pl(s.open_pl)} · peak ${money(s.peak_pl)} · closed trades ${money(s.realized)}</div></div>
    ${lk ? `<div class="lockbox"><div class="t"><i class="ic ic-lock"></i>LOCKED</div><div class="sm">${esc(lk.reason)}</div><div class="muted xs">unlocks ${tHM(lk.until)} ET · in ${left(lk.until)}${lk.account !== a.name ? ` · triggered by ${esc(lk.account)}` : ''}</div></div>`
      : cd ? `<div class="lockbox cool"><div class="t">COOLDOWN</div><div class="sm">${esc(cd.reason)}</div><div class="muted xs">entries blocked for ${left(cd.until)}</div></div>`
        : a.window ? `<div class="lockbox cool"><div class="t">OUTSIDE WINDOW</div><div class="sm">${esc(a.window.reason)}</div></div>` : ''}
    ${meter('Daily loss used', Math.max(0, -s.day_pl), r.max_daily_loss)}
    ${meter('Profit target', Math.max(0, s.day_pl), r.profit_target)}
    <div class="stat-row"><div><div class="k">Trades</div><div class="v">${s.trades}${s.trades_left != null ? `<span class="muted xs"> / ${s.trades + s.trades_left}</span>` : ''}</div></div>
      <div><div class="k">Loss streak</div><div class="v">${s.streak}</div></div><div><div class="k">Position</div><div class="v">${n2(s.pos_abs)}</div></div>
      <div><div class="k">Balance</div><div class="v xs">${a.balance != null ? money(a.balance) : '—'}</div></div></div>
    ${a.native ? nativeLine(a.native) : ''}
    ${a.flatten ? `<div class="banner red sm">Flatten requested: ${esc(a.flatten_reason)}</div>` : ''}
    ${a.locked ? '' : `<div class="lockone"><select data-lkmin="${esc(a.name)}">${[['', 'until the reset'], ['30', '30 min'], ['60', '1 hour'], ['120', '2 hours'], ['240', '4 hours']].map(([v, t]) => `<option value="${v}"${(S.lkMin || {})[a.name] === v ? ' selected' : ''}>${t}</option>`).join('')}</select>
      <button class="btn danger sm" data-lkone="${esc(a.name)}" title="Lock only this account (closes its positions, blocks new orders). The other accounts keep trading. No early unlock.">Lock this account</button></div>`}
  </div>`;
}
// per-account lock: delegated so it survives the 2.5 s re-render
document.addEventListener('change', e => { const k = e.target && e.target.dataset && e.target.dataset.lkmin; if (k != null) (S.lkMin = S.lkMin || {})[k] = e.target.value; });
document.addEventListener('click', async e => {
  const b = e.target.closest && e.target.closest('[data-lkone]'); if (!b) return;
  const name = b.dataset.lkone, min = (S.lkMin || {})[name] || '';
  const why = prompt(`Lock ${name} ${min ? 'for ' + (min >= 60 ? min / 60 + ' h' : min + ' min') : 'until the next session'}?
Its positions are closed and new orders blocked. The other accounts keep trading. There is no early unlock.

Reason (optional):`, 'Done on this account');
  if (why === null) return;
  try { await api('api/guard/manual-lock', { method: 'POST', body: { account: name, minutes: min ? +min : null, reason: why || 'Done on this account' } }); toast(name + ' locked'); refreshLockPill(); }
  catch (err) { toast(err.message, true); }
});
function nativeLine(n) {
  const bits = [];
  if (n.daily_loss_limit) bits.push(`loss limit ${money(n.daily_loss_limit)}`);
  if (n.max_trades_per_day) bits.push(`max ${n.max_trades_per_day} trades`);
  if (n.trades_today != null) bits.push(`${n.trades_today} today`);
  bits.push(n.settings_locked ? `<b>frozen</b>${n.settings_locked_until ? ' until ' + esc(String(n.settings_locked_until).slice(11, 16)) + ' UTC' : ''}` : 'not frozen');
  if (n.lockout) bits.push(`<span class="neg">native lockout${n.lockout.ends_at ? ' until ' + esc(String(n.lockout.ends_at).slice(11, 16)) + ' UTC' : ''}</span>`);
  return `<div class="muted xs">HyperProp: ${bits.join(' · ')}</div>`;
}
function rulesTable(accts) {
  if (!accts.length) return '<div class="muted">No account yet.</div>';
  const rows = [['Daily loss limit', r => r.max_daily_loss && money(r.max_daily_loss)], ['Profit target', r => r.profit_target && money(r.profit_target)],
    ['Give-back from peak', r => r.giveback_pct && `${r.giveback_pct}%`], ['Max trades', r => r.max_trades], ['Losses in a row', r => r.max_consec_losses],
    ['Cooldown after loss', r => r.cooldown_after_loss && `${r.cooldown_after_loss} min`], ['Max loss / trade', r => r.max_trade_loss && money(r.max_trade_loss)],
    ['Max contracts', r => r.max_contracts], ['Trading window', r => r.window_start && `${r.window_start}–${r.window_end}`], ['Flatten when locked', r => r.flatten_on_lock ? 'yes' : '']];
  return `<div class="tbl-wrap"><table class="tbl"><thead><tr><th></th>${accts.map(a => `<th>${esc(a.label || a.name)}</th>`).join('')}</tr></thead><tbody>
    ${rows.map(([k, f]) => `<tr><td class="muted">${k}</td>${accts.map(a => { const x = f(a.rules || {}); return `<td class="${x ? 'num' : 'faint'}">${x ? esc(x) : 'off'}</td>`; }).join('')}</tr>`).join('')}
  </tbody></table></div>`;
}
function rulesSummary(r) {
  const rows = [['Daily loss limit', r.max_daily_loss && money(r.max_daily_loss)], ['Profit target', r.profit_target && money(r.profit_target)],
  ['Give-back from peak', r.giveback_pct && `${r.giveback_pct}% (after ${money(r.giveback_min_peak || 0)})`], ['Max trades', r.max_trades],
  ['Max losses in a row', r.max_consec_losses], ['Cooldown after loss', r.cooldown_after_loss && `${r.cooldown_after_loss} min`],
  ['Max loss per trade', r.max_trade_loss && money(r.max_trade_loss)], ['Max contracts', r.max_contracts],
  ['Trading window', r.window_start && `${r.window_start}–${r.window_end} ET`], ['Flatten when locked', r.flatten_on_lock ? 'yes' : 'no']];
  return `<div class="kv">${rows.map(([k, val]) => `<span class="k">${k}</span><span class="${val ? '' : 'faint'}">${val ? esc(val) : 'off'}</span>`).join('')}</div>`;
}

/* ---------------------------------------------------------------- history */
async function pageHistory(v) {
  const h = await api('api/history?days=90');
  const byDay = {};
  h.locks.filter(l => l.kind === 'hard').forEach(l => (byDay[l.day] = byDay[l.day] || []).push(l));
  const days = Object.keys(byDay).sort().reverse();
  v.innerHTML = `<div class="grid g2">
    <div class="card"><div class="card-h"><h2>Locked days (90 days)</h2><span class="chip">${days.length}</span></div>
      ${days.length ? `<div class="tbl-wrap"><table class="tbl"><thead><tr><th>Day</th><th>Time</th><th>Rule</th><th>Account</th><th>Reason</th></tr></thead><tbody>
      ${days.map(d => byDay[d].map(l => `<tr><td>${esc(d)}</td><td class="num">${tHM(l.started)}</td><td><span class="chip lock">${esc(l.rule.replace(/_/g, ' '))}</span></td><td class="mono xs">${esc(l.account)}</td><td style="white-space:normal">${esc(l.reason)}</td></tr>`).join('')).join('')}
      </tbody></table></div>` : '<div class="empty">No lockouts in the last 90 days.</div>'}</div>
    <div class="card"><div class="card-h"><h2>Guard events</h2></div><div class="log">${h.events.map(e => `<div><span class="num muted">${tDay(e.ts)} ${tHM(e.ts)}</span><span class="chip ${e.type === 'lock' ? 'lock' : ['blocked_order', 'flatten', 'cooldown'].includes(e.type) ? 'warn' : ''}">${esc(e.type.replace(/_/g, ' '))}</span><span>${e.account ? `<span class="mono xs">${esc(e.account)}</span> ` : ''}${detail(e)}</span></div>`).join('') || '<div class="muted">No events yet.</div>'}</div></div>
  </div>`;
}

/* ---------------------------------------------------------------- rules + settings */
async function pageRules(v) {
  const [R, st] = await Promise.all([api('api/rules'), api('api/settings')]);
  const list = R.account_list || [];
  if (!S.ruleScope || (S.ruleScope !== 'default' && !list.some(a => a.name === S.ruleScope))) S.ruleScope = list.length ? list[0].name : 'default';
  const scope = S.ruleScope;
  const cur = scope === 'default' ? R.default : (R.effective[scope] || R.default);
  const pend = (R.pending[scope] || {}).rules || {};
  const lz = R.loosen[scope] || { ok: R.loosen_now, why: R.loosen_why };
  const tabName = a => `${a.label || a.name}`;
  const input = d => {
    const val = cur[d.key];
    if (d.kind === 'bool') return `<label class="switch"><input type="checkbox" data-k="${d.key}" ${val ? 'checked' : ''}><i></i></label>`;
    if (d.kind === 'time') return `<input type="time" data-k="${d.key}" value="${val || ''}">`;
    return `<input type="number" min="0" step="any" data-k="${d.key}" value="${val ?? ''}" placeholder="off"><span>${d.unit}</span>`;
  };
  v.innerHTML = `
  <div class="seg" style="justify-self:start;flex-wrap:wrap">${list.map(a => `<button data-scope="${esc(a.name)}" class="${scope === a.name ? 'on' : ''}">${esc(tabName(a))}${R.loosen[a.name] && !R.loosen[a.name].ok ? ' 🔒' : ''}</button>`).join('')}<button data-scope="default" class="${scope === 'default' ? 'on' : ''}">Template for new accounts</button></div>
  <div class="banner ${lz.ok ? 'accent' : 'amber'}"><div><b>${scope === 'default' ? 'Template' : esc(scope)}: ${lz.ok ? 'you can change anything right now.' : 'stricter changes apply now; looser ones wait for the next session.'}</b><div class="sm muted">${esc(lz.why)} Each account has its own rules and locks on its own. Locks are never lifted early.</div></div></div>
  <div class="grid g-main">
    <div class="card"><div class="card-h"><h2>${scope === 'default' ? 'Template for new accounts' : 'Rules for ' + esc(tabName(list.find(a => a.name === scope) || { name: scope }))}</h2>
      ${scope !== 'default' ? `<span class="muted xs mono">${esc(scope)}</span>` : ''}</div>
      <p class="muted sm" style="margin-top:0">${scope === 'default' ? 'Used by any account the guard sees for the first time, until you give it its own rules.' : 'Empty = off. These rules only apply to this account; a breach locks only this account.'}</p>
      <div class="rules">${R.definitions.map(d => `<div class="rule ${pend[d.key] !== undefined ? 'pend' : ''}"><div><div class="t">${d.label}</div><div class="h">${d.help}${pend[d.key] !== undefined ? ` <span class="warn">→ ${pend[d.key] ?? 'off'} from ${esc(R.pending[scope].effective_day)}</span>` : ''}</div></div><div class="in">${input(d)}</div></div>`).join('')}</div>
      <div style="display:flex;gap:10px;margin-top:14px;align-items:center"><button class="btn primary" id="r-save">Save rules</button><span class="muted sm" id="r-msg"></span></div></div>
    <div class="grid" style="align-content:start">
      <div class="card"><div class="card-h"><h2>Lockout behaviour</h2></div><div class="grid" style="gap:12px">
        <label>When one account breaks a rule, lock<select id="s-scope"><option value="account" ${st.lock_scope === 'account' ? 'selected' : ''}>only that account</option><option value="group" ${st.lock_scope === 'group' ? 'selected' : ''}>accounts in the same group</option><option value="all" ${st.lock_scope === 'all' ? 'selected' : ''}>every account</option></select></label>
        <label>Daily reset (ET)<input type="time" id="s-reset" value="${esc(st.reset_time)}"></label>
        <label>Commission per contract per side (when the platform sends none), e.g. <code>MNQ=0.37</code><textarea id="s-comm" rows="4" placeholder="MNQ=0.37&#10;NQ=1.29">${esc(Object.entries(st.commissions || {}).map(([k, x]) => `${k}=${x}`).join('\n'))}</textarea></label>
        <div style="display:flex;gap:10px;align-items:center"><button class="btn" id="s-save">Save settings</button></div></div></div>
      <div class="card"><div class="card-h"><h2>PC lockout</h2><span class="muted xs">Chrome extension + Windows companion</span></div><div class="grid" style="gap:12px">
        <p class="muted sm" style="margin:0">Blocked on this PC when <b>every</b> account is locked, after <b>Done for today</b>, and during the scheduled lockouts below. One account locked alone keeps the PC usable for the others. Removing an item or weakening the schedule only works before your first trade of the day, with no lock active.</p>
        <div class="form-row"><label>Trading sites (one per line)<textarea id="d-sites" rows="4">${esc((st.block_sites || []).join('\n'))}</textarea></label>
          <label>Trading apps (.exe)<textarea id="d-apps" rows="4">${esc((st.block_apps || []).join('\n'))}</textarea></label></div>
        <label style="display:flex;flex-direction:row;align-items:center;gap:10px"><span class="switch"><input type="checkbox" id="d-dis" ${st.block_distractions ? 'checked' : ''}><i></i></span><span>Also block distractions</span></label>
        <div class="form-row"><label>Distraction sites<textarea id="d-dsites" rows="4">${esc((st.distraction_sites || []).join('\n'))}</textarea></label>
          <label>Distraction apps (.exe)<textarea id="d-dapps" rows="4">${esc((st.distraction_apps || []).join('\n'))}</textarea></label></div>
        <div class="form-row">
          <label>Lockouts last until (next morning)<span style="display:flex;gap:6px"><input type="time" id="d-until" value="${esc(st.lock_until || '')}">
            <select id="d-until-tz" style="width:auto">${[['Europe/Paris', 'Paris time'], ['America/New_York', 'New York time'], ['UTC', 'UTC']].map(([z, l]) => `<option value="${z}" ${(st.lock_until_tz || 'America/New_York') === z ? 'selected' : ''}>${l}</option>`).join('')}</select></span>
            <span class="xs faint">empty = until the ${esc(st.reset_time)} ET reset</span></label>
          <label>Daily auto-lockout at (ET, weekdays)<input type="time" id="d-auto" value="${esc(st.auto_lock_time || '')}"><span class="xs faint">empty = off</span></label>
        </div>
        <div class="form-row"><label style="display:flex;flex-direction:row;align-items:center;gap:10px"><span class="switch"><input type="checkbox" id="d-wk" ${st.weekend_lock ? 'checked' : ''}><i></i></span><span>Lock the weekend</span></label>
          <label>from Friday at (ET)<input type="time" id="d-wkfrom" value="${esc(st.weekend_lock_from || '12:00')}"><span class="xs faint">until Monday at the "last until" time</span></label></div>
        ${st.pending ? `<div class="banner amber sm">Queued from ${esc(st.pending.effective_day)}: lockouts until ${esc(st.pending.values.lock_until || 'the reset')} ${esc((st.pending.values.lock_until_tz || '').split('/').pop().replace('_', ' '))} time${st.pending.values.auto_lock_time !== st.auto_lock_time ? ', auto-lock ' + esc(st.pending.values.auto_lock_time || 'off') : ''}. A running lock is never shortened.</div>` : ''}
        <div style="display:flex;gap:10px;align-items:center"><button class="btn" id="d-save">Save PC lockout</button></div></div></div>
      <div class="card"><h2 style="margin-bottom:8px">How locks work</h2><ol class="steps sm muted">
        <li>The TradingView extension and the NT8 add-on report P&L, positions and fills every ~1.5 s.</li>
        <li>When a rule breaks, the server writes a lock that lasts until the reset (or the cooldown ends).</li>
        <li>While locked, new orders are rejected before they reach the broker, positions are flattened (if enabled), and the sites above are blocked.</li>
        <li>Closing a position is always allowed.</li></ol></div>
    </div>
  </div>`;
  $$('[data-scope]', v).forEach(b => b.onclick = () => { S.ruleScope = b.dataset.scope; pageRules(v); });
  $('#r-save').onclick = async () => {
    const rules = {};
    $$('[data-k]', v).forEach(i => { rules[i.dataset.k] = i.type === 'checkbox' ? i.checked : i.value; });
    try {
      const res = await api('api/rules', { method: 'PUT', body: { scope, rules } });
      const a = Object.keys(res.applied).length, p = Object.keys(res.pending).length;
      toast(a || p ? `${a} change(s) active now${p ? `, ${p} queued for ${res.pending_from}` : ''}` : 'No change');
      pageRules(v);
    } catch (e) { toast(e.message, true); }
  };
  const lines = id => $(id).value.split('\n').map(x => x.trim()).filter(Boolean);
  $('#d-save').onclick = async () => {
    try {
      await api('api/settings', { method: 'PUT', body: { block_sites: lines('#d-sites'), block_apps: lines('#d-apps'), block_distractions: $('#d-dis').checked,
        distraction_sites: lines('#d-dsites'), distraction_apps: lines('#d-dapps'), lock_until: $('#d-until').value, lock_until_tz: $('#d-until-tz').value, auto_lock_time: $('#d-auto').value,
        weekend_lock: $('#d-wk').checked, weekend_lock_from: $('#d-wkfrom').value || '12:00' } });
      const res = await api('api/settings');
      toast(res.pending ? `Saved — weaker settings start ${res.pending.effective_day} (a running lock is never shortened)` : 'PC lockout saved'); pageRules(v);
    } catch (e) { toast(e.message, true); }
  };
  $('#s-save').onclick = async () => {
    const comm = {}; $('#s-comm').value.split('\n').forEach(l => { const [k, x] = l.split('='); if (k && x && !isNaN(+x)) comm[k.trim().toUpperCase()] = +x; });
    try {
      await api('api/settings', { method: 'PUT', body: { lock_scope: $('#s-scope').value, reset_time: $('#s-reset').value, block_sites: $('#s-sites').value.split('\n'), commissions: comm } });
      toast('Settings saved');
    } catch (e) { toast(e.message, true); }
  };
}

/* ---------------------------------------------------------------- accounts */
async function pageAccounts(v) {
  const rows = S.accts = await api('api/accounts');
  const list = rows.filter(r => S.showHidden || !r.hidden);
  v.innerHTML = `<div class="card"><div class="card-h"><h2>${rows.length} accounts</h2><label style="display:flex;flex-direction:row;align-items:center;gap:8px"><input type="checkbox" id="a-hid" ${S.showHidden ? 'checked' : ''} style="width:auto"> show hidden</label></div>
    <p class="muted sm" style="margin-top:0">Accounts appear when TradingView or NT8 reports them. Put a Replikanto leader and its followers in the same <b>group</b> if the lock scope is "group". <b>Journal</b> = send this account's fills to the journal (new accounts start off — tick them to journal).</p>
    <div class="tbl-wrap"><table class="tbl"><thead><tr><th>Account</th><th>Label</th><th>Firm</th><th>Group</th><th>Platform</th><th class="r">Trades</th><th class="r">Net</th><th>Last day</th><th>Journal</th><th>Hide</th></tr></thead><tbody>
    ${list.map(r => `<tr data-n="${esc(r.name)}"><td class="mono xs">${esc(r.name)}${Object.keys(r.custom_rules || {}).length ? ' <span class="chip acc">custom rules</span>' : ''}</td>
      <td><input data-f="label" value="${esc(r.label)}" style="min-width:120px"></td><td><input data-f="firm" value="${esc(r.firm)}" style="min-width:110px"></td><td><input data-f="grp" value="${esc(r.grp)}" style="min-width:90px"></td>
      <td>${esc(r.platform || '')}</td><td class="r num">${r.n}</td><td class="r">${pl(r.net)}</td><td>${r.last_day || '—'}</td>
      <td><input type="checkbox" data-f="journal" ${r.journal ? 'checked' : ''} style="width:auto">${r.journal && r.unsent ? ` <span class="muted xs">${r.unsent} queued</span>` : ''}</td>
      <td><input type="checkbox" data-f="hidden" ${r.hidden ? 'checked' : ''} style="width:auto"></td></tr>`).join('')}
    </tbody></table></div></div>`;
  $('#a-hid').onchange = e => { S.showHidden = e.target.checked; pageAccounts(v); };
  $$('tr[data-n] [data-f]', v).forEach(i => i.onchange = async () => {
    const name = i.closest('tr').dataset.n;
    try { await api('api/accounts/' + encodeURIComponent(name), { method: 'PATCH', body: { [i.dataset.f]: i.type === 'checkbox' ? i.checked : i.value } }); toast('Saved'); }
    catch (e) { toast(e.message, true); }
  });
}

/* ---------------------------------------------------------------- setup */
async function pageSetup(v) {
  const d = await api('api/device');
  v.innerHTML = `
  <div class="card"><div class="card-h"><h2>Device key</h2></div>
    <p class="muted sm" style="margin-top:0">The extension and the NT8 add-on use this key to report to <b class="mono">${esc(d.server)}</b>. The key can report trades and read lock status; it can't open the journal.</p>
    <div style="display:flex;gap:8px;max-width:640px"><input id="k" type="password" readonly value="${esc(d.key)}" class="mono"><button class="btn" id="k-show">Show</button><button class="btn" id="k-copy">Copy</button></div>
    <button class="btn ghost sm danger" id="k-rot" style="margin-top:8px">Rotate key (disconnects devices)</button></div>
  <div class="grid g2">
    <div class="card"><div class="card-h"><h2>TradingView extension</h2><a class="btn primary sm" href="downloads/tiltguard-extension.zip">Download .zip</a></div><ol class="steps sm">
      <li>Unzip it to a folder you'll keep (e.g. <code>Documents\\tiltguard-extension</code>).</li>
      <li>Chrome → <code>chrome://extensions</code> → turn on <b>Developer mode</b> → <b>Load unpacked</b> → pick the folder.</li>
      <li>Click the Tilt Guard icon → server <code>${esc(d.server)}</code> + your device key → Save.</li>
      <li>Reload TradingView and connect Tradovate. The guard bar appears at the top of the chart.</li></ol>
      <p class="muted xs">It reads the Tradovate panel's own data (account, P&L, positions, fills), rejects new orders while you're locked, and closes positions when a lock says flatten.</p></div>
    <div class="card"><div class="card-h"><h2>NinjaTrader 8 add-on</h2><a class="btn primary sm" href="downloads/TiltguardAddOn.cs">Download .cs</a></div><ol class="steps sm">
      <li>Copy <code>TiltguardAddOn.cs</code> into <code>Documents\\NinjaTrader 8\\bin\\Custom\\AddOns\\</code>.</li>
      <li>Create <code>Documents\\NinjaTrader 8\\tiltguard.txt</code>:<pre class="code">url=${esc(d.server)}\nkey=${esc(d.key)}</pre></li>
      <li>NT8 → New → NinjaScript Editor → <b>F5</b> to compile. Control Center → New → <b>Tilt Guard</b> shows the status window.</li>
      <li>It watches every connected account (Replikanto leader and followers), cancels new entries and flattens when locked.</li></ol></div>
  </div>
  <div class="card"><div class="card-h"><h2>Windows companion (taskbar strip + PC lockout)</h2><a class="btn primary sm" href="downloads/TiltGuard.exe">Download TiltGuard.exe</a></div><ol class="steps sm">
      <li>Save <code>TiltGuard.exe</code> anywhere, then run <code>TiltGuard.exe install</code> (or double-click it and use the strip menu). Windows asks for admin once.</li>
      <li>Paste the server <code>${esc(d.server)}</code> and your device key when asked.</li>
      <li>The strip appears in the taskbar next to the prayer-times widget: P&L per account, loss left, trades, lock countdown. Left-click = panel, right-click = menu (Done for today, position).</li>
      <li>The <b>Tilt Guard lockout</b> service then blocks the sites and apps listed on the Rules page whenever a lock is active — Chrome/Edge/Brave policies + hosts file; NinjaTrader is only closed once every account is flat. It can't be uninstalled while a lock is active.</li></ol></div>
  <div class="card"><div class="card-h"><h2>Import your NT8 history</h2><a class="btn sm" href="downloads/import_nt8.py">Download script</a></div>
    <p class="muted sm" style="margin-top:0">Reads every past execution from NT8's database (read-only) and sends it here; the guard forwards it to the journal. Safe to re-run.</p>
    <pre class="code">python import_nt8.py --server ${esc(d.server)} --key ${esc(d.key)}</pre></div>`;
  $('#k-show').onclick = () => { const k = $('#k'); k.type = k.type === 'password' ? 'text' : 'password'; };
  $('#k-copy').onclick = () => navigator.clipboard.writeText($('#k').value).then(() => toast('Key copied'));
  $('#k-rot').onclick = async () => { if (!confirm('Rotate the key? The extension and NT8 add-on stop reporting until you paste the new key.')) return; await api('api/device/rotate', { method: 'POST' }); pageSetup(v); };
}

boot();
