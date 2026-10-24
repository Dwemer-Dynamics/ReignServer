// Provider-free browser proof using markup and script exported by the compiled gossip suite.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const fixturePath = path.resolve(process.argv[2]), output = path.resolve(process.argv[3]);
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8').replace(/^\uFEFF/, ''));
const template = fs.readFileSync(path.join(root, 'ui/index.html'), 'utf8');
const prefix = template.slice(0, template.indexOf("<section id='general'"));
const css = fs.readFileSync(path.join(root, 'src/Modules/Platform/ReignXpOptions.cs'), 'utf8').match(/<style>([\s\S]*?)<\/style>/)[1];
const globalSave = template.slice(template.indexOf('async function saveSettings()'), template.indexOf('function updateReasoningPolicyHint()'));
const numberFields = ['intIds', 'floatIds', 'secretIds'].map(name => template.match(new RegExp('const ' + name + ' = [^\\n]+'))[0]).join('\n');
const stubs = "const chatModelProfiles={};function captureChatModels(){}function npcLivesSettingsForSave(){return {}}function codexPerformanceLegacyReasoningForSave(){return {}}function codexPerformanceSettingsForSave(){return {}}async function savePromptDrafts(){}async function loadSettings(){loadPersonalGossipOptions(await(await fetch('/api/settings')).json())}function updateImagePresetHints(){}function updateReasoningPolicyHint(){}async function controlCenterJson(url,options){const r=await fetch(url,options),d=await r.json();if(!r.ok||d.ok===false)throw Error(d.error||'save');return d;}";
const keys = Object.keys(fixture.defaults), master = 'personalGossipEnabled';
assert.equal(keys.length, 6);
fs.mkdirSync(output, { recursive: true });
const report = { schema: 'reign-personal-gossip-options-browser-v1', fixturePath, providerCalls: 0, liveSettingsAccess: false, checks: [], screenshots: [], errors: [] };
const browser = await chromium.launch({ headless: true, channel: 'msedge' });
try {
  for (const width of [800, 1280, 1920, 2560]) {
    const context = await browser.newContext({ viewport: { width, height: 1100 } });
    const page = await context.newPage(); page.on('pageerror', e => report.errors.push(e.message));
    let saved = { ...fixture.defaults, [master]: true, unrelated: 'retain' }, mode = 'ok', posts = [], release;
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      assert.equal(url.origin, 'http://gossip-options.local');
      if (url.pathname === '/assets/ReignLogo.png') return route.fulfill({ contentType: 'image/png', body: fs.readFileSync(path.join(root, 'ui/assets/ReignLogo.png')) });
      if (url.pathname === '/') return route.fulfill({ contentType: 'text/html', body: prefix + "<section id='options' class='page active'><style>" + css + '</style><h2>Options</h2>' + fixture.markup + '</section></main><script>const ids=[];const boolIds=new Set();\n' + fixture.script
        + '\n' + numberFields + '\n' + stubs + '\n' + globalSave
        + "\nfetch('/api/settings').then(r=>{if(!r.ok)throw Error('load');return r.json()}).then(loadPersonalGossipOptions).catch(()=>document.getElementById('personalGossipOptionsStatus').textContent='Could not load options. Refresh before saving.');</script></body></html>" });
      if (url.pathname === '/api/settings') {
        if (request.method() === 'GET') return route.fulfill(mode === 'load_error' ? { status: 503, json: { ok: false } } : { json: saved });
        const patch = request.postDataJSON(); posts.push(patch);
        assert.deepEqual(Object.keys(patch).filter(k => !['llmModelProfiles','codexOptions'].includes(k)).sort(), [master, ...keys].sort());
        assert.equal(typeof patch[master], 'boolean');
        assert.ok(keys.every(k => Number.isInteger(patch[k]) && patch[k] >= -100 && patch[k] <= 100));
        if (mode === 'hold') await new Promise(resolve => release = resolve);
        if (mode === 'save_error') return route.fulfill({ status: 400, json: { ok: false, error: 'Simulated save failure' } });
        saved = { ...saved, ...patch }; return route.fulfill({ json: { ok: true } });
      }
      return route.fulfill({ status: 404, body: '' });
    });
    const status = page.locator('#personalGossipOptionsStatus'), button = page.locator('#savePersonalGossipOptions');
    const open = async () => { await page.goto('http://gossip-options.local/'); await button.waitFor(); };
    const ready = () => page.waitForFunction(() => !document.getElementById('savePersonalGossipOptions').disabled);
    const capture = async label => {
      const file = path.join(output, `${width}-${label}.png`); await page.screenshot({ path: file, fullPage: true }); report.screenshots.push(file);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'No horizontal overflow');
      const bounds = await page.locator('#personalGossipOptions').boundingBox(); assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width + 1);
      for (const key of keys) { assert.equal(await page.locator('#' + key).getAttribute('min'), '-100'); assert.equal(await page.locator('#' + key).getAttribute('max'), '100'); }
    };
    await open(); await ready();
    for (const key of keys) { assert.equal(await page.locator('#' + key).inputValue(), '0'); assert.equal(await page.locator('#' + key + 'Value').textContent(), 'Standard'); }
    await capture('standard');
    const first = page.locator('#' + keys[0]); await first.focus(); await first.press('End'); assert.equal(await first.inputValue(), '100');
    await first.press('Home'); assert.equal(await first.inputValue(), '-100'); await first.press('ArrowRight'); assert.equal(await first.inputValue(), '-99');
    for (let i = 0; i < keys.length; i++) await page.locator('#' + keys[i]).press(i % 2 ? 'Home' : 'End');
    await capture('adjusted'); mode = 'hold'; await button.click(); await status.filter({ hasText: 'Saving' }).waitFor();
    assert.ok(await button.isDisabled()); assert.ok(await first.isDisabled());
    const postCount = posts.length; await page.evaluate(() => savePersonalGossipOptions()); assert.equal(posts.length, postCount); await capture('saving');
    mode = 'ok'; release(); await status.filter({ hasText: 'Saved.' }).waitFor(); assert.equal(saved.unrelated, 'retain');
    await open(); await ready(); for (let i = 0; i < keys.length; i++) assert.equal(await page.locator('#' + keys[i]).inputValue(), String(i % 2 ? -100 : 100));
    await page.locator('#' + master).uncheck(); assert.ok(await first.isDisabled());
    await button.click(); await status.filter({ hasText: 'Saved.' }).waitFor(); await open(); await ready();
    assert.equal(await page.locator('#' + master).isChecked(), false); assert.equal(await first.inputValue(), '100'); await capture('disabled');
    await page.locator('#resetPersonalGossipOptions').click(); assert.ok(await page.locator('#' + master).isChecked());
    for (const key of keys) assert.equal(await page.locator('#' + key).inputValue(), '0');
    assert.equal(saved[master], false, 'Reset is a draft until saved'); await button.click(); await status.filter({ hasText: 'Saved.' }).waitFor();
    assert.ok(keys.every(key => saved[key] === 0)); assert.equal(saved[master], true);
    await first.press('ArrowRight'); await page.locator('#saveChangesButton').click();
    await page.locator('#status').filter({ hasText: 'Changes saved' }).waitFor();
    assert.equal(saved[keys[0]], 1); await open(); await ready(); assert.equal(await first.inputValue(), '1');
    await page.locator('#resetPersonalGossipOptions').click(); await button.click(); await status.filter({ hasText: 'Saved.' }).waitFor();
    mode = 'save_error'; await first.press('ArrowRight'); await button.click(); await status.filter({ hasText: 'Simulated save failure' }).waitFor();
    assert.equal(saved[keys[0]], 0); assert.equal(await button.isDisabled(), false); await capture('save-error');
    mode = 'load_error'; const before = posts.length; await open(); await status.filter({ hasText: 'Could not load' }).waitFor();
    assert.ok(await button.isDisabled()); await page.evaluate(() => savePersonalGossipOptions()); assert.equal(posts.length, before); await capture('load-error');
    report.checks.push({ width, defaults: true, independentSliders: true, keyboardEndpoints: true, roundtrip: true, globalSaveNumericRoundtrip: true, patchScope: true, duplicateSaveGuard: true, savingGuard: true, masterRetainsPreferences: true, loadFailureGuard: true, saveFailure: true, resetRequiresSave: true, noOverflow: true });
    await context.close();
  }
  assert.deepEqual(report.errors, []); report.ok = true;
} catch (error) { report.ok = false; report.failure = error.stack; process.exitCode = 1; }
finally { await browser.close(); fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ ok: report.ok, report: path.join(output, 'report.json') })); }
