#!/usr/bin/env node
'use strict';

/*
 * onedriveasadrive — no-code front-end for the OneDriveAsADrive WebDAV bridge.
 *
 * This is a thin wrapper: the actual work is done by the native Windows exe + install.ps1.
 * The CLI just edits config.json, maps/unmaps drives, and shells the installer. Node has no
 * business touching WAM or Graph — it's the friendly face, like Brian doing Peter's taxes.
 *
 * Commands:
 *   install [--config <file>]                      download, verify, register background task, map drives
 *   add --letter S --type sharepoint --site ... [--library ...] [--name ...] [--machine]
 *   remove <letter> [--machine]                    drop a mount + unmap the drive
 *   list                                           show configured mounts
 *   status                                         is the server running? which drives are mapped?
 *   debug                                          run the exe with a visible console (admin/troubleshoot)
 *   uninstall                                      remove task, unmap drives, delete files
 */

const fs = require('fs');
const os = require('os');
const path = require('path');
const https = require('https');
const { spawnSync } = require('child_process');

const REPO = 'Avatorsinc/OneDriveAsADrive';
const APPNAME = 'OneDriveAsADrive';

const C = {
  reset: '\x1b[0m', cyan: '\x1b[36m', green: '\x1b[32m', red: '\x1b[31m', yellow: '\x1b[33m', dim: '\x1b[90m'
};
const log = (m) => console.log('  ' + m);
const ok = (m) => console.log(`  ${C.green}[OK]${C.reset} ${m}`);
const warn = (m) => console.log(`  ${C.yellow}[!]${C.reset} ${m}`);
const die = (m) => { console.error(`  ${C.red}[!!]${C.reset} ${m}`); process.exit(1); };

function requireWindows() {
  if (process.platform !== 'win32') die('OneDriveAsADrive only runs on Windows.');
}

// ── paths ─────────────────────────────────────────────────────────────────────
const installDir = path.join(process.env.LOCALAPPDATA || '', APPNAME);
const exePath = path.join(installDir, `${APPNAME}.exe`);
const secretPath = path.join(installDir, '.secret');
const userConfig = path.join(process.env.LOCALAPPDATA || '', APPNAME, 'config.json');
const machineConfig = path.join(process.env.ProgramData || '', APPNAME, 'config.json');

// ── arg parsing ────────────────────────────────────────────────────────────────
function parseFlags(argv) {
  const flags = {}; const pos = [];
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) { flags[key] = true; }
      else { flags[key] = next; i++; }
    } else pos.push(a);
  }
  return { flags, pos };
}

// ── config helpers ───────────────────────────────────────────────────────────
function configPathFor(flags) { return flags.machine ? machineConfig : userConfig; }

function readConfig(p) {
  if (!fs.existsSync(p)) return { port: 8080, mounts: [] };
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); }
  catch { die(`Malformed config: ${p}`); }
}
function writeConfig(p, cfg) {
  fs.mkdirSync(path.dirname(p), { recursive: true });
  fs.writeFileSync(p, JSON.stringify(cfg, null, 2) + '\n', 'utf8');
}
// The config that's actually in effect: machine wins over user (matches the exe's Load order).
function effectiveConfig() {
  if (fs.existsSync(machineConfig)) return { path: machineConfig, cfg: readConfig(machineConfig) };
  if (fs.existsSync(userConfig)) return { path: userConfig, cfg: readConfig(userConfig) };
  return { path: null, cfg: { port: 8080, mounts: [{ letter: 'Z', type: 'onedrive', name: 'OneDrive' }] } };
}

function readSecret() {
  if (!fs.existsSync(secretPath)) return null;
  return fs.readFileSync(secretPath, 'utf8').trim();
}

