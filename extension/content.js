/* Tilt Guard - content script (isolated world) on TradingView.
 * Relays the page hook's reports to the background worker, hands the guard status back to the
 * hook, and draws the guard bar + lock overlay. */
(() => {
  const VERSION = chrome.runtime.getManifest().version;
  let status = null, active = null, collapsedLock = null;

  const toHook = (type, data) => window.postMessage({ __tg: 'content', type, data }, location.origin);
  let orphaned = false;   // the extension was reloaded / updated after this tab opened
  const send = msg => new Promise(res => {
    try {
      if (!chrome.runtime || !chrome.runtime.id) throw new Error('context invalidated');
      chrome.runtime.sendMessage(msg, r => { void chrome.runtime.lastError; res(r); });
    } catch (e) { orphaned = true; draw(); res(null); }
  });

  function apply(st) {
    if (!st || !st.accounts) return;
    status = st;
    toHook('guard', st);
    draw();
  }

  // fail-safe: give the hook the last known status before TradingView loads the broker
  chrome.storage.local.get(['guard'], r => { if (r.guard) apply(r.guard); });

  window.addEventListener('message', async ev => {
    if (ev.source !== window || !ev.data || ev.data.__tg !== 'inject') return;
    const { type, data } = ev.data;
    if (type === 'report') {
      active = data.account.name;
      const r = await send({ type: 'report', data });
      if (r && r.status) apply(r.status);
      else draw();
    } else if (type === 'event') {
      send({ type: 'event', data });
    } else if (type === 'blocked') {
      flash(`Order rejected — ${data.reason}`);
    }
  });

  setInterval(async () => { const r = await send({ type: 'status' }); if (r && r.status) apply(r.status); }, 5000);
  setInterval(draw, 1000);

  /* ------------------------------------------------ UI */
  const css = `
  #tg-bar{position:fixed;top:6px;left:50%;transform:translateX(-50%);z-index:2147483000;display:flex;align-items:center;gap:10px;
    font:12px/1.3 Inter,system-ui,sans-serif;color:#e7eaf1;background:rgba(18,21,28,.94);border:1px solid #2a3040;border-radius:999px;
    padding:5px 12px 5px 8px;box-shadow:0 6px 24px rgba(0,0,0,.35);cursor:pointer;user-select:none;backdrop-filter:blur(6px)}
  #tg-bar .d{width:9px;height:9px;border-radius:50%;background:#2bd67b;box-shadow:0 0 0 3px rgba(43,214,123,.18)}
  #tg-bar.warn .d{background:#f5a524;box-shadow:0 0 0 3px rgba(245,165,36,.2)} #tg-bar.lock .d{background:#f25672;box-shadow:0 0 0 3px rgba(242,86,114,.25)}
  #tg-bar.off .d{background:#5b6376;box-shadow:none}
  #tg-bar.lock{border-color:#f25672} #tg-bar b{font-weight:600} #tg-bar .m{color:#8b93a7}
  #tg-bar .n{font-family:"JetBrains Mono",ui-monospace,monospace;font-weight:600} .tg-pos{color:#2bd67b} .tg-neg{color:#f25672}
  #tg-over{position:fixed;inset:0;z-index:2147483600;background:rgba(8,9,12,.78);display:grid;place-items:center;font:14px/1.5 Inter,system-ui,sans-serif;color:#e7eaf1;backdrop-filter:blur(3px)}
  #tg-over .c{width:min(460px,calc(100vw - 32px));background:#12151c;border:1px solid #f25672;border-radius:16px;padding:26px;text-align:center;box-shadow:0 20px 60px rgba(0,0,0,.5)}
  #tg-over h2{margin:6px 0 8px;font-size:22px;color:#f25672;letter-spacing:.02em} #tg-over .r{font-size:15px;margin-bottom:14px}
  #tg-over .t{font-family:"JetBrains Mono",ui-monospace,monospace;font-size:30px;font-weight:600;margin:8px 0 2px}
  #tg-over .m{color:#8b93a7;font-size:12px} #tg-over button{margin-top:16px;background:#1f2430;color:#e7eaf1;border:1px solid #2a3040;border-radius:8px;padding:8px 14px;font:inherit;cursor:pointer}
  #tg-over .lk{width:44px;height:44px;margin:0 auto;border-radius:12px;background:rgba(242,86,114,.14);display:grid;place-items:center;font-size:22px}
  #tg-toast{position:fixed;top:44px;left:50%;transform:translateX(-50%);z-index:2147483001;background:#f25672;color:#fff;font:600 12px Inter,system-ui,sans-serif;
    padding:7px 12px;border-radius:8px;box-shadow:0 6px 20px rgba(0,0,0,.35)}
  #tg-pop{position:fixed;top:40px;left:50%;transform:translateX(-50%);z-index:2147483002;width:320px;background:#12151c;color:#e7eaf1;border:1px solid #2a3040;border-radius:12px;
    padding:12px 14px;font:12px/1.5 Inter,system-ui,sans-serif;box-shadow:0 12px 40px rgba(0,0,0,.45)}
  #tg-pop .row{display:flex;justify-content:space-between;gap:8px;padding:3px 0;border-bottom:1px solid #1f2430} #tg-pop .row:last-of-type{border:0}
  #tg-pop a{color:#8b6cff;text-decoration:none} #tg-pop .k{color:#8b93a7}`;

  function mount() {
    if (document.getElementById('tg-style') || !document.body) return;
    const s = document.createElement('style'); s.id = 'tg-style'; s.textContent = css; document.head.appendChild(s);
    const b = document.createElement('div'); b.id = 'tg-bar'; b.title = 'Tilt Guard'; document.body.appendChild(b);
    makeDraggable(b);
    draw();
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', mount); else mount();

  /* drag the bar anywhere (position remembered per site); double-click = back to top centre; click = details */
  const posKey = 'hudPos:' + location.hostname;
  function placeBar(b, pos) {
    if (!pos) { b.style.left = '50%'; b.style.top = '6px'; b.style.transform = 'translateX(-50%)'; return; }
    const x = Math.max(0, Math.min(window.innerWidth - b.offsetWidth, pos.x));
    const y = Math.max(0, Math.min(window.innerHeight - b.offsetHeight, pos.y));
    b.style.left = x + 'px'; b.style.top = y + 'px'; b.style.transform = 'none';
  }
  function makeDraggable(b) {
    chrome.storage.local.get([posKey], r => placeBar(b, r[posKey]));
    let start = null, moved = false;
    b.addEventListener('mousedown', e => {
      if (e.button !== 0) return;
      const r = b.getBoundingClientRect();
      start = { mx: e.clientX, my: e.clientY, x: r.left, y: r.top }; moved = false;
      e.preventDefault();
    });
    window.addEventListener('mousemove', e => {
      if (!start) return;
      const dx = e.clientX - start.mx, dy = e.clientY - start.my;
      if (!moved && Math.abs(dx) + Math.abs(dy) < 5) return;
      moved = true;
      b.style.cursor = 'grabbing';
      placeBar(b, { x: start.x + dx, y: start.y + dy });
    }, true);
    window.addEventListener('mouseup', () => {
      if (!start) return;
      const wasMoved = moved; start = null; b.style.cursor = 'pointer';
      if (wasMoved) {
        const r = b.getBoundingClientRect();
        chrome.storage.local.set({ [posKey]: { x: Math.round(r.left), y: Math.round(r.top) } });
        const p = document.getElementById('tg-pop'); if (p) p.remove();
      } else togglePop();
    }, true);
    b.addEventListener('dblclick', () => { chrome.storage.local.remove(posKey); placeBar(b, null); const p = document.getElementById('tg-pop'); if (p) p.remove(); });
    window.addEventListener('resize', () => chrome.storage.local.get([posKey], r => placeBar(b, r[posKey])));
    b.title = 'Tilt Guard — drag to move, double-click to reset, click for details';
  }

  const money = v => v == null ? '—' : (v < 0 ? '-' : '+') + '$' + Math.abs(v).toLocaleString('en-US', { maximumFractionDigits: 0 });
  const left = u => { const s = Math.max(0, u - Date.now() / 1000), h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), x = Math.floor(s % 60); return h ? `${h}h ${String(m).padStart(2, '0')}m` : `${m}m ${String(x).padStart(2, '0')}s`; };
  const hm = ts => new Date(ts * 1000).toLocaleString([], { weekday: 'short', hour: '2-digit', minute: '2-digit', hour12: false });   // your local time
  const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  function current() {
    if (!status) return null;
    return (active && status.accounts[active]) || Object.values(status.accounts).find(a => a.locked) || null;
  }

  function draw() {
    const bar = document.getElementById('tg-bar');
    if (!bar) return;
    const a = current();
    if (orphaned) { bar.className = 'lock'; bar.innerHTML = `<span class="d"></span><b>Tilt Guard was updated</b><span class="m">reload this tab (F5) to stay protected</span>`; return; }
    if (!status) { bar.className = 'off'; bar.innerHTML = `<span class="d"></span><b>Tilt Guard</b><span class="m">not connected — click the extension icon</span>`; return; }
    if (!a) { bar.className = ''; bar.innerHTML = `<span class="d"></span><b>Tilt Guard</b><span class="m">waiting for the Tradovate panel…</span>`; overlay(null); return; }
    const s = a.stats, r = a.rules || {};
    const st = a.locked ? `LOCKED · ${left(a.lock.until)}` : a.cooldown ? `COOLDOWN · ${left(a.cooldown.until)}` : a.window ? 'OUTSIDE WINDOW' : 'OK';
    bar.className = a.locked ? 'lock' : a.block_entries ? 'warn' : '';
    bar.innerHTML = `<span class="d"></span><b>${esc(a.label || shortName(a.name))}</b>
      <span class="n ${s.day_pl > 0 ? 'tg-pos' : s.day_pl < 0 ? 'tg-neg' : ''}">${money(s.day_pl)}</span>
      ${r.max_daily_loss ? `<span class="m">loss left <span class="n">${money(s.loss_left).replace('+', '')}</span></span>` : ''}
      <span class="m">trades <span class="n">${s.trades}${s.trades_left != null ? '/' + (s.trades + s.trades_left) : ''}</span></span>
      <b>${st}</b>`;
    overlay(a);
  }
  const shortName = n => n.length > 12 ? '…' + n.slice(-8) : n;
  function overlay(a) {
    let o = document.getElementById('tg-over');
    const lock = a && a.locked ? a.lock : null;
    if (!lock || collapsedLock === lock.id) { if (o) o.remove(); return; }
    if (!o) {
      o = document.createElement('div'); o.id = 'tg-over'; document.body.appendChild(o);
      o.addEventListener('click', e => { if (e.target.id === 'tg-hide') { collapsedLock = lock.id; o.remove(); } });
    }
    o.innerHTML = `<div class="c"><div class="lk">🔒</div><h2>LOCKED OUT</h2><div class="r">${esc(lock.reason)}</div>
      <div class="m">Trading unlocks ${hm(lock.until)} (your time)</div><div class="t">${left(lock.until)}</div>
      <div class="m">${lock.account !== a.name ? `Triggered by ${esc(lock.account)}. ` : ''}New orders are rejected; closing positions still works.</div>
      <button id="tg-hide">Show the chart (trading stays blocked)</button></div>`;
  }

  function flash(msg, color) {
    let t = document.getElementById('tg-toast');
    if (!t) { t = document.createElement('div'); t.id = 'tg-toast'; document.body.appendChild(t); }
    t.style.background = color || '#f25672';
    t.textContent = '🛡 ' + msg; clearTimeout(flash._t); flash._t = setTimeout(() => t.remove(), 4000);
  }

  async function togglePop() {
    let p = document.getElementById('tg-pop');
    if (p) { p.remove(); return; }
    const cfg = await new Promise(r => chrome.storage.local.get(['server'], r));
    const a = current(), r = (a && a.rules) || {};
    p = document.createElement('div'); p.id = 'tg-pop'; document.body.appendChild(p);
    const br = document.getElementById('tg-bar').getBoundingClientRect();
    p.style.transform = 'none';
    p.style.left = Math.max(8, Math.min(window.innerWidth - 328, br.left + br.width / 2 - 160)) + 'px';
    p.style.top = (br.bottom + 330 > window.innerHeight ? Math.max(8, br.top - 330) : br.bottom + 6) + 'px';
    const row = (k, v) => `<div class="row"><span class="k">${k}</span><span>${v}</span></div>`;
    p.innerHTML = a ? `<b>${esc(a.name)}</b>
      ${row('Day P&L', money(a.stats.day_pl))}${row('Open P&L', money(a.stats.open_pl))}${row('Peak', money(a.stats.peak_pl))}
      ${row('Daily loss limit', r.max_daily_loss ? '$' + r.max_daily_loss : 'off')}${row('Profit target', r.profit_target ? '$' + r.profit_target : 'off')}
      ${row('Max loss / trade', r.max_trade_loss ? '$' + r.max_trade_loss : 'off')}${row('Max contracts', r.max_contracts || 'off')}
      ${row('Losses in a row', a.stats.streak)}${row('Status', a.reason ? esc(a.reason) : 'OK')}
      ${a.locked ? '' : `<div style="display:flex;gap:6px;margin-top:8px"><select id="tg-lkmin" style="flex:1;background:#181c25;color:#e7eaf1;border:1px solid #2a3040;border-radius:7px;padding:5px;font:inherit">
        <option value="">until the reset</option><option value="30">30 min</option><option value="60">1 hour</option><option value="120">2 hours</option><option value="240">4 hours</option></select>
        <button id="tg-lkone" style="background:#f25672;color:#fff;border:0;border-radius:7px;padding:6px 10px;font:600 12px Inter,system-ui,sans-serif;cursor:pointer">Lock this account</button></div>`}
      <div style="margin-top:8px;display:flex;justify-content:space-between"><a href="${esc((cfg.server || '') + '/')}" target="_blank">Guard ↗</a><a href="${esc((cfg.server || '').replace(/\/guard\/?$/, '') + '/')}" target="_blank">Journal ↗</a><span class="k">v${VERSION}</span></div>`
      : `<b>Tilt Guard v${VERSION}</b><div class="k">${status ? 'Connect Tradovate in the trading panel.' : 'Set the server and key in the extension popup.'}</div>`;
    const lb = p.querySelector('#tg-lkone');
    if (lb) lb.onclick = async () => {
      const min = p.querySelector('#tg-lkmin').value;
      if (!confirm(`Lock ${a.label || a.name} ${min ? 'for ' + (min >= 60 ? min / 60 + ' h' : min + ' min') : 'until the next session'}?
Positions are closed, new orders blocked. Your other accounts keep trading. No early unlock.`)) return;
      const r = await send({ type: 'manual_lock', account: a.name, minutes: min ? +min : null, reason: 'Locked from the chart' });
      if (r && r.status) apply(r.status); p.remove();
    };
    setTimeout(() => document.addEventListener('click', function off(e) { if (!e.target.closest('#tg-pop,#tg-bar')) { p.remove(); document.removeEventListener('click', off); } }), 0);
  }

})();
