// Uses the exact Verification Lab Control Center fixture. No real server/provider traffic.
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require(process.env.REIGN_PLAYWRIGHT_MODULE || 'playwright');
const [htmlPath, output] = process.argv.slice(2);
const html = await fs.readFile(htmlPath, 'utf8');
const reasoningCatalog = process.env.REIGN_CODEX_MODEL_CATALOG
  ? JSON.parse(await fs.readFile(process.env.REIGN_CODEX_MODEL_CATALOG,'utf8')).models
  : [{model:'fixture-all',supportedReasoningEfforts:['none','minimal','low','medium','high','xhigh','max','ultra']},
     {model:'fixture-limited',supportedReasoningEfforts:[{reasoningEffort:'low'},{reasoningEffort:'high'}]},
     {model:'fixture-unknown'}];
await fs.mkdir(output, { recursive: true });
const keys = ['dialogueModel','characterConstructionModel','diplomacyModel','eventsModel','fastModel','memoryModel',
  'relationshipModel','correspondenceModel','strategyModel','actionPlannerModel','actionRouterModel'];
const providers = ['nanogpt','openrouter','openai_compatible','codex_subscription'];
let settings = { llmProvider:'nanogpt', nanoGptApiKeyPresent:true, nanoGptApiKeyLength:20,
  openRouterApiKeyPresent:true, openRouterApiKeyLength:24, apiUrl:'https://fixture.invalid/chat',
  llmModelProfiles: Object.fromEntries(providers.map(p => [p,Object.fromEntries(keys.map(k => [k,p + '/' + k]))])),
  reasoningMode:'selective', reasoningEffort:'medium',
  codexOptions: {schema:'reign-codex-options-v1',version:1,reasoningMode:'selective',reasoningEffort:'medium',fastMode:false,
    structuredOutputs:false,compactMetadata:false,stablePromptMapping:false,parallelContextPreparation:false,reuseThreads:false,asyncThreadCleanup:false},
  codex: {runtimeVersion:'fixture-runtime',catalogFreshness:'fresh',discoveryErrors:[],models:[{id:'gpt-5.5',displayName:'GPT-5.5',supportedReasoningEfforts:['minimal','low','medium','high','xhigh']}],modelVerification:'unverified'} };
