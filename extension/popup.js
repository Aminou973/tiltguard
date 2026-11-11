const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
$('ver').textContent = 'v' + chrome.runtime.getManifest().version;

function show(st, err) {
  $('st').className = 'st ' + (err ? 'bad' : 'ok');
  $('st').textContent = err ? `Not connected: ${err}` : `Connected · session ${st.day}${st.offline ? ' (offline cache)' : ''}`;
  const accts = st ? Object.values(st.accounts) : [];
  $('accts').innerHTML = accts.slice(0, 8).map(a => `<div class="acc"><span>${esc(a.label || a.name)}</span><span class="${a.locked ? 'bad' : a.block_entries ? '' : 'ok'}">${a.locked ? 'LOCKED' : a.cooldown ? 'cooldown' : a.window ? 'outside window' : 'ok'} · ${a.stats.day_pl >= 0 ? '+' : ''}${Math.round(a.stats.day_pl)}$</span></div>`).join('');
}

chrome.storage.local.get(['server', 'key', 'guard', 'lastError'], c => {
  $('server').value = c.server || 'http://127.0.0.1:8940';
  $('key').value = c.key || '';
  $('open').href = ($('server').value || '').replace(/\/+$/, '') + '/';
  if (c.guard) show(c.guard, c.lastError);
});

$('save').onclick = async () => {
  const server = $('server').value.trim().replace(/\/+$/, ''), key = $('key').value.trim();
  await chrome.storage.local.set({ server, key });
  $('open').href = server + '/';
  $('st').className = 'st'; $('st').textContent = 'Testing…';
  chrome.runtime.sendMessage({ type: 'test' }, r => {
    if (chrome.runtime.lastError || !r) return show(null, chrome.runtime.lastError?.message || 'no answer');
    if (r.error) return show(r.status, r.error);
    show(r.status);
  });
};
