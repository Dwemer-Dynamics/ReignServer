// Uses the exact Verification Lab Control Center fixture. No real server/provider traffic.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [htmlPath, output] = process.argv.slice(2);
const html = await fs.readFile(htmlPath, 'utf8');
await fs.mkdir(output, { recursive: true });
const keys = ['dialogueModel','characterConstructionModel','diplomacyModel','eventsModel','fastModel','memoryModel',
  'relationshipModel','correspondenceModel','strategyModel','selectorModel','actionPlannerModel','actionRouterModel'];
const providers = ['nanogpt','openrouter','openai_compatible','codex_subscription'];
let settings = { llmProvider:'nanogpt', nanoGptApiKeyPresent:true, nanoGptApiKeyLength:20,
  openRouterApiKeyPresent:true, openRouterApiKeyLength:24, apiUrl:'https://fixture.invalid/chat',
  llmModelProfiles: Object.fromEntries(providers.map(p => [p,Object.fromEntries(keys.map(k => [k,p + '/' + k]))])),
  reasoningMode:'selective', reasoningEffort:'medium',
  codexOptions: {schema:'reign-codex-options-v1',version:1,reasoningMode:'selective',reasoningEffort:'medium',fastMode:false,
    structuredOutputs:false,compactMetadata:false,stablePromptMapping:false,parallelContextPreparation:false,reuseThreads:false,asyncThreadCleanup:false},
  codex: {runtimeVersion:'fixture-runtime',catalogFreshness:'fresh',discoveryErrors:[],models:[{id:'gpt-5.5',displayName:'GPT-5.5',supportedReasoningEfforts:['minimal','low','medium','high','xhigh']}],modelVerification:'unverified'} };
