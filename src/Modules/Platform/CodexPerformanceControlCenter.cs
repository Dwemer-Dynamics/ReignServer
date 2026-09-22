namespace ReignBetaServer
{
    internal static partial class Program
    {
        // The host page inserts this immediately after the existing reasoning-effort label.
        // The control is intentionally provider-scoped in the markup and in the script.  A
        // provider switch therefore cannot accidentally expose or apply Codex-only settings.
        private static string CodexPerformanceControlCenterHtml() => @"
        <section id='codexPerformanceControls' class='codexPerformanceControls' hidden aria-label='Codex performance controls'>
          <label>Fast mode
            <select id='codexFastMode' aria-describedby='codexFastModeHint'>
              <option value='off'>Off</option>
              <option value='on'>On</option>
            </select>
          </label>
          <p id='codexFastModeHint' class='hint'>Fast mode requests the supported faster processing tier for Codex requests. It may increase usage; it does not guarantee a faster end-to-end response and never changes the selected model, reasoning effort, output contract, or repair policy.</p>
          <div id='codexFastModeStatus' class='editorStatus' role='status' aria-live='polite'></div>

          <details id='codexPerformanceExperiments'>
            <summary>Codex performance experiments</summary>
            <p class='hint'>These switches are optional, independent experiments. They apply only to Codex text requests and are shipped off until you enable them. An unsupported capability remains visible with its reported status.</p>
            <label><input id='codexStructuredOutputs' type='checkbox' data-codex-option='structuredOutputs'> Structured output schemas</label>
            <p class='hint'>Request a typed response contract when the current Reign contract is supported; semantic validation and repair remain active.</p>
            <label><input id='codexCompactMetadata' type='checkbox' data-codex-option='compactMetadata'> Compact response metadata</label>
            <p class='hint'>Ask for concise metadata while preserving evidence identifiers, uncertainty, classifications, and durable writes.</p>
            <label><input id='codexStablePromptMapping' type='checkbox' data-codex-option='stablePromptMapping'> Stable prompt mapping</label>
            <p class='hint'>Reuse the existing stable rule, character, and current-turn segments when the runtime reports compatible cache support.</p>
            <label><input id='codexParallelContextPreparation' type='checkbox' data-codex-option='parallelContextPreparation'> Parallel context preparation</label>
            <p class='hint'>Prepare independent context reads concurrently with a maximum of four isolated workers when a consistent snapshot is available.</p>
            <label><input id='codexReuseThreads' type='checkbox' data-codex-option='reuseThreads'> Reuse short conversation threads</label>
            <p class='hint'>Reuse only ordinary conversation threads with compatible authoritative context. Repairs, private scopes, and invalidated identities use fresh threads.</p>
            <label><input id='codexAsyncThreadCleanup' type='checkbox' data-codex-option='asyncThreadCleanup'> Clean up threads asynchronously</label>
            <p class='hint'>Return the validated result without waiting for bounded deletion of inactive Reign-owned threads.</p>
          </details>
          <pre id='codexCapabilityStatus'>Capability support is unknown until the Codex runtime reports it.</pre>
        </section>";

        // The host page inserts this directly beside the existing free catalog-discovery
        // controls.  Keeping it separate makes the paid verification boundary obvious.
        private static string CodexModelAvailabilityHtml() => @"
          <div id='codexModelAvailability' class='card codexModelAvailabilityCard'>
            <h3>Codex model availability</h3>
            <p class='hint'>Discovery is free and reads the paginated runtime catalog, including hidden entries. Verification is separate, consumes Codex usage, and sends one explicitly bounded test request. GPT-5.4 remains unverified until you run that action.</p>
            <label>Candidate model
              <input id='codexModelCandidate' value='gpt-5.4' spellcheck='false' autocomplete='off' aria-describedby='codexModelCandidateHint'>
            </label>
            <div class='actions'>
              <button id='codexModelAvailabilityButton' onclick='checkCodexModelAvailability()'>Check model availability</button>
            </div>
            <p id='codexModelCandidateHint' class='hint'>Use an exact runtime model ID. Refreshing the catalog and saving settings never runs verification.</p>
            <pre id='codexCatalogStatus'>Catalog status has not been loaded.</pre>
            <pre id='codexModelVerificationStatus'>No model verification has been run.</pre>
          </div>";

        // The host page inserts this after ChatProviderScript in the same script block.  The
        // functions are deliberately public-to-the-host (ordinary JS globals) so the existing
        // load/save code can call the two explicit settings hooks without duplicating state.
        private static string CodexPerformanceControlCenterScript() => @"
    const codexPerformanceOptionKeys = ['structuredOutputs','compactMetadata','stablePromptMapping','parallelContextPreparation','reuseThreads','asyncThreadCleanup'];
    const codexPerformanceDefaults = {
      schema: 'reign-codex-options-v1', version: 1, reasoningMode: 'selective', reasoningEffort: 'medium', fastMode: false,
      structuredOutputs: false, compactMetadata: false, stablePromptMapping: false,
      parallelContextPreparation: false, reuseThreads: false, asyncThreadCleanup: false
    };
    let codexPerformanceOptions = {...codexPerformanceDefaults};
    let codexPerformanceNonCodexReasoning = null;
    let codexPerformanceProvider = '';
    const codexPerformanceReasoningEfforts = ['none','minimal','low','medium','high','xhigh','max','ultra'];

    function codexPerformanceBool(value) {
      return value === true || value === 1 || value === '1' || String(value || '').toLowerCase() === 'true' || String(value || '').toLowerCase() === 'on';
    }
    function codexPerformanceNormalize(raw, legacy) {
      const source = raw && typeof raw === 'object' ? raw : {};
      const old = legacy && typeof legacy === 'object' ? legacy : {};
      const result = {...codexPerformanceDefaults};
      result.reasoningMode = ['selective','off','all'].includes(String(source.reasoningMode || old.reasoningMode || result.reasoningMode)) ? String(source.reasoningMode || old.reasoningMode || result.reasoningMode) : result.reasoningMode;
      result.reasoningEffort = codexPerformanceReasoningEfforts.includes(String(source.reasoningEffort || old.reasoningEffort || result.reasoningEffort)) ? String(source.reasoningEffort || old.reasoningEffort || result.reasoningEffort) : result.reasoningEffort;
      result.fastMode = codexPerformanceBool(source.fastMode);
      codexPerformanceOptionKeys.forEach(key => { result[key] = codexPerformanceBool(source[key]); });
      return result;
    }
    function codexPerformanceIsSelected() {
      return (document.getElementById('llmProvider')?.value || '') === 'codex_subscription';
    }
    function codexPerformanceReadControls() {
      const mode = document.getElementById('reasoningMode')?.value || codexPerformanceOptions.reasoningMode;
      const effort = document.getElementById('reasoningEffort')?.value || codexPerformanceOptions.reasoningEffort;
      codexPerformanceOptions.reasoningMode = ['selective','off','all'].includes(mode) ? mode : 'selective';
      codexPerformanceOptions.reasoningEffort = codexPerformanceReasoningEfforts.includes(effort) ? effort : 'medium';
      const fast = document.getElementById('codexFastMode');
      codexPerformanceOptions.fastMode = fast ? fast.value === 'on' : !!codexPerformanceOptions.fastMode;
      codexPerformanceOptionKeys.forEach(key => {
        const input = document.getElementById('codex' + key.charAt(0).toUpperCase() + key.slice(1));
        if (input) codexPerformanceOptions[key] = !!input.checked;
      });
      return {...codexPerformanceOptions, schema: 'reign-codex-options-v1', version: 1};
    }
    function codexPerformanceApplyControls(options) {
      codexPerformanceOptions = codexPerformanceNormalize(options, null);
      const mode = document.getElementById('reasoningMode');
      const effort = document.getElementById('reasoningEffort');
      if (mode) mode.value = codexPerformanceOptions.reasoningMode;
      if (effort) {
        const value = codexPerformanceOptions.reasoningEffort;
        if (!effort.querySelector('option[value=""' + value + '""]')) {
          const option = document.createElement('option');
          option.value = value;
          option.textContent = value === 'none' ? 'None' : value === 'xhigh' ? 'Extra high' : value.charAt(0).toUpperCase() + value.slice(1);
          effort.appendChild(option);
        }
        effort.value = value;
      }
      const fast = document.getElementById('codexFastMode');
      if (fast) fast.value = codexPerformanceOptions.fastMode ? 'on' : 'off';
      codexPerformanceOptionKeys.forEach(key => {
        const id = 'codex' + key.charAt(0).toUpperCase() + key.slice(1);
        const input = document.getElementById(id);
        if (input) input.checked = !!codexPerformanceOptions[key];
      });
      codexPerformanceRenderStatus();
    }
    function codexPerformanceCaptureBeforeProviderChange() {
      // The select's value has already changed when onchange invokes the host
      // handler.  The existing provider script keeps the old selection in
      // displayedChatProvider until changeChatProvider completes, so use that
      // identity to capture the correct bank.
      const provider = (typeof displayedChatProvider === 'string' && displayedChatProvider) || document.getElementById('llmProvider')?.value || '';
      if (provider === 'codex_subscription') codexPerformanceReadControls();
      else {
        codexPerformanceNonCodexReasoning = {
          reasoningMode: document.getElementById('reasoningMode')?.value || 'selective',
          reasoningEffort: document.getElementById('reasoningEffort')?.value || 'medium'
        };
      }
    }
    function codexPerformanceApplyProvider(provider) {
      const controls = document.getElementById('codexPerformanceControls');
      if (!controls) return;
      const selected = provider === 'codex_subscription';
      controls.hidden = !selected;
      controls.style.display = selected ? 'block' : 'none';
      if (selected) {
        if (!codexPerformanceNonCodexReasoning) codexPerformanceNonCodexReasoning = {
          reasoningMode: document.getElementById('reasoningMode')?.value || 'selective',
          reasoningEffort: document.getElementById('reasoningEffort')?.value || 'medium'
        };
        codexPerformanceApplyControls(codexPerformanceOptions);
      } else if (codexPerformanceNonCodexReasoning) {
        const mode = document.getElementById('reasoningMode');
        const effort = document.getElementById('reasoningEffort');
        if (mode) mode.value = codexPerformanceNonCodexReasoning.reasoningMode;
        if (effort) effort.value = codexPerformanceNonCodexReasoning.reasoningEffort;
      }
    }
    function codexPerformanceSetLoadedSettings(settings) {
      const s = settings && typeof settings === 'object' ? settings : {};
      codexPerformanceOptions = codexPerformanceNormalize(s.codexOptions, s);
      codexPerformanceNonCodexReasoning = {
        reasoningMode: s.reasoningMode || 'selective', reasoningEffort: s.reasoningEffort || 'medium'
      };
      codexPerformanceProvider = s.llmProvider || '';
      if (codexPerformanceProvider === 'codex_subscription') codexPerformanceApplyControls(codexPerformanceOptions);
      codexPerformanceRenderCatalogStatus(s.codex || s.codexStatus || {});
      codexPerformanceApplyProvider(codexPerformanceProvider);
    }
    function codexPerformanceSettingsForSave() {
      if (codexPerformanceIsSelected()) codexPerformanceReadControls();
      return {...codexPerformanceOptions, schema: 'reign-codex-options-v1', version: 1};
    }
    function codexPerformanceLegacyReasoningForSave() {
      if (codexPerformanceIsSelected() && codexPerformanceNonCodexReasoning) return {...codexPerformanceNonCodexReasoning};
      return {
        reasoningMode: document.getElementById('reasoningMode')?.value || 'selective',
        reasoningEffort: document.getElementById('reasoningEffort')?.value || 'medium'
      };
    }
    function codexPerformanceRenderStatus() {
      const output = document.getElementById('codexFastModeStatus');
      if (!output) return;
      output.textContent = codexPerformanceOptions.fastMode ? 'Fast mode requested; runtime capability status will be reported per request.' : 'Fast mode is off.';
      output.className = 'editorStatus';
    }
    function codexPerformanceRenderCatalogStatus(status) {
      const output = document.getElementById('codexCatalogStatus');
      if (!output) return;
      const value = status && typeof status === 'object' ? status : {};
      const catalog = value.catalog || value.modelCatalog || value;
      const verification = value.gpt54Verification || value.modelVerification || value.verificationStatus || value.verification || catalog.gpt54Verification || catalog.modelVerification || 'unverified';
      const rows = {
        runtimeVersion: value.runtimeVersion || catalog.runtimeVersion || 'unknown',
        catalogFreshness: value.catalogFreshness || value.freshness || catalog.freshness || 'unknown',
        discoveryErrors: value.discoveryErrors || value.errors || catalog.discoveryErrors || catalog.discoveryError || [],
        supportedReasoningEfforts: value.supportedReasoningEfforts || catalog.supportedReasoningEfforts || (value.models || catalog.models || []).map(model => ({id:model.id || '', efforts:model.supportedReasoningEfforts || model.reasoningEfforts || []})),
        modelVerification: verification
      };
      output.textContent = JSON.stringify(rows, null, 2);
      const capability = document.getElementById('codexCapabilityStatus');
      if (capability) {
        const reported = value.capabilities || value.capabilityStatus;
        const protocol = value.protocol || {};
        const fallback = {
          fastMode: protocol.supportsFastMode === true ? 'supported' : protocol.supportsFastMode === false ? 'unsupported' : 'unknown',
          structuredOutputSchemas: protocol.supportsOutputSchema === true ? 'supported' : protocol.supportsOutputSchema === false ? 'unsupported' : 'unknown',
          requested: 'unknown', applied: 'unknown', unsupported: 'unknown', unknown: 'unknown'
        };
        capability.textContent = JSON.stringify(reported || fallback, null, 2);
      }
      if (verification && typeof verification === 'object') codexPerformanceRenderVerification(verification);
    }
    function codexPerformanceRenderVerification(result) {
      const output = document.getElementById('codexModelVerificationStatus');
      if (!output) return;
      output.textContent = JSON.stringify(result || {status:'unknown'}, null, 2);
      const button = document.getElementById('codexModelAvailabilityButton');
      if (button) button.disabled = false;
    }
    async function checkCodexModelAvailability() {
      const candidate = (document.getElementById('codexModelCandidate')?.value || '').trim();
      if (!candidate) return;
      if (!window.confirm('Check availability by sending one bounded Codex model request? This consumes Codex usage.')) return;
      const output = document.getElementById('codexModelVerificationStatus');
      const button = document.getElementById('codexModelAvailabilityButton');
      if (button) button.disabled = true;
      if (output) output.textContent = 'Verification is explicitly gated and sends one bounded Codex test request...';
      try {
        const response = await fetch('/api/codex/models/verify', {method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({model:candidate, maxRequests:1, bounded:true, confirmation:'verify one Codex model request'})});
        const result = await response.json();
        codexPerformanceRenderVerification(result);
      } catch (e) {
        codexPerformanceRenderVerification({status:'unknown', inconclusive:true, error:e.message});
      }
    }
    ['reasoningMode','reasoningEffort','codexFastMode',...codexPerformanceOptionKeys.map(key => 'codex' + key.charAt(0).toUpperCase() + key.slice(1))].forEach(id => {
      document.getElementById(id)?.addEventListener('change', () => {
        if (codexPerformanceIsSelected()) codexPerformanceReadControls();
        codexPerformanceRenderStatus();
      });
    });
";

    }
}
