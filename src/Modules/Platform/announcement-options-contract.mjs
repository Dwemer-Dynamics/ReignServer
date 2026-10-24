// Provider-free browser proof against the compiled production HTML/JS fixture.
import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const fixturePath = path.resolve(process.argv[2]);
const output = path.resolve(process.argv[3]);
const serverRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8').replace(/^\uFEFF/, ''));
const template = fs.readFileSync(path.join(serverRoot, 'ui/index.html'), 'utf8');
const prefix = template.slice(0, template.indexOf("<section id='general'"));
const css = fs.readFileSync(path.join(serverRoot, 'src/Modules/Platform/ReignXpOptions.cs'), 'utf8').match(/<style>([\s\S]*?)<\/style>/)[1];
const switches = [...fixture.markup.matchAll(/type='checkbox' checked id='([^']+)'/g)].map(m => m[1]);
assert.equal(switches.length, 10);
assert.ok(switches.includes('announcementGovernmentEnabled'));
fs.mkdirSync(output, { recursive: true });
const report = { schema: 'reign-announcement-options-browser-v1', fixturePath, providerCalls: 0, liveSettingsAccess: false, checks: [], screenshots: [], errors: [] };
const browser = await chromium.launch({ headless: true, channel: 'msedge' });
try {
  for (const width of [800, 1280, 1920]) {
    const context = await browser.newContext({ viewport: { width, height: 1100 } });
    const page = await context.newPage();
    page.on('pageerror', error => report.errors.push(error.message));
    let saved = { ...Object.fromEntries(switches.map(k => [k, true])), unrelated: 'preserve' };
    let mode = 'ok', release, posts = [];
    await context.route('**/*', async route => {
      const request = route.request(), url = new URL(request.url());
      assert.equal(url.origin, 'http://news-options.local');
      if (url.pathname === '/assets/ReignLogo.png') return route.fulfill({ contentType: 'image/png', body: fs.readFileSync(path.join(serverRoot, 'ui/assets/ReignLogo.png')) });
      if (url.pathname === '/') return route.fulfill({ contentType: 'text/html', body: prefix + "<section id='options' class='page active'><style>" + css + '</style><h2>Options</h2>' + fixture.markup + '</section></main><script>const ids=[];const boolIds=new Set();\n' + fixture.script
        + "\nfetch('/api/settings').then(r=>{if(!r.ok)throw Error('load');return r.json()}).then(loadAnnouncementOptions).catch(()=>document.getElementById('announcementOptionsStatus').textContent='Could not load options. Refresh before saving.');</script></body></html>" });
      if (url.pathname === '/api/settings') {
        if (request.method() === 'GET') return route.fulfill(mode === 'load_error' ? { status: 503, json: { ok: false } } : { json: saved });
        const patch = request.postDataJSON(); posts.push(patch);
        assert.deepEqual(Object.keys(patch).sort(), [...switches].sort());
        assert.ok(Object.values(patch).every(v => typeof v === 'boolean'));
        if (mode === 'hold') await new Promise(resolve => release = resolve);
        if (mode === 'save_error') return route.fulfill({ status: 400, json: { ok: false, error: 'Simulated save failure' } });
        saved = { ...saved, ...patch }; return route.fulfill({ json: { ok: true } });
      }
      return route.fulfill({ status: 404, body: '' });
    });
    const open = async () => { await page.goto('http://news-options.local/'); await page.locator('#saveAnnouncementOptions').waitFor(); };
    const ready = () => page.waitForFunction(() => !document.getElementById('saveAnnouncementOptions').disabled);
    const capture = async label => {
      const file = path.join(output, `${width}-${label}.png`); await page.screenshot({ path: file, fullPage: true }); report.screenshots.push(file);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'No horizontal overflow');
      const bounds = await page.locator('#announcementOptions').boundingBox(); assert.ok(bounds.x >= 0 && bounds.x + bounds.width <= width + 1);
    };
    await open(); await ready();
    for (const id of switches) assert.ok(await page.locator('#' + id).isChecked());
    await capture('defaults');
    await page.locator('#announcementCapturesEnabled').uncheck();
    await page.locator('#announcementWorldEventsEnabled').uncheck();
    await page.locator('#announcementGovernmentEnabled').uncheck();
    await page.locator('#announcementPopupsEnabled').uncheck();
    mode = 'hold'; await page.locator('#saveAnnouncementOptions').click();
    await page.locator('#announcementOptionsStatus').filter({ hasText: 'Saving' }).waitFor();
    assert.ok(await page.locator('#saveAnnouncementOptions').isDisabled()); await capture('saving');
    mode = 'ok'; release(); await page.locator('#announcementOptionsStatus').filter({ hasText: 'Saved.' }).waitFor();
    assert.equal(saved.unrelated, 'preserve'); await open(); await ready();
    for (const id of ['announcementPopupsEnabled', 'announcementCapturesEnabled', 'announcementWorldEventsEnabled', 'announcementGovernmentEnabled']) assert.equal(await page.locator('#' + id).isChecked(), false);
    assert.ok(await page.locator('#announcementExecutionsEnabled').isChecked()); await capture('disabled');
    await page.locator('#announcementPopupsEnabled').check(); await capture('independent-types');
    await page.getByRole('button', { name: 'Reset to defaults' }).click();
    for (const id of switches) assert.ok(await page.locator('#' + id).isChecked());
    await page.locator('#saveAnnouncementOptions').click(); await page.locator('#announcementOptionsStatus').filter({ hasText: 'Saved.' }).waitFor();
    assert.ok(switches.every(k => saved[k]));
    mode = 'save_error'; await page.locator('#announcementSentencesEnabled').uncheck(); await page.locator('#saveAnnouncementOptions').click();
    await page.locator('#announcementOptionsStatus').filter({ hasText: 'Simulated save failure' }).waitFor();
    assert.equal(saved.announcementSentencesEnabled, true); assert.equal(await page.locator('#saveAnnouncementOptions').isDisabled(), false); await capture('save-error');
    mode = 'load_error'; const before = posts.length; await open();
    await page.locator('#announcementOptionsStatus').filter({ hasText: 'Could not load' }).waitFor();
    assert.ok(await page.locator('#saveAnnouncementOptions').isDisabled()); await page.evaluate(() => saveAnnouncementOptions()); assert.equal(posts.length, before); await capture('load-error');
    report.checks.push({ width, defaults: true, independentToggles: true, masterRetainsPreferences: true, roundtrip: true, patchScope: true, savingGuard: true, loadFailureGuard: true, saveFailure: true, reset: true, noOverflow: true });
    await context.close();
  }
  assert.deepEqual(report.errors, []); report.ok = true;
} catch (error) { report.ok = false; report.failure = error.stack; process.exitCode = 1; }
finally { await browser.close(); fs.writeFileSync(path.join(output, 'report.json'), JSON.stringify(report, null, 2)); console.log(JSON.stringify({ ok: report.ok, report: path.join(output, 'report.json') })); }
