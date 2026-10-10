using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Reign.Core.Contracts.Actions
{
    public enum AgreementState { Draft, Offered, Counteroffered, Accepted, Reserved, InTransit, Waiting, Completed, Rejected, Cancelled, Impossible, RecoveryRequired }
    public enum ActionOrigin { Conversation, Console, Autonomous, GuardedTest }
    public enum TermTiming { Immediate, OnArrival, OnCondition, Installment }

    public sealed class ActionParameter
    {
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Kind { get; set; } = "text";
        public bool Required { get; set; }
        public bool Automatic { get; set; }
        public double? Minimum { get; set; }
        public double? Maximum { get; set; }
        public string[] Choices { get; set; } = Array.Empty<string>();
    }

    public sealed class ActionDefinition
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
        public string Family { get; set; } = "";
        public string Adapter { get; set; } = "";
        public string Description { get; set; } = "";
        public string Source { get; set; } = "";
        public bool ConversationEligible { get; set; } = true;
        public bool MovesActor { get; set; }
        public bool IsSetup { get; set; }
        public string DurationParameter { get; set; } = "";
        public string NativeObjective { get; set; } = "";
        public List<ActionParameter> Parameters { get; set; } = new List<ActionParameter>();
    }

    public sealed class ActionClause
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string ActionId { get; set; } = "";
        public string ActorId { get; set; } = "";
        public string RecipientId { get; set; } = "";
        public SortedDictionary<string, string> Arguments { get; set; } = new SortedDictionary<string, string>(StringComparer.Ordinal);
        public TermTiming Timing { get; set; }
        public double DueDay { get; set; }
        public double DurationDays { get; set; }
        public int Installments { get; set; } = 1;
        public double IntervalDays { get; set; }
        public string ConditionKind { get; set; } = "";
        public string ConditionTargetId { get; set; } = "";
        public string DisplayText { get; set; } = "";
    }

    public sealed class ActionAgreement
    {
        public const int CurrentVersion = 1;
        public int Version { get; set; } = CurrentVersion;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Revision { get; set; } = 1;
        public string PlayerId { get; set; } = "";
        public string NpcId { get; set; } = "";
        public string ConversationId { get; set; } = "";
        public string ProposerId { get; set; } = "";
        public ActionOrigin Origin { get; set; }
        public AgreementState State { get; set; }
        public List<ActionClause> Clauses { get; set; } = new List<ActionClause>();
        public string PlayerAcceptedHash { get; set; } = "";
        public string NpcAcceptedHash { get; set; } = "";
        public string Failure { get; set; } = "";
        public double CreatedDay { get; set; }
        public double ExpiresDay { get; set; }
        public bool AdvancePayment { get; set; }
        public string Sentence => string.Join(" ", Clauses.Select(x => x.DisplayText.Trim().TrimEnd('.')).Where(x => x.Length > 0)) + ".";

        // Length-prefixed fields avoid ambiguous hashes; presentation is included so consent
        // is bound to the exact sentence the recipient reviewed, as well as executable terms.
        public string TermsHash()
        {
            var b = new StringBuilder();
            // Version 1 consent was signed by the .NET Framework game client.
            // Modern .NET's default double format uses more digits, so an unchanged
            // fractional campaign day otherwise produces a different server hash.
            // Keep the client's existing G15 representation, including unsigned zero,
            // rather than invalidating saved consent by changing the hash protocol.
            void Add(object value)
            {
                string s = value is double number
                    ? number == 0d ? "0" : number.ToString("G15", CultureInfo.InvariantCulture)
                    : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                b.Append(s.Length).Append(':').Append(s);
            }
            Add(Version); Add(Id); Add(Revision); Add(PlayerId); Add(NpcId); Add(ConversationId); Add(ProposerId); Add(Origin); Add(AdvancePayment); Add(CreatedDay); Add(ExpiresDay); Add(Clauses.Count);
            foreach (var c in Clauses)
            {
                Add(c.Id); Add(c.ActionId); Add(c.ActorId); Add(c.RecipientId); Add(c.Timing); Add(c.DueDay); Add(c.DurationDays);
                Add(c.Installments); Add(c.IntervalDays); Add(c.ConditionKind); Add(c.ConditionTargetId); Add(c.DisplayText);
                Add(c.Arguments.Count);
                foreach (var kv in c.Arguments.OrderBy(x => x.Key, StringComparer.Ordinal)) { Add(kv.Key); Add(kv.Value); }
            }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        public bool HasBothConsents => PlayerAcceptedHash == TermsHash() && NpcAcceptedHash == TermsHash();
        public bool HasRequiredConsents => HasBothConsents || AgreementRules.IsPlayerDeclaration(this)
            && Origin == ActionOrigin.Conversation && PlayerAcceptedHash == TermsHash();
        public void Revise()
        {
            if (State != AgreementState.Draft && State != AgreementState.Offered && State != AgreementState.Counteroffered && State != AgreementState.Accepted)
                throw new InvalidOperationException("A committed agreement cannot be edited.");
            Revision++; PlayerAcceptedHash = ""; NpcAcceptedHash = ""; State = AgreementState.Draft;
        }
    }

    public sealed class ActionReceipt
    {
        public string OperationId { get; set; } = "";
        public string TermsHash { get; set; } = "";
        public AgreementState State { get; set; }
        public string Message { get; set; } = "";
        public List<string> ObservedEffects { get; set; } = new List<string>();
        public double Day { get; set; }
    }

    public static class AgreementRules
    {
        // A completed native journal permits confirmation of the original terms,
        // never a new delivery or caller-supplied consent. Callers bind the draft
        // to their independently observed conversation before comparing hashes.
        public static bool IsExactCompletedReplay(ActionAgreement draft, AgreementJournal saved)
        {
            if (draft == null || saved?.Agreement == null || saved.OperatorAuthorized
                || draft.Origin != ActionOrigin.Conversation
                || saved.Agreement.Origin != ActionOrigin.Conversation
                || saved.Agreement.State != AgreementState.Completed
                || saved.CommittedHash.Length == 0 || !saved.Agreement.HasRequiredConsents
                || saved.Steps == null || saved.Steps.Count == 0
                || saved.Steps.Any(step => step == null || !step.Completed || step.FulfilledInstallments < 1))
                return false;
            return saved.CommittedHash == saved.Agreement.TermsHash()
                && saved.CommittedHash == draft.TermsHash();
        }

        public static bool BindGuestReview(ActionAgreement agreement, ActionClause clause,
            string guestAgreementId, double reviewDueDay, int guestRevision)
        {
            if (clause.ActionId != "renew_temporary_party_guest" && clause.ActionId != "end_temporary_party_guest") return false;
            var keys = new[] { "terms.agreementId", "terms.expectedReviewDueDay", "terms.expectedAgreementRevision" };
            int supplied = keys.Count(clause.Arguments.ContainsKey);
            if (supplied == keys.Length) return false; // Existing consent must never be rebound to newer native state.
            if (supplied != 0) throw new ArgumentException("The guest agreement binding is incomplete. Build a new request.");
            if (agreement.PlayerAcceptedHash.Length > 0 || agreement.NpcAcceptedHash.Length > 0)
                throw new ArgumentException("This signed guest request lacks its original agreement binding. Build a new request.");
            if (string.IsNullOrWhiteSpace(guestAgreementId) || guestAgreementId.Length > 256
                || double.IsNaN(reviewDueDay) || double.IsInfinity(reviewDueDay) || reviewDueDay <= 0
                || guestRevision < 0 || guestRevision == int.MaxValue)
                throw new ArgumentException("There is no available current guest agreement to review.");
            clause.Arguments[keys[0]] = guestAgreementId;
            clause.Arguments[keys[1]] = reviewDueDay.ToString("R", CultureInfo.InvariantCulture);
            clause.Arguments[keys[2]] = guestRevision.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        public const int MaximumClauses = 64;
        public static bool IsPlayerDeclaration(ActionAgreement a) => a != null && a.Clauses != null
            && a.Clauses.Count == 1 && a.Clauses[0] != null
            && a.Clauses[0].ActionId == "start_ruling_clan_rebellion"
            && a.ProposerId == a.PlayerId && a.Clauses[0].ActorId == a.PlayerId
            && a.Clauses[0].RecipientId == a.NpcId;
        public static readonly string[] ConditionKinds = { "", "arrival", "release", "peace", "war", "ownership", "day" };
        public static List<string> Validate(ActionAgreement a, Func<string, ActionDefinition?> resolve, double now)
        {
            var errors = new List<string>();
            if (a == null) return new List<string> { "An agreement is required." };
            if (a.Clauses == null || a.Clauses.Any(c => c == null || c.Arguments == null || c.DisplayText == null || c.ConditionKind == null || c.ConditionTargetId == null))
                return new List<string> { "Agreement terms are malformed." };
            if (a.Version != ActionAgreement.CurrentVersion) errors.Add("Unsupported agreement version.");
            if (!Enum.IsDefined(typeof(ActionOrigin), a.Origin) || !Enum.IsDefined(typeof(AgreementState), a.State)) errors.Add("Unknown agreement origin or state.");
            if (string.IsNullOrWhiteSpace(a.Id) || a.Revision < 1) errors.Add("Agreement identity is required.");
            if (new[] { a.Id, a.PlayerId, a.NpcId, a.ConversationId, a.ProposerId }.Any(s => s == null || s.Length > 256)) errors.Add("Agreement identity exceeds its allowed bounds.");
            if (string.IsNullOrWhiteSpace(a.PlayerId) || string.IsNullOrWhiteSpace(a.NpcId) || a.PlayerId == a.NpcId) errors.Add("Two distinct participants are required.");
            if (a.ProposerId != a.PlayerId && a.ProposerId != a.NpcId) errors.Add("The proposer must be a participant.");
            if (a.Clauses.Count == 0 || a.Clauses.Count > MaximumClauses) errors.Add("Select between one and 64 terms.");
            if (a.Clauses.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != a.Clauses.Count) errors.Add("Duplicate clause identity.");
            if (new[] { now, a.CreatedDay, a.ExpiresDay }.Any(x => double.IsNaN(x) || double.IsInfinity(x) || x < 0)) errors.Add("Invalid campaign time.");
            if (a.ExpiresDay > 0 && now > a.ExpiresDay) errors.Add("This proposal has expired.");
            if (a.Clauses.Any(c => c.ActionId == "start_ruling_clan_rebellion"))
            {
                if (!IsPlayerDeclaration(a) || a.AdvancePayment)
                    errors.Add("A rebellion declaration must be the player's own standalone statement to the ruler, without a bargain.");
                if (a.Clauses.Any(c => c.DueDay != 0 || c.DurationDays != 0 || c.Installments != 1
                    || c.IntervalDays != 0 || c.ConditionKind.Length != 0 || c.Timing != TermTiming.Immediate))
                    errors.Add("A rebellion declaration is submitted when sent and cannot be scheduled or conditional.");
            }
            foreach (var c in a.Clauses)
            {
                if (new[] { c.Id, c.ActionId, c.ActorId, c.RecipientId }.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 256) || c.ConditionTargetId.Length > 256) errors.Add("Invalid term identity.");
                if (c.DisplayText.Length > 4096 || c.Arguments.Count > 64 || c.Arguments.Any(kv => kv.Key.Length > 128 || kv.Value == null || kv.Value.Length > 4096)) errors.Add("A term exceeds its allowed bounds.");
                if (!Enum.IsDefined(typeof(TermTiming), c.Timing)) errors.Add("Unknown term timing.");
                var definition = resolve(c.ActionId);
                if (definition == null) { errors.Add("Unknown action: " + c.ActionId); continue; }
                if (a.Origin == ActionOrigin.Conversation && !definition.ConversationEligible) errors.Add("This action is available only to an operator.");
                if (a.Origin == ActionOrigin.Conversation && definition.ConversationEligible
                    && !ReignActionConversationPolicy.Allows(definition, c.ActorId == a.PlayerId))
                    errors.Add("This action cannot be performed in the selected conversation direction.");
                if (c.ActorId != a.PlayerId && c.ActorId != a.NpcId) errors.Add("The acting person must participate in this agreement.");
                if (c.RecipientId != a.PlayerId && c.RecipientId != a.NpcId) errors.Add("The recipient must participate in this agreement.");
                if (c.ActorId == c.RecipientId) errors.Add("An action must be between the two distinct participants.");
                if (a.Origin == ActionOrigin.Conversation && c.Arguments.Any(kv =>
                    !ReignActionConversationPolicy.AllowsParticipantSelection(c.ActionId, kv.Key, kv.Value, c.ActorId, c.RecipientId)))
                    errors.Add("This action requires a distinct, eligible target from this conversation.");
                if (c.ActionId == "pledge_rebellion_support" && (c.ActorId != a.NpcId || c.RecipientId != a.PlayerId))
                    errors.Add("The approached lord pledges support to the player; use Join rebellion for the player's allegiance.");
                if (a.Origin == ActionOrigin.Conversation && c.ActionId == "marriage_alliance"
                    && (c.Arguments.TryGetValue("terms.marriageHero1StringId", out string? spouseOne) && spouseOne != c.ActorId
                        || c.Arguments.TryGetValue("terms.marriageHero2StringId", out string? spouseTwo) && spouseTwo != c.RecipientId))
                    errors.Add("Personal marriage must name the two consenting participants exactly.");
                if (definition.MovesActor && c.ActorId == a.PlayerId && a.Origin == ActionOrigin.Conversation) errors.Add("The player controls their own movement.");
                if (c.DurationDays > 0 && definition.DurationParameter.Length == 0) errors.Add("This action has no duration term.");
                if (c.DurationDays > 0 && c.Arguments.TryGetValue(definition.DurationParameter, out string? durationValue)
                    && (!double.TryParse(durationValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double duration)
                        || Math.Abs(duration - c.DurationDays * (definition.DurationParameter.EndsWith("Hours", StringComparison.Ordinal) ? 24 : 1)) > 0.000001))
                    errors.Add("The duration fields disagree.");
                if (c.Installments > 1 && !new[] { "give.gold", "give.item", "give.troop", "give.prisoner" }.Contains(c.ActionId)) errors.Add("Only divisible resources support installments.");
                if (c.Installments < 1 || c.Installments > 365 || (c.Installments > 1 && c.IntervalDays <= 0)) errors.Add("Invalid installment schedule.");
                if (new[] { c.DueDay, c.DurationDays, c.IntervalDays }.Any(x => x < 0 || double.IsNaN(x) || double.IsInfinity(x))) errors.Add("Invalid term timing.");
                if (!ConditionKinds.Contains(c.ConditionKind)) errors.Add("Unsupported fulfillment condition.");
                if (c.Timing == TermTiming.OnCondition && c.ConditionKind.Length == 0 || c.ConditionKind.Length > 0 && c.ConditionKind != "day" && c.ConditionTargetId.Length == 0) errors.Add("Select an observable condition and exact target.");
                foreach (var p in definition.Parameters)
                {
                    c.Arguments.TryGetValue(p.Key, out string? value);
                    if (p.Key == definition.DurationParameter && c.DurationDays > 0 && string.IsNullOrWhiteSpace(value))
                        value = (c.DurationDays * (p.Key.EndsWith("Hours", StringComparison.Ordinal) ? 24 : 1)).ToString("R", CultureInfo.InvariantCulture);
                    string parameterError = ValidateParameterValue(p, value);
                    if (parameterError.Length > 0) errors.Add(parameterError);
                }
                foreach (var key in c.Arguments.Keys)
                    if (!definition.Parameters.Any(p => p.Key == key)) errors.Add("Unknown parameter: " + key);
            }
            foreach (var group in a.Clauses.Where(c => c.ActionId == "government.support_vote")
                .GroupBy(c => c.ActorId + "\n" + (c.Arguments.TryGetValue("businessId", out var id) ? id : "")))
                if (group.Select(c => c.Arguments.TryGetValue("optionId", out var option) ? option : "").Distinct(StringComparer.Ordinal).Count() > 1)
                    errors.Add("One agreement cannot promise opposing votes on the same proposal.");
            foreach (var group in a.Clauses.Where(c => c.ActionId == "government.back_platform").GroupBy(c => c.ActorId))
                if (group.Select(c => c.Arguments.TryGetValue("partyId", out var party) ? party : "").Distinct(StringComparer.Ordinal).Count() > 1)
                    errors.Add("One agreement cannot bind a member to different party platforms.");
            foreach (var group in a.Clauses.Where(c => c.ActionId.StartsWith("court.", StringComparison.Ordinal)).GroupBy(c => c.ActorId))
            {
                if (group.GroupBy(c => c.ActionId).Any(g => g.Count() > 1)) errors.Add("One agreement cannot repeat the same court appointment or dismissal.");
                foreach (var pair in new[] { new[] { "court.serve_office", "court.leave_office" }, new[] { "court.serve_regent", "court.leave_regent" }, new[] { "court.serve_ambassador", "court.recall_ambassador" } })
                    if (group.Any(c => c.ActionId == pair[0]) && group.Any(c => c.ActionId == pair[1])) errors.Add("One agreement cannot both appoint and dismiss the same person from a role.");
            }
            foreach (var group in a.Clauses.Where(c => c.ActionId == "court.serve_office")
                .GroupBy(c => c.Arguments.TryGetValue("office", out var office) ? office : ""))
                if (group.Count() > 1) errors.Add("A court office cannot be promised to two people in one agreement.");
            if (a.Clauses.Count(c => c.ActionId == "court.serve_regent") > 1) errors.Add("Only one regent can be appointed in one agreement.");
            return errors.Distinct().ToList();
        }

        public static bool IsNumericKind(string kind) => kind == "quantity" || kind == "money" || kind == "number" || kind == "integer";
        public static Dictionary<string, object> WorldTerms(ActionDefinition definition, IDictionary<string, string> arguments)
        {
            var terms = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var argument in arguments)
            {
                var parameter = definition.Parameters.FirstOrDefault(p => p.Key == argument.Key)
                    ?? throw new ArgumentException("Unknown action parameter: " + argument.Key);
                string error = ValidateParameterValue(parameter, argument.Value);
                if (error.Length > 0) throw new ArgumentException(error);
                string key = argument.Key.StartsWith("terms.", StringComparison.Ordinal) ? argument.Key.Substring(6) : argument.Key;
                if (definition.Id == "transfer_item" || definition.Id == "trade_package")
                    key = key == "Item" ? "itemId" : key == "Amount" ? "amount" : key;
                object value = argument.Value;
                if (!string.IsNullOrWhiteSpace(argument.Value))
                {
                    if (parameter.Kind == "number") value = double.Parse(argument.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                    else if (IsNumericKind(parameter.Kind)) value = int.Parse(argument.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                    else if (parameter.Choices.Contains("true") && bool.TryParse(argument.Value, out bool boolean)) value = boolean;
                }
                terms.Add(key, value);
            }
            return terms;
        }

        public static string ValidateParameterValue(ActionParameter parameter, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return parameter.Required ? "Select " + parameter.Label + "." : "";
            if (parameter.Choices.Length > 0 && !parameter.Choices.Contains(value, StringComparer.Ordinal)) return "Invalid " + parameter.Label + ".";
            if (!IsNumericKind(parameter.Kind)) return "";
            bool integer = parameter.Kind != "number";
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double amount) || double.IsNaN(amount) || double.IsInfinity(amount)
                || integer && (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int _) || amount != Math.Truncate(amount)))
                return parameter.Label + (integer ? " must be a whole number." : " must be a finite number.");
            double minimum = parameter.Minimum ?? (parameter.Kind == "quantity" ? 1 : parameter.Kind == "money" ? 0 : double.MinValue);
            double maximum = parameter.Maximum ?? (integer ? int.MaxValue : double.MaxValue);
            return amount < minimum || amount > maximum ? parameter.Label + " is outside the allowed range." : "";
        }

        public static bool Accept(ActionAgreement agreement, string participantId, string reviewedHash)
        {
            if (agreement.State != AgreementState.Offered && agreement.State != AgreementState.Counteroffered) return false;
            if (reviewedHash != agreement.TermsHash()) return false;
            if (participantId == agreement.PlayerId) agreement.PlayerAcceptedHash = reviewedHash;
            else if (participantId == agreement.NpcId) agreement.NpcAcceptedHash = reviewedHash;
            else return false;
            if (agreement.HasRequiredConsents) agreement.State = AgreementState.Accepted;
            return true;
        }

        public static bool IsReasonableQuote(long quoted, long appraised, double minimumRatio, double maximumRatio)
        {
            return quoted >= 0 && appraised > 0 && minimumRatio >= 0 && maximumRatio >= minimumRatio
                && !double.IsInfinity(maximumRatio) && !double.IsNaN(maximumRatio)
                && quoted >= appraised * minimumRatio && quoted <= appraised * maximumRatio;
        }
    }
}