// ── shelling out ─────────────────────────────────────────────────────────────
function run(cmd, args, opts = {}) {
  return spawnSync(cmd, args, { stdio: 'inherit', ...opts });
}
// Explorer shows WebDAV drives as the raw "\\localhost@8080\s" path unless we set a friendly
// label. The MountPoints2 key for \\localhost@PORT\x is ##localhost@PORT#x; _LabelFromReg is
// what Explorer displays, so "S: (Finance)" instead of "S: (\\localhost@8080)".
function mountPointKey(letter, port) {
  return `HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\MountPoints2\\##localhost@${port}#${letter.toLowerCase()}`;
}
function setDriveLabel(letter, port, label) {
  if (!label) return;
  spawnSync('reg', ['add', mountPointKey(letter, port), '/v', '_LabelFromReg', '/t', 'REG_SZ', '/d', label, '/f'], { stdio: 'ignore' });
}
function netUseMap(letter, port, secret, label) {
  const drive = `${letter.toUpperCase()}:`;
  const url = `http://localhost:${port}/${letter.toLowerCase()}/`;
  spawnSync('net', ['use', drive, '/delete', '/y'], { stdio: 'ignore' });
  const r = spawnSync('net', ['use', drive, url, `/user:onedrive`, secret, '/persistent:yes'],
    { encoding: 'utf8' });
  if (r.status !== 0) warn(`net use ${drive} failed: ${(r.stderr || r.stdout || '').trim()}`);
  else { setDriveLabel(letter, port, label); ok(`Mapped ${drive} -> ${url}${label ? ` ("${label}")` : ''}`); }
}
function netUseDelete(letter, port) {
  const drive = `${letter.toUpperCase()}:`;
  spawnSync('net', ['use', drive, '/delete', '/y'], { stdio: 'ignore' });
  if (port) spawnSync('reg', ['delete', mountPointKey(letter, port), '/f'], { stdio: 'ignore' });
  ok(`Unmapped ${drive}`);
}

// Synchronous sleep (the CLI is entirely synchronous; no event loop to yield to).
function sleep(ms) { Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms); }

// The exe reads config.json only at startup, so after we edit the config we must bounce the
// background server or it'll keep serving the old set of prefixes. Kill it, re-run the task
// (or relaunch hidden), and give it a moment to bind the port + write its secret.
function restartBackground() {
  spawnSync('taskkill', ['/IM', `${APPNAME}.exe`, '/F'], { stdio: 'ignore' });
  const r = spawnSync('schtasks', ['/Run', '/TN', APPNAME], { stdio: 'ignore' });
  if (r.status !== 0 && fs.existsSync(exePath)) {
    // No task registered (or it refused) — launch the exe hidden and detached ourselves.
    require('child_process').spawn(exePath, [], { detached: true, stdio: 'ignore', windowsHide: true }).unref();
  }
  sleep(3000);
}

// Follow redirects and download a URL to disk.
function download(url, dest) {
  return new Promise((resolve, reject) => {
    https.get(url, { headers: { 'User-Agent': 'onedriveasadrive-cli' } }, (res) => {
      if ([301, 302, 307, 308].includes(res.statusCode)) {
        res.resume();
        return resolve(download(res.headers.location, dest));
      }
      if (res.statusCode !== 200) { res.resume(); return reject(new Error(`HTTP ${res.statusCode} for ${url}`)); }
      const file = fs.createWriteStream(dest);
      res.pipe(file);
      file.on('finish', () => file.close(resolve));
    }).on('error', reject);
  });
}

// ── commands ──────────────────────────────────────────────────────────────────
async function cmdInstall(flags) {
  requireWindows();
  log('Downloading installer from the latest release...');
  const ps1 = path.join(os.tmpdir(), 'onedriveasadrive-install.ps1');
  const url = `https://github.com/${REPO}/releases/latest/download/install.ps1`;
  try { await download(url, ps1); } catch (e) { die(`Could not download installer: ${e.message}`); }
  ok('Installer downloaded');

  const psArgs = ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ps1];
  if (flags.config) psArgs.push('-Config', String(flags.config));
  if (flags.port) psArgs.push('-Port', String(flags.port));
  log('Running installer (a UAC / admin prompt may appear)...');
  const r = run('powershell', psArgs);
  process.exit(r.status || 0);
}

