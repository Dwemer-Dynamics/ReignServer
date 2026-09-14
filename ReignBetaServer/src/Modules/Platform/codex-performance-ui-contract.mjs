// Codex performance UI contract entry point.
//
// The provider contract owns the isolated fixture, provider-switching state
// machine, save/reload assertions, bounded verification interception, and the
// 1672/1024/600 screenshot matrix. Reuse that executable contract here so the
// Codex catalog has a stable dedicated path without creating a second fixture
// that could drift from the Control Center's provider parity checks.
import './chat-provider-ui-contract.mjs';
