namespace OneDriveAsADrive.Settings;

// The settings page, inlined as a string rather than shipped as static files. The product is a
// single self-contained exe that people download and run — adding a wwwroot folder would mean
// either losing that or wiring up embedded-resource plumbing for two files. This is the smaller
// lie. No external fonts, scripts, or styles: the CSP that serves this blocks all of them.
internal static class SettingsPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OneDriveAsADrive Settings</title>
<style>
  :root {
    color-scheme: light dark;
    --bg: #f6f7f9; --card: #fff; --fg: #1b1d21; --muted: #6b7280; --line: #e3e6ea;
    --accent: #0b6bcb; --accent-fg: #fff; --warn-bg: #fff7e6; --warn-line: #f0c36d;
    --err: #b42318; --ok: #127d4a; --disabled: #f2f3f5;
  }
  @media (prefers-color-scheme: dark) {
    :root {
      --bg: #16181c; --card: #1e2126; --fg: #e8eaed; --muted: #9aa2ad; --line: #2e333a;
      --accent: #4a9eff; --accent-fg: #10131a; --warn-bg: #2b2312; --warn-line: #6b5522;
      --err: #ff7b72; --ok: #4ac98a; --disabled: #24272d;
    }
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 32px 20px; background: var(--bg); color: var(--fg);
    font: 15px/1.5 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
  }
  .wrap { max-width: 780px; margin: 0 auto; }
  h1 { font-size: 22px; margin: 0 0 4px; font-weight: 650; }
  .sub { color: var(--muted); font-size: 13px; margin-bottom: 24px; }
  .card { background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 20px; margin-bottom: 16px; }
  .card h2 { font-size: 15px; margin: 0 0 16px; font-weight: 620; }
  label { display: block; font-size: 13px; color: var(--muted); margin-bottom: 5px; }
  input, select {
    width: 100%; padding: 8px 10px; font: inherit; font-size: 14px; color: var(--fg);
    background: var(--card); border: 1px solid var(--line); border-radius: 6px;
  }
  input:disabled, select:disabled { background: var(--disabled); color: var(--muted); cursor: not-allowed; }
  input:focus, select:focus { outline: 2px solid var(--accent); outline-offset: -1px; border-color: transparent; }
  .row { display: flex; gap: 12px; flex-wrap: wrap; }
  .row > div { flex: 1 1 160px; }
  .field { margin-bottom: 14px; }
  .managed { font-size: 12px; color: var(--muted); margin-top: 5px; display: none; }
  .managed.on { display: block; }
  .banner { background: var(--warn-bg); border: 1px solid var(--warn-line); border-radius: 8px; padding: 12px 14px; font-size: 13px; margin-bottom: 16px; }
  .mount { border: 1px solid var(--line); border-radius: 8px; padding: 14px; margin-bottom: 12px; }
  .mount .head { display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px; }
  .mount .head strong { font-size: 14px; }
  button {
    font: inherit; font-size: 14px; padding: 8px 16px; border-radius: 6px; cursor: pointer;
    border: 1px solid var(--line); background: var(--card); color: var(--fg);
  }
  button:hover:not(:disabled) { border-color: var(--accent); }
  button:disabled { opacity: .5; cursor: not-allowed; }
  button.primary { background: var(--accent); color: var(--accent-fg); border-color: var(--accent); }
  button.link { border: none; background: none; color: var(--accent); padding: 4px 6px; }
  .actions { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
  .status { font-size: 13px; }
  .status.err { color: var(--err); }
  .status.ok { color: var(--ok); }
  .kv { display: flex; justify-content: space-between; font-size: 13px; padding: 5px 0; border-bottom: 1px solid var(--line); }
  .kv:last-child { border-bottom: none; }
  .kv span:first-child { color: var(--muted); }
  .sp { display: none; }
  .sp.on { display: block; }
  .check { display: flex; align-items: center; gap: 9px; font-size: 14px; color: var(--fg); margin: 0; cursor: pointer; }
  .check input { width: auto; flex: none; margin: 0; accent-color: var(--accent); }
  .hint { font-size: 12px; color: var(--muted); margin-top: 8px; }
  ul.warn { margin: 8px 0 0; padding-left: 20px; font-size: 13px; color: var(--muted); }
</style>
</head>
<body>
<div class="wrap">
  <h1>OneDriveAsADrive</h1>
  <div class="sub" id="sub">Loading…</div>

  <div class="banner" id="managedBanner" style="display:none">
    Some settings on this machine are managed by your organization. Those fields are shown but can't be changed here.
  </div>

  <div class="card">
    <h2>Status</h2>
    <div class="kv"><span>Signed in as</span><strong id="signedIn">—</strong></div>
    <div class="kv"><span>Settings coming from</span><strong id="configSummary">—</strong></div>
    <div class="kv"><span>Version</span><strong id="version">—</strong></div>
    <div style="margin-top:14px"><button id="signinBtn">Sign in / switch account</button></div>
  </div>

  <div class="card">
    <h2>Startup</h2>
    <label class="check"><input type="checkbox" id="autostart"> Start OneDriveAsADrive when I sign in to Windows</label>
    <div class="hint" id="autostartVia">—</div>
    <div class="hint">
      This isn't a Windows service — a service runs with no desktop and no drive letters of its own,
      so it couldn't map your drives or show you a sign-in window. It runs as you, at sign-in, instead.
      To stop it right now and disconnect your drives, use <b>Turn off</b> on the tray icon next to
      the clock.
    </div>
  </div>

  <div class="card">
    <h2>Server</h2>
    <div class="row">
      <div class="field">
        <label for="port">Local port</label>
        <input id="port" type="number" min="1024" max="65535">
        <div class="managed" id="portManaged">Managed by your organization</div>
      </div>
      <div class="field">
        <label for="account">Account (optional)</label>
        <input id="account" type="text" placeholder="you@contoso.com" autocomplete="off" spellcheck="false">
        <div class="managed" id="accountManaged">Managed by your organization</div>
      </div>
    </div>
  </div>

  <div class="card">
    <h2>Drives</h2>
    <div id="mounts"></div>
    <div class="managed" id="mountsManaged">Your drives are managed by your organization</div>
    <button id="addBtn" class="link">+ Add a drive</button>
  </div>

  <div class="card">
    <div class="actions">
      <button id="saveBtn" class="primary">Save</button>
      <button id="remapBtn">Re-map drives now</button>
      <span class="status" id="status"></span>
    </div>
    <ul class="warn" id="warnings"></ul>
  </div>
</div>

<script>
const api = (p) => '/-/api/' + p;
let state = null;
let csrf = '';

const $ = (id) => document.getElementById(id);
const esc = (s) => (s ?? '').toString();

function setStatus(msg, kind) {
  const el = $('status');
  el.textContent = msg || '';
  el.className = 'status' + (kind ? ' ' + kind : '');
}

function showWarnings(list) {
  const ul = $('warnings');
  ul.innerHTML = '';
  (list || []).forEach(w => {
    const li = document.createElement('li');
    li.textContent = w;
    ul.appendChild(li);
  });
}

function mountRow(m, i, locked) {
  const wrap = document.createElement('div');
  wrap.className = 'mount';
  const isSp = (m.type || 'onedrive').toLowerCase() === 'sharepoint';

  const head = document.createElement('div');
  head.className = 'head';
  const title = document.createElement('strong');
  title.textContent = 'Drive ' + (m.letter || '?').toUpperCase() + ':';
  head.appendChild(title);
  if (!locked) {
    const del = document.createElement('button');
    del.className = 'link';
    del.textContent = 'Remove';
    del.onclick = () => { state.mounts.splice(i, 1); renderMounts(); };
    head.appendChild(del);
  }
  wrap.appendChild(head);

  const row = document.createElement('div');
  row.className = 'row';
  row.appendChild(field('Letter', input('text', m.letter, locked, v => {
    state.mounts[i].letter = v.toUpperCase().slice(0, 1);
    title.textContent = 'Drive ' + (state.mounts[i].letter || '?') + ':';
  }, 1)));
  row.appendChild(field('Type', select(['onedrive', 'sharepoint'], m.type || 'onedrive', locked, v => {
    state.mounts[i].type = v;
    renderMounts();
  })));
  row.appendChild(field('Label', input('text', m.name, locked, v => state.mounts[i].name = v)));
  wrap.appendChild(row);

  const sp = document.createElement('div');
  sp.className = 'sp' + (isSp ? ' on' : '');
  const spRow = document.createElement('div');
  spRow.className = 'row';
  spRow.appendChild(field('Site address', input('text', m.site, locked, v => state.mounts[i].site = v,
    null, 'contoso.sharepoint.com:/sites/Finance')));
  spRow.appendChild(field('Library (optional)', input('text', m.library, locked, v => state.mounts[i].library = v,
    null, 'Documents')));
  sp.appendChild(spRow);
  wrap.appendChild(sp);

  return wrap;
}

function field(labelText, control) {
  const d = document.createElement('div');
  d.className = 'field';
  const l = document.createElement('label');
  l.textContent = labelText;
  d.appendChild(l);
  d.appendChild(control);
  return d;
}

function input(type, value, disabled, onInput, maxLength, placeholder) {
  const el = document.createElement('input');
  el.type = type;
  el.value = esc(value);
  el.disabled = disabled;
  if (maxLength) el.maxLength = maxLength;
  if (placeholder) el.placeholder = placeholder;
  el.oninput = () => onInput(el.value);
  return el;
}

function select(options, value, disabled, onChange) {
  const el = document.createElement('select');
  options.forEach(o => {
    const opt = document.createElement('option');
    opt.value = o;
    opt.textContent = o === 'sharepoint' ? 'SharePoint' : 'OneDrive';
    if (o === (value || '').toLowerCase()) opt.selected = true;
    el.appendChild(opt);
  });
  el.disabled = disabled;
  el.onchange = () => onChange(el.value);
  return el;
}

function renderMounts() {
  const host = $('mounts');
  host.innerHTML = '';
  state.mounts.forEach((m, i) => host.appendChild(mountRow(m, i, state.locked.mounts)));
}

// The tick follows what the machine actually ended up doing, never what was clicked — registering
// a scheduled task can be refused outright, and on some locked-down machines the fallback is what
// answers. Both cases come back in the response, so both are shown.
function renderAutostart(a) {
  state.autostart = a || { enabled: false, description: '—' };
  $('autostart').checked = !!state.autostart.enabled;
  $('autostartVia').textContent = state.autostart.description || '—';
}

function render() {
  $('sub').textContent = 'Manage the drives this machine maps from OneDrive and SharePoint.';
  $('signedIn').textContent = state.signedInAs || 'Not signed in';
  $('configSummary').textContent = state.configSummary || '—';
  $('version').textContent = state.version;

  $('managedBanner').style.display = state.managed ? 'block' : 'none';

  renderAutostart(state.autostart);

  $('port').value = state.port;
  $('port').disabled = state.locked.port;
  $('portManaged').className = 'managed' + (state.locked.port ? ' on' : '');

  $('account').value = state.account || '';
  $('account').disabled = state.locked.account;
  $('accountManaged').className = 'managed' + (state.locked.account ? ' on' : '');

  $('mountsManaged').className = 'managed' + (state.locked.mounts ? ' on' : '');
  $('addBtn').style.display = state.locked.mounts ? 'none' : 'inline-block';
  $('saveBtn').disabled = state.locked.port && state.locked.account && state.locked.mounts;

  renderMounts();
  showWarnings(state.warnings);
}

// Every failed call funnels through here, so an expired page says the same thing whichever
// button you happened to press.
const EXPIRED = 'This settings page has expired. Open Settings again from the OneDriveAsADrive tray icon (next to the clock), or from the Start Menu.';

function fail(data, status, fallback) {
  setStatus((data && data.expired) || status === 403 || status === 401
    ? EXPIRED
    : ((data && data.error) || fallback), 'err');
}

async function load() {
  const res = await fetch(api('state'), { credentials: 'same-origin' });
  if (!res.ok) {
    setStatus(EXPIRED, 'err');
    return;
  }
  state = await res.json();
  csrf = state.csrfToken;
  render();
}

async function post(path, body) {
  const res = await fetch(api(path), {
    method: 'POST',
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json', 'X-CSRF-Token': csrf },
    body: body ? JSON.stringify(body) : '{}'
  });
  let data = {};
  try { data = await res.json(); } catch (e) { /* empty body on some errors */ }
  return { ok: res.ok, status: res.status, data };
}

$('addBtn').onclick = () => {
  state.mounts.push({ letter: '', type: 'onedrive', name: '' });
  renderMounts();
};

$('saveBtn').onclick = async () => {
  setStatus('Saving…');
  const payload = {
    port: parseInt($('port').value, 10),
    account: $('account').value,
    mounts: state.mounts
  };
  const { ok, status, data } = await post('settings', payload);
  if (!ok) { fail(data, status, 'Save failed.'); return; }
  showWarnings(data.warnings);
  setStatus(data.restartRequired
    ? 'Saved. Restart OneDriveAsADrive for the port or account change to take effect.'
    : 'Saved.', 'ok');
  await load();
};

$('remapBtn').onclick = async () => {
  setStatus('Re-mapping drives…');
  const { ok, status, data } = await post('remap');
  if (!ok) { fail(data, status, 'Re-map failed.'); return; }
  const bits = [];
  if (data.mapped && data.mapped.length) bits.push('mapped ' + data.mapped.join(', '));
  if (data.unmapped && data.unmapped.length) bits.push('removed ' + data.unmapped.join(', '));
  setStatus(bits.length ? bits.join('; ') : 'Nothing to change.', data.errors && data.errors.length ? 'err' : 'ok');
  showWarnings(data.errors);
};

$('autostart').onchange = async () => {
  const want = $('autostart').checked;
  setStatus(want ? 'Turning automatic start on…' : 'Turning automatic start off…');
  const { ok, status, data } = await post('autostart', { enabled: want });
  if (!ok) {
    $('autostart').checked = !want;   // it didn't happen, so don't leave the box claiming it did
    fail(data, status, 'Could not change the startup setting.');
    return;
  }
  renderAutostart(data.autostart);
  setStatus(data.autostart.enabled
    ? 'Automatic start is on — your drives connect at every sign-in.'
    : 'Automatic start is off — it will not start on its own.', 'ok');
};

$('signinBtn').onclick = async () => {
  const { ok, status, data } = await post('signin');
  if (!ok) { fail(data, status, 'Could not start sign-in.'); return; }
  setStatus(data.message, 'ok');
};

load();
</script>
</body>
</html>
""";

    // Shown when someone reaches the page without a valid session — a bookmarked URL after the
    // cookie expired, or a browser that arrived on its own. Tells them the one way in.
    public const string DeniedHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>OneDriveAsADrive Settings</title>
<style>
  :root { color-scheme: light dark; }
  body {
    margin: 0; min-height: 100vh; display: grid; place-items: center;
    font: 15px/1.6 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif;
    background: #f6f7f9; color: #1b1d21;
  }
  @media (prefers-color-scheme: dark) { body { background: #16181c; color: #e8eaed; } }
  .box { max-width: 420px; padding: 32px; text-align: center; }
  h1 { font-size: 18px; margin: 0 0 10px; }
  p { color: #6b7280; margin: 0 0 16px; }
  code { font-family: Consolas, "Cascadia Mono", monospace; font-size: 13px; }
</style>
</head>
<body>
  <div class="box">
    <h1>Open settings from the app</h1>
    <p>This page needs a session that only the app can start, so it can't be opened straight from a bookmark.</p>
    <p>Run this and it'll open for you:</p>
    <p><code>OneDriveAsADrive.exe --settings</code></p>
  </div>
</body>
</html>
""";
}