Object.assign(settings, settings.llmModelProfiles.nanogpt);
let globalKeys = null, globalSaves = [], globalClears = [], settingsGets = 0, globalSaveReply = null;
const dialogs = [];
let saved, codexCheckCalls = 0, lastCodexCheck, modelRefreshCalls = 0, failModelRefresh = false;
const results = [], errors = [];
const browser = await chromium.launch({channel:process.env.REIGN_BROWSER_CHANNEL || 'msedge',headless:true});
try {
  const page = await browser.newPage();
  page.on('pageerror', e => errors.push(e.message));
  page.on('dialog', dialog => { dialogs.push(dialog.message()); dialog.accept(); });
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
      else settingsGets++;
      body = {...settings,ok:true};
    }
    if (url.pathname === '/api/global-keys' && globalKeys) body = globalKeys;
    if (url.pathname === '/api/settings/clear-key') {
      globalClears.push({body:route.request().postDataJSON(),header:route.request().headers()['x-reign-keys']});
      body = {ok:true,outcome:'unbound'};
    }
    if (url.pathname === '/api/global-keys/save') {
      globalSaves.push({body:route.request().postDataJSON(),header:route.request().headers()['x-reign-keys']});
      body = globalSaveReply || {ok:true,outcome:'created',keyId:'99999999-2222-4333-8444-555555555555'};
    }
    if (url.pathname === '/api/codex/status') body = {...settings.codex,ok:true,models:settings.codex.models || []};
    if (url.pathname === '/api/codex/models/refresh') {
      modelRefreshCalls++;
      body = failModelRefresh ? {ok:false,error:'fixture discovery unavailable'} : {ok:true,models:[
        {model:'gpt-6.1-sol',displayName:'GPT-6.1 Sol'}, {slug:'gpt-5.6-terra',displayName:'GPT-5.6 Terra'}]};
      if (body.ok) settings.codex.models = body.models;
    }
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
      assert.equal(await page.locator('#codexLookupSettings').isVisible(),provider === 'codex_subscription');
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
  assert.deepEqual(await page.locator('#codexLookupMode option').evaluateAll(xs => xs.map(x => x.textContent.trim())),['Auto','Direct lookups']);
  assert.equal(await page.inputValue('#codexLookupMode'),'auto');
  assert.ok(await page.evaluate(() => !!(document.getElementById('llmProvider').compareDocumentPosition(document.getElementById('codexLookupMode')) & 4)));
  await page.evaluate(() => renderCodexModels([]));
  await page.selectOption('#llmProvider','nanogpt');
  const beforeDiscovery = modelRefreshCalls;
  await page.selectOption('#llmProvider','codex_subscription');
  await page.waitForFunction(() => [...document.getElementById('codexModelPicker').options].some(x => x.value === 'gpt-5.6-terra'));
  assert.equal(modelRefreshCalls,beforeDiscovery+1,'Selecting Codex automatically loads an empty catalog without generation');
  await page.selectOption('#codexModelPicker','gpt-5.6-terra');
  await page.evaluate(() => renderCodexStatus({ok:true,verificationStatus:'unverified'}));
  assert.equal(await page.inputValue('#codexModelPicker'),'gpt-5.6-terra','Unrelated status updates retain the loaded catalog and selected model');
  failModelRefresh = true;
  await page.evaluate(() => refreshCodexModels());
  assert.equal(await page.inputValue('#codexModelPicker'),'gpt-5.6-terra','Failed discovery retains the prior selection');
  assert.ok((await page.textContent('#codexStatus')).includes('fixture discovery unavailable'));
  failModelRefresh = false;
  await page.evaluate(() => refreshCodexModels());
  assert.equal(await page.inputValue('#codexModelPicker'),'gpt-5.6-terra','Successful refresh retains selection');
  await page.selectOption('#codexLookupMode','direct');
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
  assert.equal(await page.inputValue('#codexModelCandidate'),'codex_subscription/dialogueModel','The initial check target uses the Codex dialogue model');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.fill('#codexModelCandidate','gpt-6.1-sol');
  const dialogueBeforeCheck = await page.inputValue('#dialogueModel');
  await page.evaluate(() => saveSettings());
  assert.equal(await page.inputValue('#codexModelCandidate'),'gpt-6.1-sol','Saving retains the availability check target');
  await page.reload();
  await page.waitForFunction(() => document.getElementById('codexModelCandidate').value === 'gpt-6.1-sol');
  assert.equal(await page.inputValue('#dialogueModel'),dialogueBeforeCheck,'Changing the check target cannot change the game model');
  assert.equal(codexCheckCalls,0,'Saving or reloading the check target cannot send a provider request');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.locator('#codexModelAvailabilityButton').click();
  await page.waitForFunction(() => document.getElementById('codexModelVerificationStatus').textContent.includes('unverified'));
  assert.equal(codexCheckCalls,1);
  assert.equal(lastCodexCheck.model,'gpt-6.1-sol');
  assert.equal(lastCodexCheck.bounded,true);
  assert.equal(lastCodexCheck.maxRequests,1);
  assert.equal(lastCodexCheck.confirmation,'verify one Codex model request');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.codexOptions.schema,'reign-codex-options-v1');
  assert.equal(saved.codexOptions.fastMode,true);
  assert.equal(saved.codexOptions.reasoningEffort,'high');
  assert.equal(saved.codexOptions.stablePromptMapping,true);
  assert.equal(saved.codexOptions.structuredOutputs,false);
  assert.equal(saved.codexOptions.lookupMode,'direct');
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
  // Provider keys are never sent by the main Save (they go only to Global API Keys); other settings save and the key stays typed.
  const savedBeforeTypedKey = saved;
  await page.fill('#openRouterApiKey','fixture-replacement');
  await page.evaluate(() => saveSettings());
  assert.notEqual(saved,savedBeforeTypedKey);
  assert.equal(saved.openRouterApiKey,'','Main Save must not send a provider key');
  assert.equal(await page.inputValue('#openRouterApiKey'),'fixture-replacement','Main Save keeps the typed key');
  assert.match(await page.textContent('#status'),/typed key was kept/);
  await page.evaluate(() => { const el = document.getElementById('openRouterApiKey'); el.value = el.dataset.savedMask; });
  await page.evaluate(() => saveSettings());
  assert.notEqual(saved,savedBeforeTypedKey);
  assert.equal(saved.nanoGptApiKey,'');
  assert.equal(saved.codexOptions.fastMode,true,'Non-Codex save must preserve the Codex bank');
  assert.equal(saved.codexOptions.stablePromptMapping,true,'Non-Codex save must preserve independent Codex experiments');
  assert.equal(saved.codexOptions.lookupMode,'direct','Provider switching and non-Codex saves preserve the lookup selection');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('models').classList.add('active'); });
  const retired = ['highMemoryMode','memoryCheckerModel','memoryCheckerTimeoutMs','memoryEvidenceModel',
    'memoryEvidenceTimeoutMs','memoryEvidenceReasoningEffort','memoryEvidencePlanningReasoningEffort',
    'memoryEvidenceInputTokenBudget','memoryEvidenceAdaptiveRetrieval','memoryFinalCheckEnabled',
    'selectorModel','enableMinimeMemoryWorker','minimeTopicUrl','minimeMaxTopics','minimeTimeoutMs','minimeRetryCooldownSeconds','enableMinimeMemoryReranking','minimeRerankUrl','minimeRerankCandidateCount','minimeRerankTimeoutMs','minimeRerankWeight'];
  for (const key of retired) assert.equal(await page.locator('#' + key).count(),0,'Retired memory control must be absent: ' + key);
  await page.fill('#memoryModel','fixture/summary-writer');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.memoryModel,'fixture/summary-writer');
  assert.equal(saved.dialogueModel,'openai/router-draft');
  assert.equal(saved.llmModelProfiles.openrouter.memoryModel,'fixture/summary-writer');
  for (const key of retired) {
    assert.equal(Object.hasOwn(saved,key),false,'Settings save must not send retired key: ' + key);
    assert.ok(Object.values(saved.llmModelProfiles).every(bank => !Object.hasOwn(bank,key)));
  }
  for (const width of [1672,1024,600]) {
    await page.setViewportSize({width,height:941});
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth>innerWidth+1),false);
    const screenshot=path.join(output,`${width}-local-memory.png`);
    await page.locator('#models').screenshot({path:screenshot});
    results.push({width,retiredControlsAbsent:true,screenshot,passed:true});
  }
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','codex_subscription');
  for (const model of reasoningCatalog) {
    const id=model.model || model.id || model.slug;
    settings.codex.models=[model];
    await page.evaluate(model => {
      renderCodexModels([model]);
      document.getElementById('codexModelPicker').value=model.model || model.id || model.slug;
      applyCodexModelToAll();
    },model);
    const order=['none','minimal','low','medium','high','xhigh','max','ultra'];
    const supported=(model.supportedReasoningEfforts || model.reasoningEfforts || []).map(value => typeof value === 'string' ? value : value.reasoningEffort || value.effort);
    const choices=order.filter(value => !supported.length || supported.includes(value));
    assert.deepEqual(await page.locator('#reasoningEffort option').evaluateAll(options => options.map(x => x.value)),choices,`${id}: choices must match its advertised levels`);
    await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('models').classList.add('active'); });
    for (const effort of choices) {
      await page.selectOption('#reasoningEffort',effort);
      await page.evaluate(() => saveSettings());
      assert.equal(saved.codexOptions.reasoningEffort,effort,`${id}/${effort}: save must retain the exact choice`);
      assert.equal(saved.dialogueModel,id);
      assert.equal(saved.reasoningEffort,'medium','Model-specific Codex choices cannot change the other provider reasoning bank');
    }
    await page.reload();
    await page.waitForFunction(() => document.getElementById('llmProvider').value === 'codex_subscription');
    assert.equal(await page.inputValue('#reasoningEffort'),choices.at(-1),`${id}: saved choice survives reload`);
    results.push({model:id,reasoningEfforts:choices,saveReload:true,passed:true});
  }
  await page.reload();
  await page.waitForFunction(() => document.getElementById('llmProvider').value === 'codex_subscription');
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','codex_subscription');
  assert.equal(await page.inputValue('#codexLookupMode'),'direct','Direct selection survives a full settings reload and provider switch');
  for (const width of [1672,1024,600]) {
    await page.setViewportSize({width,height:941});
    assert.equal(await page.evaluate(() => document.documentElement.scrollWidth>innerWidth+1),false);
    const screenshot=path.join(output,`${width}-codex-direct.png`);
    await page.locator('#llm').screenshot({path:screenshot});
    results.push({width,lookupMode:'direct',reload:true,screenshot,passed:true});
  }
  // Global API Keys rows: no automatic selection, main Save cannot drop a typed key, per-row save sends the guarded create.
  const sharedKey = {keyId:'11111111-2222-4333-8444-555555555555',label:'Shared',preview:'...abcd',revision:2,configured:true};
  globalKeys = {ok:true,available:true,outcome:'listed',slots:[{slot:'openRouterApiKey',name:'OpenRouter',provider:'openrouter',
    origin:'https://openrouter.ai:443',legacy:true,binding:null,keys:[sharedKey]}]};
  await page.reload();
  await page.waitForFunction(() => !!document.getElementById('openRouterApiKeyGlobalRow'));
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','openrouter');
  assert.equal(await page.inputValue('#openRouterApiKeyGlobalRow select'),'','A single matching global key is never selected automatically');
  const savedBefore = saved;
  await page.fill('#openRouterApiKey','fixture-global');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.openRouterApiKey,'','Main Save must not send a typed provider key');
  assert.equal(await page.inputValue('#openRouterApiKey'),'fixture-global','Main Save must not drop a typed provider key');
  assert.match(await page.textContent('#status'),/Save key/);
  await page.selectOption('#openRouterApiKeyGlobalRow select','new');
  await page.click('#openRouterApiKeyGlobalRow button');
  for (let i = 0; i < 100 && !globalSaves.length; i++) await page.waitForTimeout(20);
  assert.deepEqual(globalSaves[0],{body:{slot:'openRouterApiKey',mode:'create',label:'',secret:'fixture-global',expectedKeyId:''},header:'1'});
  await page.waitForFunction(() => document.getElementById('openRouterApiKey').value === '');
  // Saving one key refreshes only its row: other unsaved settings and key inputs survive, and settings are not reloaded.
  globalKeys.slots.push({slot:'nanoGptApiKey',name:'NanoGPT text',provider:'nano_gpt',origin:'https://nano-gpt.com:443',legacy:false,binding:null,keys:[]});
  globalKeys.slots[0] = {...globalKeys.slots[0],legacy:false,binding:{keyId:sharedKey.keyId,provider:'openrouter',origin:'https://openrouter.ai:443',originMatches:true}};
  await page.evaluate(() => loadGlobalKeys());
  await page.fill('#nanoGptApiKey','other-pending-key');
  await page.evaluate(() => { document.getElementById('dialogueModel').value = 'pending-model'; });
  const getsBefore = settingsGets;
  await page.fill('#openRouterApiKeyGlobalRow input','Renamed');
  await page.click('#openRouterApiKeyGlobalRow button');
  for (let i = 0; i < 100 && globalSaves.length < 2; i++) await page.waitForTimeout(20);
  assert.equal(globalSaves[1].body.mode,'rotate');
  assert.equal(globalSaves[1].body.secret,'');
  assert.equal(globalSaves[1].body.expectedKeyId,sharedKey.keyId);
  assert.match(dialogs.at(-1),/Rename/,'A label-only change is a rename, not a value replacement');
  await page.waitForFunction(() => /Saved|updated|unknown|No change/.test(document.querySelector('#openRouterApiKeyGlobalRow [role=status]').textContent));
  assert.equal(settingsGets,getsBefore,'A key save must not reload all settings');
  assert.equal(await page.inputValue('#nanoGptApiKey'),'other-pending-key');
  assert.equal(await page.inputValue('#dialogueModel'),'pending-model');
  // Clearing a bound slot unbinds it through the guarded route; the global key itself is untouched.
  await page.evaluate(() => clearKey('openRouterApiKey'));
  assert.deepEqual(globalClears.at(-1),{body:{key:'openRouterApiKey'},header:'1'});
  assert.match(dialogs.at(-1),/stays in Global API Keys/);
  assert.equal(await page.inputValue('#nanoGptApiKey'),'other-pending-key');
  globalKeys.slots[0] = {...globalKeys.slots[0],legacy:false,binding:{keyId:sharedKey.keyId,provider:'openrouter',origin:'https://openrouter.ai:443',originMatches:true}};
  globalKeys.available = false;
  await page.reload();
  await page.waitForFunction(() => !!document.getElementById('openRouterApiKeyGlobalRow'));
  assert.equal(await page.locator('#openRouterApiKeyGlobalRow button').isDisabled(),true,'Stopped DwemerDistro disables key saves');
  assert.match(await page.textContent('#openRouterApiKeyGlobalRow [role=status]'),/unavailable/);
  // Not set up: rows are still shown (disabled, with the one-time setup step) and a typed key is kept, never saved locally.
  globalKeys = {ok:true,available:false,enrolled:false,outcome:'not_enrolled',slots:[{...globalKeys.slots[0],binding:null,legacy:false}]};
  await page.reload();
  await page.waitForFunction(() => !!document.getElementById('openRouterApiKeyGlobalRow'));
  assert.equal(await page.locator('#openRouterApiKeyGlobalRow select').isDisabled(),true);
  assert.match(await page.textContent('#openRouterApiKeyGlobalRow [role=status]'),/Connect-GlobalKeys/);
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','openrouter');
  const savedBeforeUnenrolled = saved;
  await page.fill('#openRouterApiKey','fixture-unenrolled');
  await page.evaluate(() => saveSettings());
  assert.notEqual(saved,savedBeforeUnenrolled);
  assert.equal(saved.openRouterApiKey,'');
  assert.equal(await page.inputValue('#openRouterApiKey'),'fixture-unenrolled');
  assert.match(await page.textContent('#status'),/Connect-GlobalKeys/);
  // Custom endpoint: a key typed for an unsaved URL is never sent; main Save stores the URL and keeps the key, then Save key
  // sends the shown URL for the server's check, and a server-side endpoint mismatch keeps the typed key.
  globalKeys = {ok:true,available:true,outcome:'listed',slots:[{slot:'apiKey',name:'Custom endpoint',provider:'custom_openai',
    origin:'https://fixture.invalid:443',legacy:false,binding:null,keys:[]}]};
  await page.reload();
  await page.waitForFunction(() => !!document.getElementById('apiKeyGlobalRow'));
  await page.evaluate(() => { document.querySelectorAll('.page').forEach(x => x.classList.remove('active')); document.getElementById('llm').classList.add('active'); });
  await page.selectOption('#llmProvider','openai_compatible');
  const savesBeforeEndpoint = globalSaves.length, oldApiUrl = await page.inputValue('#apiUrl');
  await page.fill('#apiUrl','https://new-endpoint.invalid/v1/chat/completions');
  await page.fill('#apiKey','fixture-custom-new');
  await page.selectOption('#apiKeyGlobalRow select','new');
  await page.click('#apiKeyGlobalRow button');
  assert.equal(globalSaves.length,savesBeforeEndpoint,'A key typed beside an unsaved API URL is never sent');
  assert.equal(await page.inputValue('#apiKey'),'fixture-custom-new');
  assert.match(await page.textContent('#apiKeyGlobalRow [role=status]'),/Save Changes/);
  assert.notEqual(oldApiUrl,'https://new-endpoint.invalid/v1/chat/completions');
  await page.evaluate(() => saveSettings());
  assert.equal(saved.apiUrl,'https://new-endpoint.invalid/v1/chat/completions');
  assert.equal(saved.apiKey,'');
  assert.equal(await page.inputValue('#apiKey'),'fixture-custom-new','Saving the URL keeps the typed endpoint key');
  globalSaveReply = {ok:false,outcome:'endpoint_changed'};
  await page.selectOption('#apiKeyGlobalRow select','new');
  await page.click('#apiKeyGlobalRow button');
  for (let i = 0; i < 100 && globalSaves.length === savesBeforeEndpoint; i++) await page.waitForTimeout(20);
  assert.deepEqual(globalSaves.at(-1).body,{slot:'apiKey',mode:'create',label:'',secret:'fixture-custom-new',expectedKeyId:'',
    expectedApiUrl:'https://new-endpoint.invalid/v1/chat/completions'});
  await page.waitForFunction(() => /Save Changes/.test(document.querySelector('#apiKeyGlobalRow [role=status]').textContent));
  assert.equal(await page.inputValue('#apiKey'),'fixture-custom-new','A server endpoint refusal keeps the typed key');
  globalSaveReply = null;
  await page.click('#apiKeyGlobalRow button');
  for (let i = 0; i < 100 && globalSaves.length < savesBeforeEndpoint + 2; i++) await page.waitForTimeout(20);
  await page.waitForFunction(() => document.getElementById('apiKey').value === '');
  assert.equal(globalSaves.at(-1).body.expectedApiUrl,'https://new-endpoint.invalid/v1/chat/completions');
  results.push({globalKeys:true,noAutoSelect:true,endpointGuard:true,mainSaveGuard:true,rowOnlyRefresh:true,unbind:true,rename:true,unavailable:true,notEnrolled:true,passed:true});
  globalKeys = null;
  assert.deepEqual(errors,[]);
  const report=path.join(output,'ui-report.json');
  await fs.writeFile(report,JSON.stringify({ok:true,isolated:true,htmlPath,results,errors,saveReload:true,maskedKeysPreserved:true},null,2));
  console.log(JSON.stringify({ok:true,report,screenshots:results.length}));
} finally { await browser.close(); }