Object.assign(settings, settings.llmModelProfiles.nanogpt);
let saved, codexCheckCalls = 0, lastCodexCheck;
const results = [], errors = [];
const browser = await chromium.launch({channel:process.env.REIGN_BROWSER_CHANNEL || 'msedge',headless:true});
try {
  const page = await browser.newPage();
  page.on('pageerror', e => errors.push(e.message));
  page.on('dialog', dialog => dialog.accept());
  await page.addInitScript(() => { window.setInterval = () => 0; });
  await page.route('**/*', async route => {
    const url = new URL(route.request().url());
    if (url.pathname === '/preview') return route.fulfill({contentType:'text/html',body:html});
    let body = {ok:true,campaigns:[],models:[]};
    if (url.pathname === '/api/settings') {
      if (route.request().method() === 'POST') {
        saved = route.request().postDataJSON();
        settings = {...settings,...saved};
      }
      body = {...settings,ok:true};
    }
    if (url.pathname === '/api/codex/status') body = {...settings.codex,ok:true,models:settings.codex.models || []};
    if (url.pathname === '/api/codex/models/verify' && route.request().method() === 'POST') {
      codexCheckCalls++;
      lastCodexCheck = route.request().postDataJSON();
      body = {ok:true,model:lastCodexCheck.model,status:'unverified',verificationStatus:'unverified',providerCalls:1,bounded:true,fixture:true};
    }
    await route.fulfill({contentType:'application/json',body:JSON.stringify(body)});
  });
  await page.goto('http://reign-preview.invalid/preview');
  await page.waitForFunction(() => document.getElementById('dialogueModel').value === 'nanogpt/dialogueModel');
  assert.deepEqual(await page.locator('#llmProvider option').evaluateAll(xs => xs.map(x => x.value)),providers);
  assert.equal(await page.locator('#codexPerformanceControls').count(),1,'Codex performance controls must be present in the Control Center fixture');
  assert.equal(await page.locator('#codexModelAvailability').count(),1,'Codex model verification must sit beside catalog discovery in the fixture');
  for (const width of [1672,1024,600]) {
    await page.setViewportSize({width,height:941});
    await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
    for (const provider of providers) {
      await page.selectOption('#llmProvider',provider);
      const state = await page.evaluate(({keys,provider}) => ({
        models:keys.map(k => document.getElementById(k).value),
        custom:document.getElementById('openAiCompatibleSettings').checkVisibility(),
        codex:document.getElementById('codexSubscriptionSettings').checkVisibility(),
        performance:(() => { const el=document.getElementById('codexPerformanceControls'); return !!el && !el.hidden && el.style.display !== 'none'; })(),
        fastMode:document.getElementById('codexFastMode')?.value,
        experiments:['structuredOutputs','compactMetadata','stablePromptMapping','parallelContextPreparation','reuseThreads','asyncThreadCleanup']
          .map(key => document.getElementById('codex' + key.charAt(0).toUpperCase() + key.slice(1))?.checked),
        fastAfterEffort:document.getElementById('reasoningEffort').compareDocumentPosition(document.getElementById('codexFastMode')),
        keyTypes:['nanoGptApiKey','openRouterApiKey'].map(k => document.getElementById(k).type),
        overflow:document.documentElement.scrollWidth > innerWidth+1,
        hint:document.getElementById('chatProviderHint').textContent,
        background:getComputedStyle(document.getElementById('openRouterApiKey')).backgroundColor
      }),{keys,provider});
      assert.deepEqual(state.models,keys.map(k => provider + '/' + k));
      assert.equal(state.custom,provider === 'openai_compatible');
      assert.equal(state.codex,provider === 'codex_subscription');
      assert.equal(state.performance,provider === 'codex_subscription');
      if (provider === 'codex_subscription') {
        assert.equal(state.fastMode,'off');
        assert.deepEqual(state.experiments,[false,false,false,false,false,false]);
        // compareDocumentPosition returns the DOM bitmask from the browser;
        // this assertion runs in Node, where the browser's global Node object
        // is intentionally unavailable. DOCUMENT_POSITION_FOLLOWING is 0x04.
        assert.ok((state.fastAfterEffort & 0x04) !== 0,'Fast mode must follow reasoning effort');
      }
      assert.equal(await page.locator('#promptCacheProbeButton').isDisabled(),provider !== 'nanogpt');
      assert.deepEqual(state.keyTypes,['password','password']);
      assert.equal(state.overflow,false);
      assert.equal(state.background,'rgb(11, 11, 11)');
      await page.locator('#openRouterApiKey').focus();
      const screenshot=path.join(output,`${width}-${provider}.png`);
      await page.locator('#llm').screenshot({path:screenshot});
      results.push({width,provider,passed:true,state,screenshot});
    }
    await page.selectOption('#llmProvider','codex_subscription');
    await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('models').classList.add('active'); });
    const performanceScreenshot=path.join(output,`${width}-codex-performance.png`);
    await page.locator('#models').screenshot({path:performanceScreenshot});
    results.push({width,provider:'codex_subscription',performanceScreenshot,performanceContract:true});
    await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  }
  await page.selectOption('#llmProvider','codex_subscription');
  // Provider selection lives on the LLM page while reasoning/Fast controls
  // live on the Models page. Keep the navigation explicit so the contract
  // exercises the same two-page flow as the Control Center.
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('models').classList.add('active'); });
  await page.selectOption('#reasoningEffort','high');
  await page.selectOption('#codexFastMode','on');
  await page.locator('#codexPerformanceExperiments').evaluate(el => { el.open = true; });
  await page.locator('#codexStablePromptMapping').check();
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','nanogpt');
  assert.equal(await page.inputValue('#reasoningEffort'),'medium','Non-Codex reasoning must be restored after leaving Codex');
  await page.selectOption('#llmProvider','codex_subscription');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('models').classList.add('active'); });
  assert.equal(await page.inputValue('#reasoningEffort'),'high');
  assert.equal(await page.inputValue('#codexFastMode'),'on');
  assert.equal(await page.locator('#codexStablePromptMapping').isChecked(),true);
  assert.equal(codexCheckCalls,0,'Catalog refresh and provider switching must not verify a model');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.locator('#codexModelAvailabilityButton').click();
  await page.waitForFunction(() => document.getElementById('codexModelVerificationStatus').textContent.includes('unverified'));
  assert.equal(codexCheckCalls,1);
  assert.equal(lastCodexCheck.model,'gpt-5.4');
  assert.equal(lastCodexCheck.bounded,true);
  assert.equal(lastCodexCheck.maxRequests,1);
  assert.equal(lastCodexCheck.confirmation,'verify one Codex model request');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.codexOptions.schema,'reign-codex-options-v1');
  assert.equal(saved.codexOptions.fastMode,true);
  assert.equal(saved.codexOptions.reasoningEffort,'high');
  assert.equal(saved.codexOptions.stablePromptMapping,true);
  assert.equal(saved.codexOptions.structuredOutputs,false);
  assert.equal(saved.reasoningEffort,'medium','Codex save must preserve the legacy/non-Codex reasoning value');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','nanogpt');
  await page.evaluate(() => { document.getElementById('dialogueModel').value='nano-draft'; document.getElementById('actionRouterModel').value='nano-router-draft'; });
  await page.selectOption('#llmProvider','openrouter');
  await page.evaluate(() => { document.getElementById('dialogueModel').value='openai/router-draft'; });
  await page.evaluate(() => saveSettings());
  assert.equal(saved.nanoGptApiKey,'');
  assert.equal(saved.openRouterApiKey,'');
  assert.equal(saved.llmModelProfiles.nanogpt.dialogueModel,'nano-draft');
  assert.equal(saved.llmModelProfiles.nanogpt.actionRouterModel,'nano-router-draft');
  assert.equal(saved.dialogueModel,'openai/router-draft');
  await page.selectOption('#llmProvider','nanogpt');
  assert.equal(await page.inputValue('#dialogueModel'),'nano-draft');
  await page.selectOption('#llmProvider','openrouter');
  assert.equal(await page.inputValue('#dialogueModel'),'openai/router-draft');
  await page.fill('#openRouterApiKey','fixture-replacement');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.openRouterApiKey,'fixture-replacement');
  assert.equal(saved.nanoGptApiKey,'');
  assert.equal(saved.codexOptions.fastMode,true,'Non-Codex save must preserve the Codex bank');
  assert.equal(saved.codexOptions.stablePromptMapping,true,'Non-Codex save must preserve independent Codex experiments');
  assert.deepEqual(errors,[]);
  const report=path.join(output,'ui-report.json');
  await fs.writeFile(report,JSON.stringify({ok:true,isolated:true,htmlPath,results,errors,saveReload:true,maskedKeysPreserved:true},null,2));
  console.log(JSON.stringify({ok:true,report,screenshots:results.length}));
} finally { await browser.close(); }
