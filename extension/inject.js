/* Tilt Guard - page hook (MAIN world, document_start) on TradingView.
 *
 * TradingView's Tradovate panel talks to Tradovate's TradingView REST API straight from the page
 * (https://tv-demo|tv-live.tradovateapi.com). This hook reads those responses (accounts, day P&L,
 * positions, orders, fills) and, while the guard says you're blocked, answers new-order requests
 * itself with an error instead of sending them. Exits (closing / reducing a position, cancelling
 * orders) always go through. The captured Authorization header never leaves the page: it is only
 * used to call the same API for fills and for flattening. */
(() => {
  if (window.__tiltguardHook) return;
  window.__tiltguardHook = true;

  const API = /^https:\/\/tv-(demo|live)\.tradovateapi\.com/;
  const nativeFetch = window.fetch;
  const st = {
    base: null, env: null, headers: null,
    accounts: {},          // id -> {id, name, type}
    columns: null,         // account summary column ids (from /config)
    active: null,          // account id TradingView is polling
    snap: {},              // id -> {balance, equity, total_pl, open_pl, net_liq}
    positions: {},         // id -> [{id, instrument, qty(signed), avgPrice, unrealizedPl}]
    orders: {},            // id -> [...]
    seenExec: new Set(),   // exec keys already reported
    newFills: [],
    instruments: {},       // id -> Set(instrument) traded / watched today
    guard: {},             // account name -> status from the server
    local: {},             // id -> {reason} instant local lock (before the server answers)
    flattening: {},
  };

  const emit = (type, data) => window.postMessage({ __tg: 'inject', type, data }, location.origin);
  const num = v => {
    if (v == null || v === '') return null;
    if (typeof v === 'number') return v;
    let s = String(v).trim(), neg = /^\(.*\)$/.test(s) || /^-/.test(s.replace(/[^\d.\-()]/g, ''));
    s = s.replace(/[^\d.]/g, '');
    return s ? (neg ? -1 : 1) * parseFloat(s) : null;
  };
  const acctId = url => { const m = url.match(/\/accounts\/([^/?#]+)/); return m ? decodeURIComponent(m[1]) : null; };
  const nameOf = id => (st.accounts[id] && st.accounts[id].name) || id;
  const guardOf = id => st.guard[nameOf(id)] || null;
  const plainHeaders = h => {
    if (!h) return null;
    if (h instanceof Headers) { const o = {}; h.forEach((v, k) => { o[k] = v; }); return o; }
    if (Array.isArray(h)) return Object.fromEntries(h);
    return { ...h };
  };
  const bodyParams = b => {
    if (!b) return {};
    if (b instanceof URLSearchParams) return Object.fromEntries(b);
    if (typeof b === 'string') {
      try { return JSON.parse(b); } catch (e) { return Object.fromEntries(new URLSearchParams(b)); }
    }
    if (b instanceof FormData) return Object.fromEntries(b);
    return {};
  };
  const posOf = (id, instrument) => (st.positions[id] || []).filter(p => p.instrument === instrument).reduce((a, p) => a + p.qty, 0);
  const reply = (obj) => new Response(JSON.stringify(obj), { status: 200, headers: { 'Content-Type': 'application/json' } });

  /* ------------------------------------------------ order gate */
  function blockReason(id) {
    if (st.local[id]) return st.local[id].reason;
    const g = guardOf(id);
    return g && g.block_entries ? (g.reason || 'Locked') : null;
  }
  function gate(id, method, path, body) {
    const p = bodyParams(body);
    const g = guardOf(id);
    if (method === 'PUT') {                                    // modify a working order
      if (!(g && g.locked) && !st.local[id]) return null;
      const oid = decodeURIComponent(path.split('/').pop());
      const o = (st.orders[id] || []).find(x => String(x.id) === oid);
      const pos = o ? posOf(id, o.instrument) : 0;
      const exit = o && pos !== 0 && ((pos > 0) !== (o.side === 'buy'));
      return exit ? null : blockReason(id) || 'Locked';
    }
    const qty = Math.abs(num(p.qty) || 0), side = String(p.side || '').toLowerCase(), inst = p.instrument;
    const pos = posOf(id, inst);
    const reducing = pos !== 0 && ((pos > 0 && side === 'sell') || (pos < 0 && side === 'buy')) && qty <= Math.abs(pos);
    if (reducing) return null;                                  // exits always allowed
    const why = blockReason(id);
    if (why) return why;
    const mc = g && g.rules && g.rules.max_contracts;
    if (mc) {
      const after = Math.abs(pos + (side === 'buy' ? qty : -qty));
      if (after > mc) return `Max ${mc} contracts (this order would make ${after})`;
    }
    return null;
  }

  /* ------------------------------------------------ response readers */
  function readConfig(d) {
    try {
      const am = (d.accountManager || []).find(x => x.id === 'accountSummary') || (d.accountManager || [])[0];
      if (am && am.columns) st.columns = am.columns.map(c => c.id);
    } catch (e) { }
  }
  function readState(id, d) {
    const s = { balance: num(d.balance), equity: num(d.equity), open_pl: num(d.unrealizedPl), total_pl: null, net_liq: null };
    try {
      const row = d.amData && d.amData[0] && d.amData[0][0];
      if (row && !st.columns) st.columns = ['totalPL', 'openPL', 'netLiq', 'totalMarginUsed', 'availableMargin', 'dayMargin', 'initialMargin', 'maintenanceMargin'];
      if (row && st.columns) {
        const at = k => { const i = st.columns.indexOf(k); return i >= 0 ? num(row[i]) : null; };
        s.total_pl = at('totalPL'); s.net_liq = at('netLiq');
        const o = at('openPL'); if (o != null) s.open_pl = o;
      }
    } catch (e) { }
    st.snap[id] = s;
    localCheck(id);
  }
  function readPositions(id, list) {
    const before = JSON.stringify((st.positions[id] || []).map(p => [p.instrument, p.qty]));
    st.positions[id] = (list || []).map(p => ({
      id: p.id, instrument: p.instrument, qty: (p.side === 'sell' ? -1 : 1) * Math.abs(num(p.qty) || 0),
      avgPrice: num(p.avgPrice), unrealizedPl: num(p.unrealizedPl),
    })).filter(p => p.qty !== 0);
    st.positions[id].forEach(p => watch(id, p.instrument));
    const after = JSON.stringify(st.positions[id].map(p => [p.instrument, p.qty]));
    if (before !== after) setTimeout(() => pullFills(id), 700);   // a fill happened: fetch executions
  }
  function readOrders(id, list) {
    st.orders[id] = (list || []).map(o => ({ id: o.id, instrument: o.instrument, qty: num(o.qty), side: o.side, status: o.status, parentId: o.parentId }));
    st.orders[id].forEach(o => watch(id, o.instrument));
  }
  function readExecutions(id, list) {
    for (const e of list || []) {
      const t = num(e.time);
      const key = String(e.id || `${t}|${e.side}|${e.qty}|${e.price}|${e.instrument}`);
      if (st.seenExec.has(id + key)) continue;
      st.seenExec.add(id + key);
      watch(id, e.instrument);
      st.newFills.push({ account: nameOf(id), exec_id: key, contract: e.instrument, side: e.side, qty: num(e.qty),
                         price: num(e.price), ts: t, commission: num(e.commission) || 0 });
    }
  }
  function watch(id, inst) { if (inst) (st.instruments[id] = st.instruments[id] || new Set()).add(inst); }

  /* ------------------------------------------------ our own API calls (same session as TradingView) */
  async function call(method, path) {
    if (!st.base || !st.headers) throw new Error('no session yet');
    const url = st.base + path + (path.includes('?') ? '&' : '?') + 'locale=en';
    const r = await nativeFetch(url, { method, headers: st.headers, credentials: 'omit' });
    return r.json().catch(() => ({}));
  }
  async function pullFills(id) {
    for (const inst of st.instruments[id] || []) {
      try { const j = await call('GET', `/accounts/${encodeURIComponent(id)}/executions?instrument=${encodeURIComponent(inst)}`); if (j.s === 'ok') readExecutions(id, j.d); } catch (e) { }
    }
    schedule();
  }
  async function flatten(id, reason, positionsToo = true) {
    if (st.flattening[id] && Date.now() - st.flattening[id] < 4000) return;
    st.flattening[id] = Date.now();
    try {
      const A = `/accounts/${encodeURIComponent(id)}`;
      let done = 0;
      if (positionsToo) {
        const pos = await call('GET', `${A}/positions`);
        for (const p of (pos.d || [])) if (num(p.qty)) { await call('DELETE', `${A}/positions/${encodeURIComponent(p.id)}`); done++; }
      }
      const ord = await call('GET', `${A}/orders`);
      const posNow = positionsToo ? [] : (st.positions[id] || []);
      for (const o of (ord.d || [])) {
        if (!['working', 'placing'].includes(o.status)) continue;
        const pos = posNow.filter(p => p.instrument === o.instrument).reduce((a, p) => a + p.qty, 0);
        const protective = pos !== 0 && ((pos > 0) !== (o.side === 'buy'));
        if (!protective) { await call('DELETE', `${A}/orders/${encodeURIComponent(o.id)}`); done++; }
      }
      if (done) emit('event', { account: nameOf(id), type: positionsToo ? 'flatten' : 'cancel_entries', detail: `${done} action(s): ${reason}` });
    } catch (e) {
      emit('event', { account: nameOf(id), type: 'flatten_error', detail: String(e.message || e) });
    }
  }

  /* ------------------------------------------------ instant local check (no server round-trip) */
  function localCheck(id) {
    const g = guardOf(id), s = st.snap[id];
    if (!g || !s || !g.rules) return;
    const lim = g.rules.max_daily_loss;
    if (lim && s.total_pl != null && s.total_pl <= -lim && !st.local[id] && !g.locked) {
      st.local[id] = { reason: `Daily loss limit hit: ${s.total_pl.toFixed(0)} (limit -${lim})`, ts: Date.now() };
      flatten(id, st.local[id].reason);
      schedule(0);
    }
    const mtl = g.rules.max_trade_loss;
    if (mtl && s.open_pl != null && s.open_pl <= -mtl) flatten(id, `Max loss per trade (${s.open_pl.toFixed(0)})`);
  }

  /* ------------------------------------------------ report to the content script */
  let timer = null, lastSent = 0;
  function schedule(delay = 300) {
    clearTimeout(timer);
    timer = setTimeout(send, Math.max(delay, 1500 - (Date.now() - lastSent)));
  }
  const snapshot = id => {
    const s = st.snap[id] || {};
    return {
      name: nameOf(id), broker_id: id, broker: 'tradovate', env: st.env,
      balance: s.balance, total_pl: s.total_pl, open_pl: s.open_pl, net_liq: s.net_liq,
      positions: (st.positions[id] || []).map(p => ({ contract: p.instrument, qty: p.qty, avg_price: p.avgPrice, open_pl: p.unrealizedPl })),
      working_orders: (st.orders[id] || []).filter(o => o.status === 'working').length,
    };
  };
  function send() {
    const id = st.active;
    if (!id) return;
    lastSent = Date.now();
    // every connected Tradovate account we have numbers for (the selected one + the ones we poll ourselves)
    const accounts = Object.keys(st.snap).filter(x => st.accounts[x]).map(snapshot);
    if (!st.accounts[id]) accounts.push(snapshot(id));
    const fills = st.newFills.splice(0);
    emit('report', { platform: 'tv', account: snapshot(id), accounts, fills,
                     known_accounts: Object.values(st.accounts).map(a => ({ id: a.id, name: a.name })) });
  }

  // TradingView only polls the account selected in its panel: keep the other connected accounts guarded too
  async function pollOthers() {
    if (!st.base || !st.headers) return;
    const allInst = new Set(Object.values(st.instruments).flatMap(x => [...x]));
    for (const id of Object.keys(st.accounts)) {
      if (id === st.active) continue;
      try {
        const A = `/accounts/${encodeURIComponent(id)}`;
        const s = await call('GET', `${A}/state`); if (s.s === 'ok') readState(id, s.d || {});
        const p = await call('GET', `${A}/positions`); if (p.s === 'ok') readPositions(id, p.d);
        const o = await call('GET', `${A}/orders`); if (o.s === 'ok') readOrders(id, o.d);
        allInst.forEach(i => watch(id, i));
        await pullFills(id);
      } catch (e) { }
    }
    schedule();
  }


  /* ------------------------------------------------ guard updates from the content script */
  window.addEventListener('message', ev => {
    if (ev.source !== window || !ev.data || ev.data.__tg !== 'content') return;
    const { type, data } = ev.data;
    if (type === 'guard' && data && data.accounts) {
      st.guard = data.accounts;
      for (const id of Object.keys(st.local)) {                 // server has taken over
        const g = guardOf(id);
        if ((g && g.locked) || Date.now() - st.local[id].ts > 120000) delete st.local[id];
      }
      for (const [id] of Object.entries(st.accounts)) {
        const g = guardOf(id);
        if (!g) continue;
        if (g.flatten) flatten(id, g.flatten_reason || g.reason);
        else if (g.block_entries && (st.orders[id] || []).some(o => o.status === 'working')) flatten(id, g.reason, false);
      }
    }
  });

  /* ------------------------------------------------ the fetch hook */
  window.fetch = async function (input, init) {
    const url = typeof input === 'string' ? input : (input && input.url) || String(input || '');
    if (!API.test(url)) return nativeFetch.apply(this, arguments);
    const m = url.match(API);
    st.base = m[0]; st.env = m[1];
    const method = String((init && init.method) || (input && input.method) || 'GET').toUpperCase();
    const h = plainHeaders(init && init.headers);
    if (h && Object.keys(h).some(k => k.toLowerCase() === 'authorization')) st.headers = h;
    const path = url.slice(st.base.length).split('?')[0];
    const id = acctId(path);

    if (id && (method === 'POST' || method === 'PUT') && /\/accounts\/[^/]+\/orders(\/[^/]+)?$/.test(path)) {
      const why = gate(id, method, path, init && init.body);
      if (why) {
        emit('event', { account: nameOf(id), type: 'blocked_order', detail: `${method === 'PUT' ? 'Order change' : 'New order'} rejected: ${why}` });
        emit('blocked', { account: nameOf(id), reason: why });
        return reply({ s: 'error', errmsg: `Tilt Guard: ${why}` });
      }
    }

    if (id && method === 'POST' && /\/accounts\/[^/]+\/orders$/.test(path)) {
      // remember how TradingView itself places an order here, so copied orders carry the same extra fields
      const p = bodyParams(init && init.body);
      st.orderTpl = Object.fromEntries(Object.entries(p).filter(([k]) => ['durationType', 'durationDateTime'].includes(k)));
    }
    const res = await nativeFetch.apply(this, arguments);
    if (method === 'GET') {
      res.clone().json().then(j => {
        if (!j || j.s !== 'ok') return;
        if (path === '/accounts') { (j.d || []).forEach(a => { st.accounts[a.id] = { id: a.id, name: a.name, type: a.type }; }); schedule(); return; }
        if (path === '/config') { readConfig(j.d || {}); return; }
        if (!id) return;
        if (/\/state$/.test(path)) { st.active = id; readState(id, j.d || {}); schedule(); }
        else if (/\/positions$/.test(path)) { st.active = id; readPositions(id, j.d); schedule(); }
        else if (/\/orders$/.test(path)) readOrders(id, j.d);
        else if (/\/executions$/.test(path)) { readExecutions(id, j.d); schedule(); }
      }).catch(() => { });
    }
    return res;
  };

  // pull fills for watched instruments every 30 s (covers trades made from the DOM / hotkeys)
  setInterval(() => { if (st.active) pullFills(st.active); }, 30000);
  setInterval(pollOthers, 15000);
  emit('hello', { version: 1 });
})();
