#nullable disable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Reign.Core.Contracts.Dialogue
{
    public sealed class ConversationSessionStart
    {
        public bool Ok { get; set; }
        public string SessionId { get; set; } = "";
        public string LogicalConversationId { get; set; } = "";
        public string Status { get; set; } = "";
        public string Error { get; set; } = "";
    }

    // One identity and one retry for a screen lifetime. Concurrent callers share
    // the first attempt; only a later explicit request may retry a failed start.
    public sealed class ConversationSessionStartGate
    {
        private readonly object _gate = new object();
        private Task<ConversationSessionStart> _pending;
        private int _attempts;
        public string SessionId { get; }

        public ConversationSessionStartGate(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A stable session id is required.");
            SessionId = sessionId;
        }

        public Task<ConversationSessionStart> EnsureAsync(Func<Task<ConversationSessionStart>> start, bool retryFailed)
        {
            lock (_gate)
            {
                if (_pending != null && (!_pending.IsCompleted || _pending.Result.Ok || !retryFailed || _attempts >= 2))
                    return _pending;
                _attempts++;
                return _pending = StartAsync(start);
            }
        }

        private async Task<ConversationSessionStart> StartAsync(Func<Task<ConversationSessionStart>> start)
        {
            try
            {
                var result = await start().ConfigureAwait(false) ?? new ConversationSessionStart();
                if (result.Ok && (result.SessionId != SessionId || string.IsNullOrWhiteSpace(result.LogicalConversationId)
                    || result.Status == "closed" || result.Status == "interrupted"))
                    return new ConversationSessionStart { Error = "The server did not confirm the active conversation identity." };
                return result;
            }
            catch (Exception ex) { return new ConversationSessionStart { Error = ex.Message }; }
        }
    }

    // Serialized inside the native save. A matching participant is insufficient:
    // the player must explicitly consume this saved, controller-issued handoff.
    public sealed class ConversationContinuationToken
    {
        public int Version { get; set; } = 1;
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string NpcId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string LogicalConversationId { get; set; } = "";
        public string Generation { get; set; } = "";
        public string Reason { get; set; } = "";
        public bool IsValid => Version == 1 && !string.IsNullOrWhiteSpace(CampaignId) && !string.IsNullOrWhiteSpace(TimelineId)
            && !string.IsNullOrWhiteSpace(PlayerId) && !string.IsNullOrWhiteSpace(NpcId) && !string.IsNullOrWhiteSpace(SessionId)
            && !string.IsNullOrWhiteSpace(LogicalConversationId) && !string.IsNullOrWhiteSpace(Generation)
            && (Reason == "scene_transition" || Reason == "ui_continuation" || Reason == "campaign_reload");

        public bool Matches(string campaign, string timeline, string player, string npc) => IsValid
            && CampaignId == campaign && TimelineId == timeline && PlayerId == player && NpcId == npc;
    }

    public sealed class ConversationContinuationSlot
    {
        private readonly object _gate = new object();
        private string _generation = "";
        private ConversationContinuationToken _token;
        public ConversationContinuationToken Snapshot { get { lock (_gate) return _token; } }

        public void Begin(string generation) { lock (_gate) { _generation = generation; _token = null; } }
        public void Discard() { Begin(Guid.NewGuid().ToString("N")); }
        public bool Offer(ConversationContinuationToken token)
        {
            lock (_gate)
            {
                if (token?.IsValid != true || token.Generation != _generation) return false;
                _token = token;
                return true;
            }
        }
        public void Restore(ConversationContinuationToken token)
        {
            lock (_gate) { _token = token?.IsValid == true ? token : null; _generation = _token?.Generation ?? ""; }
        }
        public ConversationContinuationToken Consume(string campaign, string timeline, string player, string npc)
        {
            lock (_gate)
            {
                if (_token?.Matches(campaign, timeline, player, npc) != true) return null;
                var result = _token;
                Discard();
                return result;
            }
        }
    }
}