function cmdAdd(flags) {
  requireWindows();
  const letter = (flags.letter || '').toString().replace(':', '').trim();
  if (!letter || letter.length !== 1) die('--letter must be a single drive letter, e.g. --letter S');
  const type = (flags.type || 'sharepoint').toString().toLowerCase();
  if (type === 'sharepoint' && !flags.site)
    die('SharePoint mounts need --site, e.g. --site contoso.sharepoint.com:/sites/Finance');

  const p = configPathFor(flags);
  const cfg = readConfig(p);
  if (!cfg.mounts) cfg.mounts = [];
  if (!cfg.port) cfg.port = Number(flags.port) || effectiveConfig().cfg.port || 8080;

  cfg.mounts = cfg.mounts.filter((m) => (m.letter || '').toLowerCase() !== letter.toLowerCase());
  const mount = { letter: letter.toUpperCase(), type };
  if (type === 'sharepoint') {
    mount.site = String(flags.site);
    if (flags.library) mount.library = String(flags.library);
  }
  mount.name = flags.name ? String(flags.name) : (type === 'sharepoint' ? String(flags.site) : 'OneDrive');
  cfg.mounts.push(mount);

  try { writeConfig(p, cfg); }
  catch (e) { die(`Could not write ${p}: ${e.message}${flags.machine ? ' (machine config needs admin)' : ''}`); }
  ok(`Added ${mount.letter}: (${mount.name}) to ${p}`);

  // Map it now if the server is up and we have a secret. The running exe read config.json only
  // at startup, so it has no idea this new prefix exists — mapping without a restart would just
  // 404. Bounce the background server first so it picks up the new mount, THEN map.
  const secret = readSecret();
  if (!secret) {
    warn('Server not installed yet. Run: onedriveasadrive install');
    return;
  }
  log('Restarting background server to load the new mount...');
  restartBackground();
  if (type === 'sharepoint')
    warn('First SharePoint mount widens Graph scopes — a one-time consent prompt may appear. ' +
         'If the drive looks empty, run: onedriveasadrive debug');
  netUseMap(letter, cfg.port, secret, mount.name);
}

function cmdRemove(pos, flags) {
  requireWindows();
  const letter = (pos[0] || flags.letter || '').toString().replace(':', '').trim();
  if (!letter) die('Usage: onedriveasadrive remove <letter>');
  const p = configPathFor(flags);
  const cfg = readConfig(p);
  const before = (cfg.mounts || []).length;
  cfg.mounts = (cfg.mounts || []).filter((m) => (m.letter || '').toLowerCase() !== letter.toLowerCase());
  if (cfg.mounts.length === before) warn(`No mount ${letter}: in ${p}`);
  else { writeConfig(p, cfg); ok(`Removed ${letter.toUpperCase()}: from ${p}`); }
  netUseDelete(letter, cfg.port);
}

function cmdList() {
  const { path: p, cfg } = effectiveConfig();
  console.log(`\n  Config: ${C.cyan}${p || 'defaults (single OneDrive on Z:)'}${C.reset}`);
  console.log(`  Port:   ${cfg.port || 8080}\n`);
  for (const m of cfg.mounts) {
    const detail = m.type === 'sharepoint'
      ? `sharepoint  ${m.site}${m.library ? ' / ' + m.library : ''}`
      : 'onedrive';
    console.log(`   ${C.green}${(m.letter || '?').toUpperCase()}:${C.reset}  ${m.name || ''}  ${C.dim}(${detail})${C.reset}`);
  }
  console.log('');
}

