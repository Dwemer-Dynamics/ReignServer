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
  const source = ['ReignXpOptions.cs','PartyBanterOptions.cs','PlayerApproachOptions.cs'].map(file =>
    fs.readFileSync(path.join(root, 'ReignServer/src/Modules/Platform', file), 'utf8')).join('\n');
  const program = fs.readFileSync(path.join(root, 'ReignServer/src/Modules/Platform/Program.cs'), 'utf8');
  const template = fs.readFileSync(path.join(root, 'ReignServer/ui/index.html'), 'utf8');
  const verbatim = (name) => {
    let position = source.indexOf(name + '() => ');
    assert.ok(position >= 0, name + ' must be the production expression');
    position += (name + '() => ').length;
    let result = '';
    while (position < source.length) {
      if (/\s|\+/.test(source[position])) { position++; continue; }
      if (source[position] === ';') return result;
      if (source.slice(position, position + 2) === '@"') {
        position += 2;
        while (position < source.length) {
          if (source[position] !== '"') { result += source[position++]; continue; }
          if (source[position + 1] === '"') { result += '"'; position += 2; continue; }
          position++; break;
        }
      } else {
        const call = source.slice(position).match(/^(\w+)\(\)/);
        assert.ok(call, 'Unexpected Options expression near ' + source.slice(position, position + 60));
        result += verbatim(call[1]); position += call[0].length;
      }
    }
    throw Error('Unterminated Options expression: ' + name);
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
  const injectionPosition = template.indexOf('@CODEX_PERFORMANCE_SCRIPT@');
  assert.ok(injectionPosition >= 0, 'Production Options script injection point exists');
  const settingsFields = ['ids', 'boolIds', 'intIds', 'floatIds', 'secretIds'].map(name => {
    const declaration = template.match(new RegExp('const ' + name + ' = [^\\n]+'));
    assert.ok(declaration, 'Production field declaration: ' + name);
    return { code: declaration[0], position: declaration.index };
  }).sort((a, b) => a.position - b.position);
  const fieldsBeforeOptions = settingsFields.filter(field => field.position < injectionPosition).map(field => field.code).join('\n');
  const fieldsAfterOptions = settingsFields.filter(field => field.position > injectionPosition).map(field => field.code).join('\n');
  const globalSave = template.slice(template.indexOf('async function saveSettings()'), template.indexOf('function updateReasoningPolicyHint()'))
    + template.slice(template.indexOf('async function controlCenterJson('), template.indexOf('function capturePromptDraft('));
  const settingsStubs = `const chatModelProfiles={}; function captureChatModels(){} function codexPerformanceLegacyReasoningForSave(){return {}} function codexPerformanceSettingsForSave(){return {}} async function savePromptDrafts(){} async function loadSettings(){loadReignXpOptions(await (await fetch('/api/gameplay-options')).json())} async function loadSharedPortraitCatalog(){} async function loadSharedPortraitGenerationStatus(){} function updateImagePresetHints(){} function updateReasoningPolicyHint(){}`;
  const browser = await chromium.launch({ headless: true, channel: 'msedge' });
  const checks = [], screenshots = [], failures = [];
  const categoryIds = ['reignXpConversationMultiplier', 'reignXpSocialCharmMultiplier', 'reignXpSocialBonusMultiplier', 'reignXpPetitionMultiplier'];
  const xpFieldIds = ['reignXpEnabled', 'reignXpMultiplier', ...categoryIds].sort();
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
            + '</main><script>' + fieldsBeforeOptions + '\n' + script + '\n' + fieldsAfterOptions + '\n' + navigation + '\nfunction loadControlCenterPage(){}\n' + settingsStubs + '\n' + globalSave
            + '\nloadReignXpOptions(' + JSON.stringify(saved) + ');</script></body></html>';
          return route.fulfill({ contentType: 'text/html', body: html });
        }
        if (request.method() === 'POST' && url.pathname === '/api/settings') {
          const payload = request.postDataJSON();
          posts.push(payload);
          if (posts.length <= 2) assert.deepEqual(Object.keys(payload).sort(), xpFieldIds);
          if ('reignXpEnabled' in payload) assert.equal(typeof payload.reignXpEnabled, 'boolean');
          if ('reignXpMultiplier' in payload) assert.equal(typeof payload.reignXpMultiplier, 'number');
          for (const id of categoryIds) if (id in payload) assert.equal(typeof payload[id], 'number');
          if (mode === 'hold') await new Promise(resolve => { pending = resolve; });
          if (mode === 'error') return route.fulfill({ status: 400, json: { ok: false, error: 'Simulated save failure' } });
          saved = { ...saved, ...payload };
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
      for (const id of categoryIds) assert.equal(await page.locator('#' + id).inputValue(), '1', 'Legacy missing category defaults to 1: ' + id);
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
      const categoryValues = [0, .25, 2, 5];
      for (let i = 0; i < categoryIds.length; i++) {
        await page.locator('#' + categoryIds[i]).fill(String(categoryValues[i]));
        await page.locator('#' + categoryIds[i]).dispatchEvent('input');
      }
      assert.equal(await page.locator('#reignXpRewards').textContent(), '0 Charm per spoken exchange; 6.25 Charm per social phase; 20 random skill XP per social phase; 250 Leadership per decided petition.');
      await capture('categories-combined');
      await page.locator('#reignXpEnabled').uncheck();
      assert.ok(await page.locator('#reignXpMultiplier').isDisabled());
      assert.equal(await page.locator('#reignXpMultiplier').inputValue(), '5');
      for (let i = 0; i < categoryIds.length; i++) {
        assert.ok(await page.locator('#' + categoryIds[i]).isDisabled());
        assert.equal(await page.locator('#' + categoryIds[i]).inputValue(), String(categoryValues[i]));
      }
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
      for (let i = 0; i < categoryIds.length; i++) assert.equal(await page.locator('#' + categoryIds[i]).inputValue(), String(categoryValues[i]), 'Partial save/reload retains category');
      await page.locator('#reignXpEnabled').check();
      await page.locator('#reignXpPetitionMultiplier').fill('3');
      await page.locator('#reignXpPetitionMultiplier').dispatchEvent('input');
      mode = 'error';
      await page.locator('#saveReignXpOptions').click();
      await page.locator('#reignXpStatus.xpError').waitFor();
      assert.equal(saved.reignXpEnabled, false, 'Failed save does not persist the new setting');
      assert.equal(saved.reignXpPetitionMultiplier, 5, 'Failed save preserves persisted category');
      assert.ok(await page.locator('#saveReignXpOptions').isEnabled());
      await capture('error');
      assert.equal(posts.length, 2);
      mode = 'ok';
      await page.locator('#reignXpMultiplier').focus();
      await page.keyboard.press('Home');
      await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
      await page.locator('#status').filter({ hasText: 'Changes saved' }).waitFor();
      assert.equal(posts[2].reignXpEnabled, true);
      assert.equal(posts[2].reignXpMultiplier, .25);
      for (let i = 0; i < categoryIds.length; i++) assert.equal(posts[2][categoryIds[i]], i === 3 ? 3 : categoryValues[i], 'Global save uses numeric categories');
      assert.equal(await page.locator('#reignXpMultiplier').inputValue(), '0.25');
      assert.equal(await page.locator('#reignXpStatus').textContent(), '');
      await capture('global-save');
      const approachIds = await page.evaluate(() => playerApproachOptionIds);
      assert.equal(approachIds.length, 7);
      for (const id of approachIds) assert.equal(await page.locator('#' + id).isChecked(), true, 'Legacy missing option defaults on: ' + id);
      await page.locator('#playerJealousyApproachesEnabled').uncheck();
      await page.locator('#playerPatrolInterceptionsEnabled').uncheck();
      await page.locator('#playerApproachesEnabled').uncheck();
      for (const id of approachIds.slice(1)) assert.ok(await page.locator('#' + id).isDisabled(), 'Master disables ' + id);
      await capture('approaches-master-off');
      mode = 'hold';
      await page.locator('#savePlayerApproachOptions').click();
      await page.locator('#playerApproachOptionsStatus').filter({ hasText: 'Saving' }).waitFor();
      assert.ok(await page.locator('#savePlayerApproachOptions').isDisabled());
      assert.deepEqual(Object.keys(posts[3]).sort(), approachIds.slice().sort(), 'Partial save contains only approach options');
      assert.ok(Object.values(posts[3]).every(value => typeof value === 'boolean'));
      await capture('approaches-saving');
      mode = 'ok'; pending();
      await page.locator('#playerApproachOptionsStatus').filter({ hasText: 'Saved.' }).waitFor();
      await open();
      assert.equal(await page.locator('#playerApproachesEnabled').isChecked(), false);
      assert.equal(await page.locator('#playerJealousyApproachesEnabled').isChecked(), false);
      assert.equal(await page.locator('#playerPatrolInterceptionsEnabled').isChecked(), false);
      await page.locator('#playerApproachesEnabled').check();
      for (const id of approachIds.slice(1)) assert.ok(await page.locator('#' + id).isEnabled());
      assert.equal(await page.locator('#playerJealousyApproachesEnabled').isChecked(), false, 'Master retains child preference');
      await capture('approaches-mixed');
      mode = 'error';
      await page.locator('#savePlayerApproachOptions').click();
      await page.locator('#playerApproachOptionsStatus.xpError').waitFor();
      assert.equal(saved.playerApproachesEnabled, false, 'Failed approach save preserves persisted choices');
      assert.ok(await page.locator('#savePlayerApproachOptions').isEnabled());
      await capture('approaches-error');
      mode = 'ok';
      // Each element independently survives a global save and full reload.
      for (const selected of approachIds.slice(1)) {
        for (const id of approachIds.slice(1)) await page.locator('#' + id).setChecked(id !== selected);
        await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
        await page.locator('#status').filter({ hasText: 'Changes saved' }).waitFor();
        await open();
        for (const id of approachIds.slice(1)) assert.equal(await page.locator('#' + id).isChecked(), id !== selected);
      }
      await page.locator('#playerPrisonRescueDialogueEnabled').focus();
      await page.keyboard.press('Space');
      assert.ok(await page.locator('#playerPrisonRescueDialogueEnabled').isChecked(), 'Keyboard toggles rescue switch');
      await capture('approaches-keyboard');
      for (const id of categoryIds) {
        await page.locator('#' + id).focus();
        await page.keyboard.press('Home');
        assert.equal(await page.locator('#' + id).inputValue(), '0');
        await page.keyboard.press('End');
        assert.equal(await page.locator('#' + id).inputValue(), '5');
      }
      // Each category remains distinct through both production save paths and full reload.
      for (const saveButton of ['#saveReignXpOptions', '#saveChangesButton']) {
        for (let selected = 0; selected < categoryIds.length; selected++) {
          for (let i = 0; i < categoryIds.length; i++) {
            await page.locator('#' + categoryIds[i]).fill(i === selected ? '0' : '1');
            await page.locator('#' + categoryIds[i]).dispatchEvent('input');
          }
          if (saveButton === '#saveReignXpOptions') {
            await page.locator(saveButton).click();
            await page.locator('#reignXpStatus').filter({ hasText: 'Saved.' }).waitFor();
            assert.deepEqual(Object.keys(posts.at(-1)).sort(), xpFieldIds);
          } else {
            await page.getByRole('button', { name: 'Save Changes', exact: true }).click();
            await page.locator('#status').filter({ hasText: 'Changes saved' }).waitFor();
          }
          await open();
          for (let i = 0; i < categoryIds.length; i++) assert.equal(await page.locator('#' + categoryIds[i]).inputValue(), i === selected ? '0' : '1');
        }
      }
      await capture('categories-independent-reload');
      checks.push({ width, ok: true, scenarios: ['production_field_initialization_order', 'navigation', 'default', 'keyboard_minimum', 'keyboard_maximum', 'disabled_retains_value', 'partial_save', 'pending', 'reload', 'failed_save', 'global_save', 'approach_legacy_defaults', 'approach_master_retains_children', 'approach_partial_save', 'approach_save_pending', 'approach_save_failure', 'six_independent_global_saves_and_reloads', 'approach_keyboard', 'category_legacy_defaults', 'category_combined_rewards', 'category_disabled_retention', 'category_failed_save_preserves_values', 'four_category_keyboard_bounds', 'four_independent_partial_saves_and_reloads', 'four_independent_global_saves_and_reloads'], productionSettingsWrites: 0 });
      await context.close();
    }
    assert.deepEqual(failures, [], 'No browser script errors');
  } finally { await browser.close(); }
  const report = { ok: true, checks, screenshots, failures, providerCalls: 0, listeners: 0 };
  fs.writeFileSync(path.join(output, 'browser-report.json'), JSON.stringify(report, null, 2));
  process.stdout.write(JSON.stringify(report));
}
main().catch(error => { console.error(error.stack); process.exitCode = 1; });
