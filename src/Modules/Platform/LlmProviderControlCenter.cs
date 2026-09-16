namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string ChatProviderKeysHtml() => @"
        <p id='chatProviderHint' class='hint'></p>
        <h3>Saved provider keys</h3>
        <p class='hint'>Keys stay saved when you switch providers. OpenRouter chat and image profiles share one key.</p>
        <label>NanoGPT chat key <input id='nanoGptApiKey' type='password' autocomplete='off'></label>
        <div class='actions'><button class='danger' onclick=""clearKey('nanoGptApiKey')"">Clear NanoGPT Chat Key</button></div>
        <label>OpenRouter key <input id='openRouterApiKey' type='password' autocomplete='off'></label>
        <div class='actions'><button class='danger' onclick=""clearKey('openRouterApiKey')"">Clear OpenRouter Key</button></div>";

        private static string ChatProviderScript() => @"
    const chatModelKeys = ['dialogueModel','characterConstructionModel','diplomacyModel','eventsModel','fastModel',
      'memoryModel','relationshipModel','correspondenceModel','strategyModel','selectorModel','actionPlannerModel','actionRouterModel'];
    let chatModelProfiles = {}, displayedChatProvider = '', lastCodexModels = [];
    function captureChatModels() {
      if (!displayedChatProvider) return;
      chatModelProfiles[displayedChatProvider] = Object.fromEntries(chatModelKeys.map(id => [id, document.getElementById(id)?.value || '']));
    }
    function changeChatProvider() {
      if (typeof codexPerformanceCaptureBeforeProviderChange === 'function') codexPerformanceCaptureBeforeProviderChange();
      captureChatModels();
      const provider = document.getElementById('llmProvider').value;
      const profile = chatModelProfiles[provider] || {};
      chatModelKeys.forEach(id => { const el = document.getElementById(id); if (el) el.value = profile[id] ?? ''; });
      displayedChatProvider = provider;
      updateLlmProviderUi();
      document.getElementById('status').textContent = 'Provider selected; save settings to apply';
    }
    function updateLlmProviderUi() {
      const provider = document.getElementById('llmProvider')?.value || 'openai_compatible';
      const compatible = document.getElementById('openAiCompatibleSettings');
      const subscription = document.getElementById('codexSubscriptionSettings');
      if (compatible) compatible.style.display = provider === 'openai_compatible' ? 'block' : 'none';
      if (subscription) subscription.style.display = provider === 'codex_subscription' ? 'block' : 'none';
      const probe = document.getElementById('promptCacheProbeButton');
      if (probe) { probe.disabled = provider !== 'nanogpt'; probe.title = 'This GLM diagnostic requires saved NanoGPT settings.'; }
      const hints = {
        nanogpt: 'NanoGPT uses its saved chat key and model selections.',
        openrouter: 'OpenRouter uses its saved key and model selections. Use full model IDs, such as openai/gpt-5.5. Usage is billed by OpenRouter.',
        openai_compatible: 'Custom endpoints use the API URL and key below.',
        codex_subscription: 'Codex uses your ChatGPT sign-in and its own saved model selections.'
      };
      document.getElementById('chatProviderHint').textContent = hints[provider] || '';
      if (typeof codexPerformanceApplyProvider === 'function') codexPerformanceApplyProvider(provider);
      const label = document.getElementById('modelProviderLabel');
      if (label) label.textContent = 'Model selections for ' + ({nanogpt:'NanoGPT',openrouter:'OpenRouter',openai_compatible:'Custom API',codex_subscription:'ChatGPT Codex'}[provider] || provider);
      const options = document.getElementById('llmModelOptions');
      if (options && provider !== 'codex_subscription') options.innerHTML = provider === 'openrouter'
        ? '<option value=\'openai/gpt-5.5\'></option><option value=\'openai/gpt-5.6-terra\'></option><option value=\'~openai/gpt-latest\'></option>' : '';
      if (provider === 'codex_subscription') renderCodexModels(lastCodexModels);
    }
";
    }
}