function cmdStatus() {
  requireWindows();
  const running = spawnSync('tasklist', ['/FI', `IMAGENAME eq ${APPNAME}.exe`], { encoding: 'utf8' });
  const isUp = (running.stdout || '').includes(`${APPNAME}.exe`);
  console.log(`\n  Server:  ${isUp ? C.green + 'running' : C.red + 'not running'}${C.reset}`);
  console.log(`  Exe:     ${fs.existsSync(exePath) ? exePath : C.yellow + 'not installed' + C.reset}`);
  console.log(`  Secret:  ${fs.existsSync(secretPath) ? 'present' : C.yellow + 'missing' + C.reset}`);
  const nu = spawnSync('net', ['use'], { encoding: 'utf8' });
  console.log('\n  Mapped drives (net use):');
  console.log((nu.stdout || '').split('\n').filter((l) => /localhost/i.test(l)).map((l) => '   ' + l.trim()).join('\n') || '   (none)');
  console.log('');
}

function cmdDebug() {
  requireWindows();
  if (!fs.existsSync(exePath)) die('Not installed. Run: onedriveasadrive install');
  // Only one instance can own the port. Kill the hidden background instance first, or this
  // console one dies with "address already in use". It restarts at next logon (or via install).
  spawnSync('taskkill', ['/IM', `${APPNAME}.exe`, '/F'], { stdio: 'ignore' });
  log('Launching with a visible console (Ctrl+C to stop)...');
  log('Note: the hidden background instance is stopped for this session; it returns at next logon.');
  run(exePath, ['--console']);
}

function cmdUninstall() {
  requireWindows();
  const { cfg } = effectiveConfig();
  for (const m of cfg.mounts || []) netUseDelete(m.letter, cfg.port);
  spawnSync('schtasks', ['/Delete', '/TN', APPNAME, '/F'], { stdio: 'ignore' });
  spawnSync('taskkill', ['/IM', `${APPNAME}.exe`, '/F'], { stdio: 'ignore' });
  try { fs.rmSync(path.join(process.env.APPDATA || '', 'Microsoft\\Windows\\Start Menu\\Programs\\Startup', `${APPNAME}.lnk`), { force: true }); } catch {}
  try { fs.rmSync(installDir, { recursive: true, force: true }); } catch {}
  ok('Removed task, drives, and files.');
  warn('Machine-wide WebDAV setting (BasicAuthLevel) left as-is. To revert (admin):');
  console.log(`   Set-ItemProperty "HKLM:\\SYSTEM\\CurrentControlSet\\Services\\WebClient\\Parameters" -Name BasicAuthLevel -Value 1`);
}

function help() {
  console.log(`
  ${C.cyan}onedriveasadrive${C.reset} — mount OneDrive & SharePoint as Windows drives

  ${C.green}install${C.reset} [--config <file>] [--port N]   Install + run + map drives
  ${C.green}add${C.reset} --letter S --type sharepoint       Add a mount and map it now
        --site contoso.sharepoint.com:/sites/Finance
        [--library Documents] [--name "Finance"] [--machine]
  ${C.green}add${C.reset} --letter O --type onedrive         Add your personal OneDrive
  ${C.green}remove${C.reset} <letter> [--machine]            Remove a mount + unmap
  ${C.green}list${C.reset}                                   Show configured mounts
  ${C.green}status${C.reset}                                 Server + mapped-drive status
  ${C.green}debug${C.reset}                                  Run with a visible console
  ${C.green}uninstall${C.reset}                              Remove everything

  --machine writes config to %ProgramData% (all users, needs admin) for IT deployment.
  Docs: https://github.com/${REPO}
`);
}

(async function main() {
  const [, , cmd, ...rest] = process.argv;
  const { flags, pos } = parseFlags(rest);
  switch ((cmd || '').toLowerCase()) {
    case 'install': await cmdInstall(flags); break;
    case 'add': cmdAdd(flags); break;
    case 'remove': case 'rm': cmdRemove(pos, flags); break;
    case 'list': case 'ls': cmdList(); break;
    case 'status': cmdStatus(); break;
    case 'debug': case 'console': cmdDebug(); break;
    case 'uninstall': cmdUninstall(); break;
    case undefined: case 'help': case '--help': case '-h': help(); break;
    default: warn(`Unknown command: ${cmd}`); help(); process.exit(1);
  }
})();
