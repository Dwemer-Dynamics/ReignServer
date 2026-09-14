# Codex app-server provider

Reign supports two chat-provider selections in the Control Center:

- `NanoGPT / OpenAI-compatible` keeps the existing HTTP chat-completions path, URL, API key, caching, retry, and circuit-breaker behavior.
- `ChatGPT Codex subscription` starts OpenAI's published `codex app-server` as a local stdio JSON-RPC child process and uses the account authenticated by that official runtime.

## Safety and lifecycle

Reign does not proxy OAuth, read Codex credential files, or store ChatGPT access or refresh tokens. Browser sign-in is initiated through `account/login/start`; the official Codex runtime owns the resulting authentication state. The child process is assigned to Reign's unified Windows lifetime job and is also stopped during orderly server shutdown.

Each Reign completion uses a fresh app-server thread with `approvalPolicy: never`, the app-server read-only sandbox restricted to a dedicated empty provider working directory, and an explicit no-tools provider prompt. Reign rejects any server-initiated approval or tool request. Completed temporary threads are deleted.

## Configuration

The default executable is `codex`, resolved by the service process. A full path can be entered when the official runtime is bundled outside `PATH`. `CODEX_HOME` is optional; blank preserves the official runtime default. Settings must be saved before starting or signing in.

The Control Center can start/check the runtime, open the official ChatGPT sign-in URL, sign out, and refresh `model/list` with `includeHidden: true`. Every model returned by the authenticated account is available in the GPT model picker and as a suggestion for each Reign request-type model field.

## Provider flow

1. Reign starts `codex app-server --listen stdio://` and sends `initialize` followed by `initialized`.
2. Account controls use `account/read`, `account/login/start`, and `account/logout`.
3. Model discovery pages through `model/list` until no cursor remains.
4. Chat requests use `thread/start` and `turn/start`, collect streamed agent-message notifications, wait for `turn/completed`, and adapt the final text to Reign's existing provider response contract.

Stdio requests use one LF-delimited JSON frame per message. Reign escapes non-ASCII UTF-16 code units as JSON Unicode escapes before writing the frame, preserving multilingual prompt text while avoiding transport corruption from raw smart punctuation, emoji, or other campaign text. Provider status reports the last method, frame character/byte counts, write timestamp, and framing mode without recording prompt contents or credentials.

No runtime is started merely by loading the settings page or querying status. Provider-backed tests remain separately gated by the Verification Lab `live-llm` tier.
