// Provider-free Control Center XP checks. No listener or production settings access.
// node reign-xp-options.js <paired-workspace> <evidence-directory>
// Set REIGN_PLAYWRIGHT_MODULE to an installed Playwright package when needed.
const fs = require('fs');
const path = require('path');
const assert = require('assert/strict');
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');

async function main() {
  const root = path.resolve(process.argv[2] || process.cwd());
  const output = path.resolve(process.argv[3] || path.join(root, '.codex-build/xp-options-browser'));
  fs.mkdirSync(output, { recursive: true });
  const source = fs.readFileSync(path.join(root, 'ReignServer/src/Modules/Platform/ReignXpOptions.cs'), 'utf8');
  const program = fs.readFileSync(path.join(root, 'ReignServer/src/Modules/Platform/Program.cs'), 'utf8');
  const template = fs.readFileSync(path.join(root, 'ReignServer/ui/index.html'), 'utf8');
  const verbatim = (name) => {
    const match = source.match(new RegExp(name + '\\(\\) => @"([\\s\\S]*?)";'));
    assert.ok(match, name + ' must be the production verbatim literal');
    return match[1].replace(/""/g, '"');
  };
  const markup = verbatim('ReignXpOptionsHtml');
  const script = verbatim('ReignXpOptionsScript');
  assert.ok(program.includes('CodexPerformanceControlCenterScript() + ReignXpOptionsScript()'), 'Production page injects the XP script');
  assert.ok(program.includes('.Replace("@REIGN_XP_OPTIONS@", ReignXpOptionsHtml())'), 'Production page injects the XP markup');
  assert.ok(template.indexOf('@REIGN_XP_OPTIONS@') < template.indexOf("<section id='campaigns'"), 'Options must be a sibling page');
  const prefix = template.slice(0, template.indexOf("<section id='general'"));
  const tabsStart = template.indexOf('const mainTabs =');
  const tabsEnd = template.indexOf('function loadControlCenterPage(tab)', tabsStart);
  const navigation = template.slice(tabsStart, tabsEnd);
  const settingsFields = ['ids', 'boolIds', 'intIds', 'floatIds', 'secretIds'].map(name => {
    const declaration = template.match(new RegExp('const ' + name + ' = [^\\n]+'));
    assert.ok(declaration, 'Production field declaration: ' + name);
    return declaration[0];
  }).join('\n');
  const globalSave = template.slice(template.indexOf('async function saveSettings()'), template.indexOf('function updateReasoningPolicyHint()'));
  const settingsStubs = `const chatModelProfiles={}; function captureChatModels(){} function codexPerformanceLegacyReasoningForSave(){return {}} function codexPerformanceSettingsForSave(){return {}} async function loadSettings(){loadReignXpOptions(await (await fetch('/api/gameplay-options')).json())} async function loadSharedPortraitCatalog(){} async function loadSharedPortraitGenerationStatus(){} function updateImagePresetHints(){} function updateReasoningPolicyHint(){}`;
  const browser = await chromium.launch({ headless: true, channel: 'msedge' });
  const checks = [], screenshots = [], failures = [];
  try {
    for (const width of [800, 1280, 1920]) {
      const context = await browser.newContext({ viewport: { width, height: 1000 } });
      const page = await context.newPage();
      page.on('pageerror', error => failures.push(error.message));
      let saved = { reignXpEnabled: true, reignXpMultiplier: 1 }, mode = 'ok', pending;
      let posts = [];
      await context.route('**/*', async route => {
        const request = route.request(), url = new URL(request.url());
        assert.equal(url.origin, 'http://reign-xp.local', 'No external requests');
        if (request.method() === 'GET' && url.pathname === '/assets/ReignLogo.png')
          return route.fulfill({ contentType: 'image/png', body: fs.readFileSync(path.join(root, 'ReignServer/ui/assets/ReignLogo.png')) });
        if (request.method() === 'GET' && url.pathname === '/') {
          const html = prefix + "<section id='general' class='page active'></section>" + markup
            + '</main><script>' + navigation + '\nfunction loadControlCenterPage(){}\n' + settingsFields + '\n' + settingsStubs + '\n' + globalSave + '\n' + script
            + '\nloadReignXpOptions(' + JSON.stringify(saved) + ');</script></body></html>';
          return route.fulfill({ contentType: 'text/html', body: html });
        }
        if (request.method() === 'POST' && url.pathname === '/api/settings') {
          const payload = request.postDataJSON();
          posts.push(payload);
          if (posts.length <= 2) assert.deepEqual(Object.keys(payload).sort(), ['reignXpEnabled', 'reignXpMultiplier']);
          assert.equal(typeof payload.reignXpEnabled, 'boolean');
          assert.equal(typeof payload.reignXpMultiplier, 'number');
          if (mode === 'hold') await new Promise(resolve => { pending = resolve; });
          if (mode === 'error') return route.fulfill({ status: 400, json: { ok: false, error: 'Simulated save failure' } });
          saved = payload;
          return route.fulfill({ json: { ok: true } });
        }
        if (request.method() === 'GET' && url.pathname === '/api/gameplay-options') return route.fulfill({ json: saved });
        return route.fulfill({ status: 404, body: '' });
      });
      const open = async () => { await page.goto('http://reign-xp.local/'); await page.locator("#mainNavigation [data-tab='options']").click(); };
      const capture = async name => {
        const file = path.join(output, width + '-' + name + '.png');
        await page.screenshot({ path: file, fullPage: true }); screenshots.push(file);
        assert.ok(await page.locator('#options').isVisible());
        assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'No horizontal overflow');
      };
      await open();
      assert.equal(await page.locator('#reignXpMultiplierValue').textContent(), '1×');
      assert.equal(await page.locator("[data-tab='options']").getAttribute('aria-pressed'), 'true');
      await capture('default');
      await page.locator('#reignXpMultiplier').focus();
      await page.keyboard.press('Home');
      assert.equal(await page.locator('#reignXpMultiplierValue').textContent(), '0.25×');
      assert.match(await page.locator('#reignXpRewards').textContent(), /0.5 random skill XP/);
      await capture('minimum');
      await page.keyboard.press('End');
      assert.equal(await page.locator('#reignXpMultiplierValue').textContent(), '5×');
      await capture('maximum');
      await page.locator('#reignXpEnabled').uncheck();
      assert.ok(await page.locator('#reignXpMultiplier').isDisabled());
      assert.equal(await page.locator('#reignXpMultiplier').inputValue(), '5');
      await capture('disabled');
      mode = 'hold';
      await page.locator('#saveReignXpOptions').click();
      await page.locator('#reignXpStatus').filter({ hasText: 'Saving' }).waitFor();
      assert.ok(await page.locator('#saveReignXpOptions').isDisabled());
      await capture('saving');
      mode = 'ok'; pending();
      await page.locator('#reignXpStatus').filter({ hasText: 'Saved.' }).waitFor();
      await open();
      assert.equal(await page.locator('#reignXpEnabled').isChecked(), false);
      assert.equal(await page.locator('#reignXpMultiplier').inputValue(), '5');
      await page.locator('#reignXpEnabled').check();
      mode = 'error';
      await page.locator('#saveReignXpOptions').click();
      await page.locator('#reignXpStatus.xpError').waitFor();
      assert.equal(saved.reignXpEnabled, false, 'Failed save does not persist the new setting');
      assert.ok(await page.locator('#saveReignXpOptions').isEnabled());
      await capture('error');
      assert.equal(posts.length, 2);
      mode = 'ok';
      await page.locator('#reignXpMultiplier').focus();
      await page.keyboard.press('Home');
      await page.getByRole('button', { name: 'Save Settings', exact: true }).click();
      await page.locator('#status').filter({ hasText: 'Saved' }).waitFor();
      assert.equal(posts[2].reignXpEnabled, true);
      assert.equal(posts[2].reignXpMultiplier, .25);
      assert.equal(await page.locator('#reignXpMultiplier').inputValue(), '0.25');
      assert.equal(await page.locator('#reignXpStatus').textContent(), '');
      await capture('global-save');
      checks.push({ width, ok: true, scenarios: ['navigation', 'default', 'keyboard_minimum', 'keyboard_maximum', 'disabled_retains_value', 'partial_save', 'pending', 'reload', 'failed_save', 'global_save'], productionSettingsWrites: 0 });
      await context.close();
    }
    assert.deepEqual(failures, [], 'No browser script errors');
  } finally { await browser.close(); }
  const report = { ok: true, checks, screenshots, failures, providerCalls: 0, listeners: 0 };
  fs.writeFileSync(path.join(output, 'browser-report.json'), JSON.stringify(report, null, 2));
  process.stdout.write(JSON.stringify(report));
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
