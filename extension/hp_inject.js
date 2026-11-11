/* Tilt Guard - page hook (MAIN world, document_start) on app.hyperprop.com.
 *
 * The HyperProp web terminal calls HyperProp's Trade API (https://api.hyperprop.com/trade/...) with the
 * signed-in user's session token. This hook uses that same session to:
 *   - read accounts, day P&L, positions, fills and HyperProp's own risk/lockout state, and report them;
 *   - reject new-entry orders while the guard says you're blocked (exits always go through);
 *   - on a guard lock, start HyperProp's NATIVE lockout (server-side: survives closing the browser) and flatten;
 *   - mirror your guard rules into HyperProp's personal risk settings and freeze them for the day once you
 *     start trading (HyperProp: a frozen setting cannot be loosened before the next session, even by support).
 * The session token never leaves the page. */
(() => {
  if (window.__tiltguardHP) return;
  window.__tiltguardHP = true;

  const API = 'https://api.hyperprop.com';
  const nativeFetch = window.fetch;
  const st = {
    accounts: {}, positions: {}, pending: {}, risk: {}, lockout: {}, unlockHopeless: {},
    seenExec: new Set(), newFills: [], guard: {}, local: {},
    did: {}, ignored: {}, lastTrades: 0, lastRisk: 0, polling: false, error: '', shapeSent: {},
  };
  const emit = (type, data) => window.postMessage({ __tg: 'inject', type, data }, location.origin);
  const num = v => (v == null || v === '' || isNaN(+v)) ? null : +v;
  const first = (o, ...keys) => { for (const k of keys) if (o && o[k] != null) return o[k]; return null; };
  const token = () => { try { return localStorage.getItem('hyperprop_session_token'); } catch (e) { return null; } };
  const selectedId = () => { try { return (localStorage.getItem('hyperprop-selected-account-id') || '').replace(/"/g, ''); } catch (e) { return ''; } };
  const nameOf = id => (st.accounts[id] && st.accounts[id].account_number) || id;
  const guardOf = id => st.guard[nameOf(id)] || null;
  const once = (key, ms) => { const t = st.did[key]; if (t && Date.now() - t < ms) return false; st.did[key] = Date.now(); return true; };
  const tsOf = v => { if (v == null) return null; if (typeof v === 'number') return v > 1e12 ? v : v * 1000; const t = Date.parse(v); return isNaN(t) ? null : t; };

  async function call(method, path, body) {
    const t = token();
    if (!t) throw new Error('not signed in');
    const r = await nativeFetch(API + path, {
      method, headers: { Authorization: 'Bearer ' + t, ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined,
    });
    const j = await r.json().catch(() => ({}));
    if (!r.ok || j.success === false) throw new Error(`${path}: ${j.error || j.code || r.status}`);
    return j.data !== undefined ? j.data : j;
  }
  const listOf = d => Array.isArray(d) ? d : (d && (d.items || d.trades || d.positions || d.orders || d.accounts)) || [];

  /* ------------------------------------------------ readers */
  function readPositions(list) {
    const by = {};
    for (const p of listOf(list)) {
      const acc = first(p, 'account_id', 'accountId');
      let qty = num(first(p, 'net_quantity', 'quantity', 'qty', 'size', 'position')) || 0;
      const side = String(first(p, 'side', 'direction') || '').toLowerCase();
      if (qty > 0 && (side === 'sell' || side === 'short')) qty = -qty;
      if (!acc || !qty) continue;
      (by[acc] = by[acc] || []).push({
        contract: first(p, 'contract', 'symbol'), qty,
        avg_price: num(first(p, 'average_price', 'avg_price', 'average_entry_price', 'entry_price', 'avg_entry_price')),
        open_pl: num(first(p, 'unrealized_pnl', 'unrealized', 'open_pnl', 'unrealizedPnl')) || 0,
      });
    }
    const before = JSON.stringify(st.positions);
    st.positions = by;
    return before !== JSON.stringify(by);
  }
  function readTrades(list) {
    for (const t of listOf(list)) {
      const acc = first(t, 'account_id', 'accountId');
      const price = num(first(t, 'price', 'fill_price', 'execution_price', 'avg_price'));
      const qty = num(first(t, 'quantity', 'qty', 'filled_quantity', 'size'));
      const ts = tsOf(first(t, 'executed_at', 'timestamp', 'filled_at', 'created_at', 'time'));
      const side = String(first(t, 'side', 'direction') || '').toLowerCase();
      const contract = first(t, 'contract', 'symbol');
      if (!acc || price == null || !qty || !ts || !contract || !side) {
        if (!st.shapeSent.trade) { st.shapeSent.trade = 1; emit('event', { account: '', type: 'hp_shape', detail: 'Unparsed HyperProp fill keys: ' + Object.keys(t).join(',') }); }
        continue;
      }
      const key = String(first(t, 'id', 'trade_id', 'fill_id', 'execution_id') || `${ts}|${side}|${qty}|${price}|${contract}`);
      if (st.seenExec.has(acc + key)) continue;
      st.seenExec.add(acc + key);
      st.newFills.push({ account: nameOf(acc), exec_id: key, order_id: first(t, 'order_id', 'parent_order_id') || '', contract,
                         side: side.startsWith('b') || side === 'long' ? 'buy' : 'sell', qty: Math.abs(qty), price, ts,
                         commission: Math.abs(num(first(t, 'fee', 'fees', 'commission')) || 0) });
    }
  }
  const posOf = (acc, contract) => (st.positions[acc] || []).filter(p => p.contract === contract).reduce((a, p) => a + p.qty, 0);
  const nativeLocked = acc => {
    const l = st.lockout[acc] || (st.accounts[acc] && st.accounts[acc].lockout);
    return !!(l && (l.active || l.locked || l.is_locked));
  };

  /* ------------------------------------------------ poll + report */
  async function poll() {
    if (st.polling || !token()) return;
    st.polling = true;
    try {
      listOf(await call('GET', '/trade/accounts')).forEach(a => { st.accounts[a.id] = a; });
      const moved = readPositions(await call('GET', '/trade/positions'));
      if (moved || Date.now() - st.lastTrades > 15000) { readTrades(await call('GET', '/trade/trades')); st.lastTrades = Date.now(); }
      if (Date.now() - st.lastRisk > 30000) { await refreshNative(); st.lastRisk = Date.now(); }
      st.error = '';
      report();
    } catch (e) { st.error = String(e.message || e); }
    finally { st.polling = false; }
  }
  async function refreshNative(only) {
    for (const id of only ? [only] : Object.keys(st.accounts)) {
      try { st.risk[id] = await call('GET', `/trade/account/risk-settings?account_id=${id}`); } catch (e) { }
      try { st.lockout[id] = await call('GET', `/trade/account/lockout?account_id=${id}`); } catch (e) { }
    }
  }
  function report() {
    const accounts = Object.values(st.accounts).map(a => {
      const pos = st.positions[a.id] || [];
      const open = pos.reduce((s, p) => s + (p.open_pl || 0), 0);
      const realized = num(a.daily_pnl) != null ? num(a.daily_pnl)
        : (num(a.current_balance) != null && num(a.daily_starting_balance) != null ? num(a.current_balance) - num(a.daily_starting_balance) : 0);
      const r = st.risk[a.id] || {}, l = st.lockout[a.id] || a.lockout || null;
      return {
        name: a.account_number || a.id, broker_id: a.id, broker: 'hyperprop',
        env: (a.organization && (a.organization.name || a.organization.slug)) || '',
        balance: num(a.current_balance), total_pl: Math.round((realized + open) * 100) / 100, open_pl: open, realized_pl: realized,
        positions: pos, working_orders: 0,
        native: {
          status: a.status, plan: a.plan && a.plan.name, settings_locked: !!r.settings_locked, settings_locked_until: r.settings_locked_until || null,
          daily_loss_limit: r.daily_loss_limit ?? null, daily_profit_target: r.daily_profit_target ?? null, max_trades_per_day: r.max_trades_per_day ?? null,
          trades_today: r.trades_today ?? null, trading_windows: (r.trading_windows || []).length, pending: r.pending || null,
          lockout: l && (l.active || l.locked) ? { active: true, mode: l.mode, ends_at: l.ends_at, locked_by: l.locked_by, reason: l.reason } : null,
        },
      };
    });
    if (!accounts.length) return;
    const sel = nameOf(selectedId());
    emit('report', { platform: 'hyperprop', accounts, account: accounts.find(a => a.name === sel) || accounts[0], fills: st.newFills.splice(0) });
  }

  /* ------------------------------------------------ enforcement (HyperProp-native, server-side) */
  async function enforce() {
    for (const id of Object.keys(st.accounts)) {
      const g = guardOf(id);
      if (!g) continue;
      const name = nameOf(id);
      try {
        if (g.locked && g.lock && !nativeLocked(id) && once('lock' + id + g.lock.id, 60000)) {
          // HyperProp's trading day ends at 16:00 CT (17:00 ET), an hour BEFORE the guard's 18:00 ET reset. A
          // "trading_day" lockout started after 17:00 ET covers the WHOLE NEXT day and can't be removed (bug of
          // 2026-10-13: re-applied at 17:00 ET, MFF locked all Wednesday). So: "trading_day" only once per guard lock
          // and only before 17:00 ET; otherwise whole hours, never past the guard's own unlock time.
          const leftMs = g.lock.until * 1000 - Date.now();
          const untilReset = window.__tgResetAt && Math.abs(g.lock.until - window.__tgResetAt) < 120;
          const etH = +new Intl.DateTimeFormat('en-US', { timeZone: 'America/New_York', hour: 'numeric', hourCycle: 'h23' }).format(new Date());
          const key = 'tg_hp_native_' + id + '_' + g.lock.id;
          let applied = false; try { applied = !!localStorage.getItem(key); } catch (e) { }
          const hours = Math.floor(leftMs / 3600000);                 // round DOWN: never lock longer than the guard
          let body = null;
          if (untilReset && !applied && etH < 17) body = { account_id: id, mode: 'trading_day' };
          else if (hours >= 1) body = { account_id: id, mode: 'hours', hours };
          if (body) {                                                 // < 1 h left: no native lockout, the page gate blocks entries until the reset
            try { localStorage.setItem(key, '1'); } catch (e) { }
            body.reason = ('Tilt Guard: ' + (g.lock.reason || 'locked')).slice(0, 200);
            st.lockout[id] = await call('POST', '/trade/account/lockout', body);          // HyperProp flattens on activation
            emit('event', { account: name, type: 'native_lockout', detail: `HyperProp lockout (${body.mode}${body.hours ? ' ' + body.hours + 'h' : ''}): ${g.lock.reason}` });
          } else if (g.flatten && (st.positions[id] || []).length && once('flat' + id, 5000)) {
            await call('POST', '/trade/flatten', { account_ids: [id] });
          }
        } else if (!g.locked && g.cooldown && !nativeLocked(id) && once('cool' + id + g.cooldown.id, 60000)) {
          const mins = (g.cooldown.until * 1000 - Date.now()) / 60000;
          if (mins >= 10) {                                                   // HyperProp's shortest timed lockout is 15 min
            const preset = mins <= 15 ? '15m' : mins <= 30 ? '30m' : '1h';
            st.lockout[id] = await call('POST', '/trade/account/lockout', { account_id: id, mode: 'timed', timed_duration: preset, reason: 'Tilt Guard: ' + g.cooldown.reason });
            emit('event', { account: name, type: 'native_lockout', detail: `HyperProp timed lockout ${preset}: ${g.cooldown.reason}` });
          }
        }
        const nl = st.lockout[id];
        if (!g.locked && !g.cooldown && nl && (nl.active || nl.locked) && nl.locked_by === 'user'
            && /^Tilt Guard/.test(nl.reason || '') && !st.unlockHopeless[id]
            && once('unlock' + id, 60000)) {
          try {
            await call('DELETE', `/trade/account/lockout?account_id=${id}`, { account_id: id });
            st.lockout[id] = null;
            emit('event', { account: name, type: 'native_unlock', detail: 'Guard lock over - HyperProp lockout removed' });
          } catch (e) {
            // A timed lockout HyperProp has already started cannot be lifted: remember and stand down instead of
            // hammering the endpoint forever (2026-10-14: 22 futile DELETEs in one morning).
            if (/cannot be removed early/i.test(String(e.message || e))) {
              st.unlockHopeless[id] = true;
              emit('event', { account: name, type: 'native_lockout', detail: 'HyperProp timed lockout stays until it expires (cannot be removed early)' });
            }
            throw e;
          }
        }
        if (g.flatten && (st.positions[id] || []).length && once('flat' + id, 5000)) {
          await call('POST', '/trade/flatten', { account_ids: [id] });
          emit('event', { account: name, type: 'flatten', detail: 'HyperProp flatten: ' + (g.flatten_reason || g.reason) });
        } else if (g.block_entries && !g.flatten && (g.locked || g.cooldown) && once('cancel' + id, 10000)) {
          const pend = listOf(await call('GET', '/trade/orders/pending')).filter(o => first(o, 'account_id') === id);
          const entries = pend.filter(o => { const p = posOf(id, first(o, 'contract', 'symbol')); const sell = String(o.side).toLowerCase() === 'sell'; return !(p !== 0 && (p > 0) === sell); });
          for (const o of entries) await call('POST', '/trade/order/cancel', { account_id: id, order_id: first(o, 'id', 'order_id') });
          if (entries.length) emit('event', { account: name, type: 'cancel_entries', detail: `${entries.length} pending entry order(s) cancelled: ${g.reason}` });
        }
        await syncRisk(id, g);
      } catch (e) {
        if (once('err' + id + e.message, 300000)) emit('event', { account: name, type: 'hp_error', detail: String(e.message || e).slice(0, 300) });
      }
    }
  }

  const ROOTS = ['MNQ', 'NQ', 'MES', 'ES', 'MYM', 'YM', 'M2K', 'RTY', 'MGC', 'GC', 'MCL', 'CL'];
  function desired(r) {
    const d = {};
    if (r.max_daily_loss) { d.daily_loss_limit = +r.max_daily_loss; d.daily_loss_action = 'liquidate_and_block'; }   // 'Close and lock me out'
    if (r.profit_target) { d.daily_profit_target = +r.profit_target; d.daily_profit_action = 'liquidate_and_block'; }
    if (r.max_trades) d.max_trades_per_day = Math.round(r.max_trades);
    if (r.window_start && r.window_end) {
      d.trading_windows = [{ days: [1, 2, 3, 4, 5], start: r.window_start, end: r.window_end, timezone: 'America/New_York', label: 'Tilt Guard' }];
      d.trading_window_action = r.flatten_on_lock ? 'flatten' : 'block';
    }
    return d;
  }
  function differs(d, cur) {
    const out = {};
    for (const [k, v] of Object.entries(d)) {
      const c = cur[k];
      if (k === 'trading_windows') {
        const cw = (c || [])[0] || {};
        if ((c || []).length !== 1 || cw.start !== v[0].start || cw.end !== v[0].end) out[k] = v;
      } else if (typeof v === 'number' ? Math.abs((+c || 0) - v) > 1e-6 : c !== v) out[k] = v;
    }
    return out;
  }
  async function syncRisk(id, g) {
    const r = g.rules || {}, cur = st.risk[id];
    if (!cur) return;
    const want = desired(r);
    const diff = differs(want, cur);
    for (const k of Object.keys(diff)) if (st.ignored[id + k + JSON.stringify(diff[k])]) delete diff[k];
    if (Object.keys(diff).length && once('risk' + id + JSON.stringify(diff), 120000)) {
      await call('POST', '/trade/account/risk-settings', { account_id: id, ...diff });
      emit('event', { account: nameOf(id), type: 'native_rules', detail: 'HyperProp risk settings ← ' + Object.keys(diff).join(', ') });
      await refreshNative(id);
      const still = differs(diff, st.risk[id] || {});
      for (const k of Object.keys(still)) st.ignored[id + k + JSON.stringify(diff[k])] = 1;
      if (Object.keys(still).length)
        emit('event', { account: nameOf(id), type: 'hp_error', detail: 'HyperProp kept its own value for: ' + Object.keys(still).join(', ') });
    }
    const mc = r.max_contracts && Math.round(r.max_contracts);
    if (mc && ROOTS.some(s => (cur.symbol_contract_limits || {})[s] !== mc) && once('mc' + id + mc, 600000)) {
      try {
        await call('POST', '/trade/account/risk-settings', { account_id: id, symbol_contract_limits: Object.fromEntries(ROOTS.map(s => [s, mc])) });
        emit('event', { account: nameOf(id), type: 'native_rules', detail: `HyperProp contract limit ${mc} per symbol` });
      } catch (e) { emit('event', { account: nameOf(id), type: 'hp_error', detail: 'contract limits: ' + e.message }); }
    }
    // freeze for the day once you've started trading (or a lock fired) and the rules are in place
    const c2 = st.risk[id] || cur;
    for (const k of Object.keys(want)) if (st.ignored[id + k + JSON.stringify(want[k])]) delete want[k];
    const started = (+c2.trades_today || 0) > 0 || (st.positions[id] || []).length > 0 || (g.stats && g.stats.trades > 0) || g.locked;
    if (window.__tgFreeze !== false && Object.keys(want).length && started && !c2.settings_locked
        && !Object.keys(differs(want, c2)).length && once('freeze' + id, 300000)) {
      await call('POST', '/trade/account/risk-settings', { account_id: id, lock_settings_for: 'today' });
      emit('event', { account: nameOf(id), type: 'native_freeze', detail: 'HyperProp risk settings frozen until the 17:00 CT session reset' });
      await refreshNative(id);
    }
  }

  /* ------------------------------------------------ order gate in the page */
  function blockReason(acc) {
    if (st.local[acc]) return st.local[acc];
    const g = guardOf(acc);
    return g && g.block_entries ? (g.reason || 'Locked') : null;
  }
  window.fetch = async function (input, init) {
    const url = typeof input === 'string' ? input : (input && input.url) || String(input || '');
    const method = String((init && init.method) || (input && input.method) || 'GET').toUpperCase();
    if (method === 'POST' && url.startsWith(API + '/trade/')) {
      const path = url.slice(API.length).split('?')[0];
      if (path === '/trade/order' || path === '/trade/position/reverse') {
        let b = {}; try { b = JSON.parse((init && init.body) || '{}'); } catch (e) { }
        const acc = b.account_id;
        const pos = posOf(acc, b.contract);
        const side = String(b.side || '').toLowerCase();
        const reducing = path === '/trade/order' && pos !== 0 && ((pos > 0 && side === 'sell') || (pos < 0 && side === 'buy')) && (+b.quantity || 0) <= Math.abs(pos);
        let why = reducing ? null : blockReason(acc);
        const g = guardOf(acc), mc = g && g.rules && g.rules.max_contracts;
        if (!why && !reducing && mc && path === '/trade/order') {
          const after = Math.abs(pos + (side === 'buy' ? 1 : -1) * (+b.quantity || 0));
          if (after > mc) why = `Max ${mc} contracts (this order would make ${after})`;
        }
        if (why) {
          emit('event', { account: nameOf(acc), type: 'blocked_order', detail: `${path === '/trade/order' ? 'New order' : 'Reverse'} rejected: ${why}` });
          emit('blocked', { account: nameOf(acc), reason: why });
          return new Response(JSON.stringify({ success: false, data: null, error: `Tilt Guard: ${why}`, code: 'TILT_GUARD_LOCKED' }),
                              { status: 403, headers: { 'Content-Type': 'application/json' } });
        }
      }
    }
    const res = await nativeFetch.apply(this, arguments);
    if (method === 'POST' && /\/trade\/(order|flatten|close-all|position\/)/.test(url)) setTimeout(poll, 600);   // refresh quickly after trading
    return res;
  };

  /* ------------------------------------------------ guard updates */
  window.addEventListener('message', ev => {
    if (ev.source !== window || !ev.data || ev.data.__tg !== 'content') return;
    const { type, data } = ev.data;
    if (type === 'guard' && data && data.accounts) {
      st.guard = data.accounts;
      window.__tgResetAt = data.reset_at;
      if (data.hp_freeze === false) window.__tgFreeze = false;
      for (const id of Object.keys(st.local)) { const g = guardOf(id); if (g && g.locked) delete st.local[id]; }
      enforce();
    }
  });

  // poll every 2.5 s
  const pollMs = 2500;
  let pollTimer = null;
  const loop = () => { clearTimeout(pollTimer); pollTimer = setTimeout(async () => { await poll(); loop(); }, pollMs); };
  loop();
  setTimeout(poll, 1500);
  emit('hello', { platform: 'hyperprop' });
})();
