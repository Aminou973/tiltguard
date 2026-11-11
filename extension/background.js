/* Tilt Guard - background service worker.
 * Talks to the guard server, caches the last status (fail-safe while offline), notifies on new
 * locks and blocks the configured trading sites (e.g. the Tradovate web trader) while locked. */
const VERSION = chrome.runtime.getManifest().version;
const RULE_BASE = 9000;
let pendingEvents = [];
let lastLockIds = new Set();

async function cfg() {
  const c = await chrome.storage.local.get(['server', 'key']);
  return { server: (c.server || '').replace(/\/+$/, ''), key: c.key || '' };
}

async function callServer(path, body) {
  const { server, key } = await cfg();
  if (!server || !key) throw new Error('not configured');
  const r = await fetch(server + path, {
    method: body ? 'POST' : 'GET',
    headers: { 'X-TG-Key': key, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (!r.ok) throw new Error(`${r.status} ${await r.text().then(t => t.slice(0, 120)).catch(() => '')}`);
  return r.json();
}

async function onStatus(st) {
  st.received = Date.now();
  await chrome.storage.local.set({ guard: st, lastOk: Date.now(), lastError: null });
  await updateBlocking(st.block_sites || []);
  const locks = Object.values(st.accounts).filter(a => a.locked).map(a => a.lock);
  for (const l of locks) {
    if (!lastLockIds.has(l.id)) {
      lastLockIds.add(l.id);
      const seen = (await chrome.storage.local.get(['notified'])).notified || [];
      if (!seen.includes(l.id)) {
        chrome.notifications.create('tg-lock-' + l.id, {
          type: 'basic', iconUrl: 'icons/icon128.png', title: 'Tilt Guard — locked out',
          message: `${l.reason}\nTrading unlocks at the ${new Date(l.until * 1000).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })} reset.`,
          priority: 2,
        });
        await chrome.storage.local.set({ notified: [...seen, l.id].slice(-50) });
      }
    }
  }
  chrome.action.setBadgeText({ text: locks.length ? 'LOCK' : '' });
  chrome.action.setBadgeBackgroundColor({ color: '#f25672' });
  return st;
}

async function updateBlocking(sites) {
  const existing = await chrome.declarativeNetRequest.getDynamicRules();
  const mine = existing.filter(r => r.id >= RULE_BASE && r.id < RULE_BASE + 100);
  const want = sites.slice(0, 50).map((d, i) => ({
    id: RULE_BASE + i, priority: 1, action: { type: 'block' },
    condition: { requestDomains: [d], resourceTypes: ['main_frame', 'sub_frame'] },
  }));
  const same = mine.length === want.length && want.every(w => mine.some(m => m.id === w.id && JSON.stringify(m.condition.requestDomains) === JSON.stringify(w.condition.requestDomains)));
  if (same) return;
  await chrome.declarativeNetRequest.updateDynamicRules({ removeRuleIds: mine.map(r => r.id), addRules: want });
}

async function cached(err) {
  const { guard } = await chrome.storage.local.get(['guard']);
  await chrome.storage.local.set({ lastError: String(err && err.message || err) });
  if (!guard) return null;
  // expire cached locks/cooldowns locally so an offline server never unlocks early nor locks forever
  const now = Date.now() / 1000;
  for (const a of Object.values(guard.accounts)) {
    if (a.lock && a.lock.until < now) { a.lock = null; a.locked = false; }
    if (a.cooldown && a.cooldown.until < now) a.cooldown = null;
    a.block_entries = !!(a.locked || a.cooldown || a.window);
    a.flatten = false;
  }
  guard.offline = true;
  return guard;
}


chrome.runtime.onMessage.addListener((msg, sender, reply) => {
  (async () => {
    try {
      if (msg.type === 'report') {
        const d = msg.data;
        const events = pendingEvents.splice(0);
        const st = await callServer('/api/ingest/state', { platform: d.platform || 'tv', client: 'ext-' + VERSION,
          accounts: Array.isArray(d.accounts) && d.accounts.length && d.accounts[0].broker ? d.accounts : (d.account ? [d.account] : []), fills: d.fills, events });
        reply({ status: await onStatus(st) });
      } else if (msg.type === 'status') {
        if (pendingEvents.length) {
          const st = await callServer('/api/ingest/state', { platform: 'tv', client: 'ext-' + VERSION, accounts: [], events: pendingEvents.splice(0) });
          reply({ status: await onStatus(st) });
        } else reply({ status: await onStatus(await callServer('/api/guard/status')) });
      } else if (msg.type === 'event') {
        pendingEvents.push(msg.data);
        reply({ ok: true });
      } else if (msg.type === 'manual_lock') {
        const st = await callServer('/api/guard/manual-lock', { account: msg.account, minutes: msg.minutes || null, reason: msg.reason || 'Done on this account' });
        reply({ status: await onStatus(st) });
      } else if (msg.type === 'test') {
        const st = await callServer('/api/guard/status');
        reply({ ok: true, accounts: Object.keys(st.accounts).length, status: await onStatus(st) });
      } else reply(null);
    } catch (e) {
      if (msg.type === 'report' && msg.data && msg.data.fills && msg.data.fills.length) {
        // keep fills for the next successful report
        const { unsent = [] } = await chrome.storage.local.get(['unsent']);
        await chrome.storage.local.set({ unsent: [...unsent, ...msg.data.fills].slice(-2000) });
      }
      reply({ status: await cached(e), error: String(e.message || e) });
    }
  })();
  return true;
});

// resend fills kept while offline
async function flushUnsent() {
  const { unsent = [] } = await chrome.storage.local.get(['unsent']);
  if (!unsent.length) return;
  try { await callServer('/api/ingest/state', { platform: 'tv', client: 'ext-' + VERSION, accounts: [], fills: unsent }); await chrome.storage.local.set({ unsent: [] }); } catch (e) { }
}

chrome.alarms.create('tg-refresh', { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(async a => {
  if (a.name !== 'tg-refresh') return;
  try { await onStatus(await callServer('/api/guard/status')); await flushUnsent(); } catch (e) { await cached(e); }
});
chrome.runtime.onStartup.addListener(() => callServer('/api/guard/status').then(onStatus).catch(() => { }));
chrome.runtime.onInstalled.addListener(() => callServer('/api/guard/status').then(onStatus).catch(() => { }));
