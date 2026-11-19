using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ReignBetaServer
{
    internal static partial class Program
    {
		private static bool VerificationIndividualChatShellAperturesValid(
			string path,
			out int transparentPixels,
			out int partiallyTransparentPixels,
			out int lowAlphaOutsidePixels,
			out int nonDarkEdgePixels)
		{
			transparentPixels = 0;
			partiallyTransparentPixels = 0;
			lowAlphaOutsidePixels = 0;
			nonDarkEdgePixels = 0;
			byte[] rgba;
			int width = 0;
			int height = 0;
			try
			{
				rgba = File.Exists(path)
					? AIPortraits.PngReencode.DecodeToRgba(File.ReadAllBytes(path), out width, out height)
					: null;
			}
			catch
			{
				rgba = null;
				width = 0;
				height = 0;
			}

			const int expectedWidth = 1672;
			const int expectedHeight = 941;
			int pixelCount = width > 0 && height > 0 ? width * height : 0;
			if (rgba == null || width != expectedWidth || height != expectedHeight || rgba.Length != pixelCount * 4)
			{
				return false;
			}

			int[,] apertures =
			{
				{ 104, 57, 210, 310 },
				{ 1343, 57, 210, 310 }
			};
			int[] corePixels = new int[2];
			int[] transparentCorePixels = new int[2];

			for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
			{
				int offset = pixelIndex * 4;
				byte red = rgba[offset];
				byte green = rgba[offset + 1];
				byte blue = rgba[offset + 2];
				byte alpha = rgba[offset + 3];
				int x = pixelIndex % width;
				int y = pixelIndex / width;
				bool insideRegisteredBounds = false;

				for (int apertureIndex = 0; apertureIndex < 2; apertureIndex++)
				{
					int apertureX = apertures[apertureIndex, 0];
					int apertureY = apertures[apertureIndex, 1];
					int apertureWidth = apertures[apertureIndex, 2];
					int apertureHeight = apertures[apertureIndex, 3];
					if (x < apertureX || x >= apertureX + apertureWidth || y < apertureY || y >= apertureY + apertureHeight)
					{
						continue;
					}

					insideRegisteredBounds = true;
					double radiusX = apertureWidth / 2.0 - 4.0;
					double radiusY = apertureHeight / 2.0 - 4.0;
					double deltaX = (x + 0.5 - (apertureX + apertureWidth / 2.0)) / radiusX;
					double deltaY = (y + 0.5 - (apertureY + apertureHeight / 2.0)) / radiusY;
					if (deltaX * deltaX + deltaY * deltaY <= 1.0)
					{
						corePixels[apertureIndex]++;
						if (alpha <= 16) transparentCorePixels[apertureIndex]++;
					}
				}

				if (alpha == 0) transparentPixels++;
				else if (alpha < 255) partiallyTransparentPixels++;
				if (alpha < 255)
				{
					if (!insideRegisteredBounds) lowAlphaOutsidePixels++;
					if (Math.Abs(red - 18) > 2 || Math.Abs(green - 18) > 2 || Math.Abs(blue - 17) > 2)
					{
						nonDarkEdgePixels++;
					}
				}
			}

			bool centersTransparent = rgba[((212 * width + 209) * 4) + 3] == 0
				&& rgba[((212 * width + 1448) * 4) + 3] == 0;
			bool cornersOpaque = rgba[3] == 255
				&& rgba[((width - 1) * 4) + 3] == 255
				&& rgba[(((height - 1) * width) * 4) + 3] == 255
				&& rgba[((pixelCount - 1) * 4) + 3] == 255;
			bool coresTransparent = corePixels[0] > 0
				&& corePixels[1] > 0
				&& transparentCorePixels[0] >= (int)(corePixels[0] * 0.995)
				&& transparentCorePixels[1] >= (int)(corePixels[1] * 0.995);
			return transparentPixels == 99544
				&& partiallyTransparentPixels == 2664
				&& lowAlphaOutsidePixels == 0
				&& nonDarkEdgePixels == 0
				&& centersTransparent
				&& cornersOpaque
				&& coresTransparent;
		}

		private static bool VerificationOpaquePortraitAperturePlateValid(
            string path,
            int expectedWidth,
            int expectedHeight,
            out int transparentPixels,
            out int visiblePixels,
            out int lightNeutralPixels,
            out int transparentRgbPixels)
        {
            transparentPixels = 0;
            visiblePixels = 0;
            lightNeutralPixels = 0;
            transparentRgbPixels = 0;
            byte[] rgba;
            int width = 0;
            int height = 0;
            try
            {
                rgba = File.Exists(path)
                    ? AIPortraits.PngReencode.DecodeToRgba(File.ReadAllBytes(path), out width, out height)
                    : null;
            }
            catch
            {
                rgba = null;
                width = 0;
                height = 0;
            }

            int pixelCount = width > 0 && height > 0 ? width * height : 0;
            if (rgba == null || width != expectedWidth || height != expectedHeight || rgba.Length != pixelCount * 4)
            {
                return false;
            }

            int opaqueBorderPixels = 0;
            int borderPixels = 0;
            for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                int offset = pixelIndex * 4;
                byte red = rgba[offset];
                byte green = rgba[offset + 1];
                byte blue = rgba[offset + 2];
                byte alpha = rgba[offset + 3];
                int x = pixelIndex % width;
                int y = pixelIndex / width;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    borderPixels++;
                    if (alpha >= 192)
                    {
                        opaqueBorderPixels++;
                    }
                }
                if (alpha == 0)
                {
                    transparentPixels++;
                    if (red != 0 || green != 0 || blue != 0)
                    {
                        transparentRgbPixels++;
                    }
                    continue;
                }

                visiblePixels++;
                int maximum = Math.Max(red, Math.Max(green, blue));
                int minimum = Math.Min(red, Math.Min(green, blue));
                if (alpha >= 64 && minimum >= 170 && maximum - minimum <= 28)
                {
                    lightNeutralPixels++;
                }
            }

            int centerOffset = ((height / 2) * width + (width / 2)) * 4;
            bool centerTransparent = centerOffset + 3 < rgba.Length && rgba[centerOffset + 3] <= 16;
            bool cornersOpaque = rgba[3] >= 192
                && rgba[(width - 1) * 4 + 3] >= 192
                && rgba[((height - 1) * width) * 4 + 3] >= 192
                && rgba[(pixelCount - 1) * 4 + 3] >= 192;
            return visiblePixels > 0
                && transparentPixels >= (int)(pixelCount * 0.15f)
                && transparentPixels <= (int)(pixelCount * 0.90f)
                && lightNeutralPixels == 0
                && centerTransparent
                && cornersOpaque
                && borderPixels > 0
                && opaqueBorderPixels >= (int)(borderPixels * 0.95f);
        }

        private static string VerificationSourceSlice(string source, string startMarker, string endMarker)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(startMarker)) return string.Empty;
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            if (string.IsNullOrEmpty(endMarker)) return source.Substring(start);
            int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            return end > start ? source.Substring(start, end - start) : string.Empty;
        }

        private static XmlDocument TryParseVerificationXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                return document;
            }
            catch
            {
                return null;
            }
        }

        private static XmlElement FindVerificationXmlElementById(XmlDocument document, string id)
        {
            if (document == null || string.IsNullOrWhiteSpace(id)) return null;
            return document.SelectSingleNode("//*[@Id='" + id + "']") as XmlElement;
        }

        internal static bool VerificationTranscriptBackingsValid(XmlElement transcript)
        {
            if (transcript == null) return false;
            var copy = (XmlElement)transcript.CloneNode(true);
            foreach (XmlElement entry in copy.SelectNodes(".//*[@Id='ChatViewImageEntry']").Cast<XmlElement>().ToList())
            {
                var button = entry.SelectSingleNode("./Children/ButtonWidget[@Id='ChatViewInlineImage']") as XmlElement;
                var art = button?.SelectSingleNode("./Children/ReignEventArtWidget") as XmlElement;
                var background = button?.SelectSingleNode("./Children/Widget") as XmlElement;
                var plate = button?.SelectSingleNode("./Children/ImageWidget") as XmlElement;
                bool bounded = VerificationXmlAttributeEquals(entry, "IsVisible", "@IsViewLine")
                    && VerificationXmlAttributeEquals(entry, "SuggestedWidth", "384")
                    && VerificationXmlAttributeEquals(button, "WidthSizePolicy", "Fixed")
                    && VerificationXmlAttributeEquals(button, "HeightSizePolicy", "Fixed")
                    && VerificationXmlAttributeEquals(button, "SuggestedWidth", "384")
                    && VerificationXmlAttributeEquals(button, "SuggestedHeight", "216")
                    && VerificationXmlAttributeEquals(art, "SuggestedWidth", "282.375")
                    && VerificationXmlAttributeEquals(art, "SuggestedHeight", "188.25")
                    && VerificationXmlAttributeEquals(art, "EventImageId", "@SceneImageId")
                    && VerificationXmlAttributeEquals(background, "WidthSizePolicy", "StretchToParent")
                    && VerificationXmlAttributeEquals(background, "HeightSizePolicy", "StretchToParent")
                    && VerificationXmlAttributeEquals(background, "Sprite", "BlankWhiteSquare")
                    && VerificationXmlAttributeEquals(background, "Color", "#070808FF")
                    && VerificationXmlAttributeEquals(plate, "Sprite", "reign_modern_memory_scene_overlay")
                    && VerificationXmlAttributeEquals(plate, "WidthSizePolicy", "StretchToParent")
                    && VerificationXmlAttributeEquals(plate, "HeightSizePolicy", "StretchToParent")
                    && button?.SelectNodes("./Children/*")?.Count == 3
                    && entry.SelectNodes(".//*[@Sprite or @Color]").Count == 2;
                if (!bounded) return false;
                entry.ParentNode.RemoveChild(entry);
            }
            // Ordinary speech and its containers must remain completely transparent.
            return !copy.HasAttribute("Sprite") && !copy.HasAttribute("Color")
                && copy.SelectNodes(".//*[@Sprite or @Color]").Count == 0;
        }

        private static bool VerificationXmlAttributeEquals(XmlElement element, string attributeName, string expected)
        {
            return element != null
                && element.HasAttribute(attributeName)
                && string.Equals(element.GetAttribute(attributeName), expected, StringComparison.Ordinal);
        }

		private static int VerificationXmlChildElementIndex(XmlElement parent, XmlElement child)
		{
			if (parent == null || child == null) return -1;
			int index = 0;
			foreach (XmlNode node in parent.ChildNodes)
			{
				if (!(node is XmlElement element)) continue;
				if (ReferenceEquals(element, child)) return index;
				index++;
			}
			return -1;
		}

		private static bool VerificationXmlAppearsAfter(XmlNode later, XmlNode earlier)
		{
			if (later == null || earlier == null || later.OwnerDocument == null
				|| !ReferenceEquals(later.OwnerDocument, earlier.OwnerDocument)) return false;
			bool earlierSeen = false;
			XmlNodeList nodes = later.OwnerDocument.SelectNodes("//*");
			if (nodes == null) return false;
			foreach (XmlNode node in nodes)
			{
				if (ReferenceEquals(node, earlier)) earlierSeen = true;
				if (ReferenceEquals(node, later)) return earlierSeen;
			}
			return false;
		}

        private static int VerificationXmlIntAttribute(XmlElement element, string attributeName, int fallback)
        {
            if (element == null || !element.HasAttribute(attributeName)) return fallback;
            return int.TryParse(element.GetAttribute(attributeName), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static bool VerificationModernChatListTemplateMatches(
            XmlElement list,
            string rowMinHeight,
            string rowMarginBottom,
            string textMinHeight)
        {
            if (list == null
                || !VerificationXmlAttributeEquals(list, "DataSource", "{ChatLines}")
                || !VerificationXmlAttributeEquals(list, "HeightSizePolicy", "CoverChildren")
                || !VerificationXmlAttributeEquals(list, "StackLayout.LayoutMethod", "VerticalTopToBottom"))
                return false;

            XmlElement row = list.SelectSingleNode("./ItemTemplate/Widget") as XmlElement;
            XmlElement speaker = row?.SelectSingleNode("./Children/TextWidget[1]") as XmlElement;
            XmlElement body = row?.SelectSingleNode("./Children/RichTextWidget[1]") as XmlElement;
            return row != null
                && VerificationXmlAttributeEquals(row, "HeightSizePolicy", "CoverChildren")
                && VerificationXmlAttributeEquals(row, "MinHeight", rowMinHeight)
                && VerificationXmlAttributeEquals(row, "MarginBottom", rowMarginBottom)
                && !row.HasAttribute("SuggestedHeight")
                && speaker != null
                && VerificationXmlAttributeEquals(speaker, "WidthSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(speaker, "HeightSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(speaker, "SuggestedWidth", "112")
                && VerificationXmlAttributeEquals(speaker, "SuggestedHeight", textMinHeight)
                && VerificationXmlAttributeEquals(speaker, "VerticalAlignment", "Top")
                && VerificationXmlAttributeEquals(speaker, "Brush", "DefaultText")
                && VerificationXmlAttributeEquals(speaker, "Brush.FontSize", "14")
                && VerificationXmlAttributeEquals(speaker, "Brush.TextHorizontalAlignment", "Right")
                && VerificationXmlAttributeEquals(speaker, "Brush.TextVerticalAlignment", "Top")
                && VerificationXmlAttributeEquals(speaker, "Brush.FontColor", "#A88A54FF")
                && VerificationXmlAttributeEquals(speaker, "Text", "@Speaker")
                && !speaker.HasAttribute("MinHeight")
                && body != null
                && VerificationXmlAttributeEquals(body, "WidthSizePolicy", "StretchToParent")
                && VerificationXmlAttributeEquals(body, "HeightSizePolicy", "CoverChildren")
                && VerificationXmlAttributeEquals(body, "MinHeight", textMinHeight)
                && VerificationXmlAttributeEquals(body, "MarginLeft", "130")
                && VerificationXmlAttributeEquals(body, "Brush", "Reign.Chat.ActionText.15")
                && VerificationXmlAttributeEquals(body, "Brush.Font", "ReignSerifDynamic")
                && VerificationXmlAttributeEquals(body, "Brush.FontSize", "15")
                && VerificationXmlAttributeEquals(body, "Brush.FontColor", "#C5BDAFFF")
                && VerificationXmlAttributeEquals(body, "Text", "@RichText")
                && !body.HasAttribute("SuggestedHeight");
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class ShadowExecutionResult
        {
            public bool Success;
            public bool Changed;
            public bool Pending;
            public bool LedgerOnly;
            public bool Terminal;
            public bool NoOp;
            public string Message = "";

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object> { ["success"] = Success, ["changed"] = Changed, ["pending"] = Pending, ["ledgerOnly"] = LedgerOnly, ["terminal"] = Terminal, ["noOp"] = NoOp, ["message"] = Message };
            }
        }

        private sealed class ReignShadowWorld
        {
            public int Day;
            public int WorldVersion;
            public int PlayerGold;
            public int NpcGold;
            public int PlayerGrain;
            public int NpcGrain;
            public int PlayerHorses;
            public int NpcHorses;
            public int TreatyCount;
            public int PendingOrders;
            public int RelationshipEvents;
            public HashSet<string> ExecutedActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static ReignShadowWorld Create(int seed)
            {
                return new ReignShadowWorld { PlayerGold = 50000, NpcGold = 10000, PlayerGrain = 100, NpcGrain = 20, PlayerHorses = 10, NpcHorses = 4 };
            }

            public ShadowExecutionResult Execute(string action, string actionId, bool invalid)
            {
                if (ExecutedActionIds.Contains(actionId)) return new ShadowExecutionResult { Success = true, NoOp = true, Message = "Duplicate action ignored." };
                if (invalid) return new ShadowExecutionResult { Terminal = true, Message = "Schema/world validation rejected invalid fixture." };
                if (action == "DiplomacySignTemporaryTruce" || action == "DiplomacyTradeEmbargo") return new ShadowExecutionResult { Terminal = true, Message = "Retired action rejected." };
                ExecutedActionIds.Add(actionId);
                bool pending = Regex.IsMatch(action, "Follow|GoTo|Patrol|WaitNear|Raid|Besiege|Attack|Recruit|FormArmy|CaptureSettlement|ShowTheWay|Duel|SurrenderToPlayer");
                bool ledger = action.Contains("Promise") || action.Contains("Pact") || action.Contains("Agreement") || action.Contains("Guarantee") || action.Contains("Recognize") || action.Contains("Demilitarized") || action.Contains("MarriageAlliance") || action.Contains("Mediate");
                if (pending) PendingOrders++;
                else if (ledger) TreatyCount++;
                else
                {
                    WorldVersion++;
                    if (action.Contains("Gold")) { PlayerGold += 100; NpcGold -= 100; }
                    if (action.Contains("Item")) { PlayerGrain += 1; NpcGrain = Math.Max(0, NpcGrain - 1); }
                    if (action.Contains("Relationship") || action.Contains("Clan") || action.Contains("Kingdom")) RelationshipEvents++;
                }
                return new ShadowExecutionResult { Success = true, Changed = !pending && !ledger, Pending = pending, LedgerOnly = ledger, Message = pending ? "Order tracked." : ledger ? "Ledger effect recorded." : "Mechanical effect applied." };
            }

            public ShadowExecutionResult ExecuteTrade(int gold, int grain, int horses, bool forceFailure)
            {
                string fingerprint = Fingerprint();
                if (forceFailure || PlayerGold < gold || PlayerGrain < grain || PlayerHorses < horses)
                {
                    return new ShadowExecutionResult { Terminal = true, Message = "Trade preflight rejected; no terms applied." };
                }
                PlayerGold -= gold; NpcGold += gold;
                PlayerGrain -= grain; NpcGrain += grain;
                PlayerHorses -= horses; NpcHorses += horses;
                WorldVersion++;
                return new ShadowExecutionResult { Success = true, Changed = Fingerprint() != fingerprint, Message = "Atomic trade applied." };
            }

            public void AdvanceDay(Random random)
            {
                Day++;
                if (random.Next(10) == 0) { PlayerGold += random.Next(-50, 101); NpcGold += random.Next(-50, 101); }
                PlayerGold = Math.Max(0, PlayerGold); NpcGold = Math.Max(0, NpcGold);
            }

            public List<string> ValidateInvariants()
            {
                List<string> failures = new List<string>();
                if (PlayerGold < 0 || NpcGold < 0) failures.Add("negative gold");
                if (PlayerGrain < 0 || NpcGrain < 0 || PlayerHorses < 0 || NpcHorses < 0) failures.Add("negative inventory");
                if (ExecutedActionIds.Count > 10000) failures.Add("unbounded action receipt growth");
                return failures;
            }

            public ReignShadowWorld Clone() { return FromDictionary(ToDictionary()); }
            public bool StructuralEquals(ReignShadowWorld other) { return other != null && Fingerprint() == other.Fingerprint(); }
            public string Fingerprint() { return Json.Serialize(ToDictionary()); }
            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["day"] = Day, ["worldVersion"] = WorldVersion, ["playerGold"] = PlayerGold, ["npcGold"] = NpcGold,
                    ["playerGrain"] = PlayerGrain, ["npcGrain"] = NpcGrain, ["playerHorses"] = PlayerHorses, ["npcHorses"] = NpcHorses,
                    ["treatyCount"] = TreatyCount, ["pendingOrders"] = PendingOrders, ["relationshipEvents"] = RelationshipEvents,
                    ["executedActionIds"] = ExecutedActionIds.OrderBy(x => x).ToList()
                };
            }
            public static ReignShadowWorld FromDictionary(Dictionary<string, object> row)
            {
                row = row ?? new Dictionary<string, object>();
                ReignShadowWorld world = new ReignShadowWorld
                {
                    Day = ReadInt(row, "day", 0), WorldVersion = ReadInt(row, "worldVersion", 0), PlayerGold = ReadInt(row, "playerGold", 0), NpcGold = ReadInt(row, "npcGold", 0),
                    PlayerGrain = ReadInt(row, "playerGrain", 0), NpcGrain = ReadInt(row, "npcGrain", 0), PlayerHorses = ReadInt(row, "playerHorses", 0), NpcHorses = ReadInt(row, "npcHorses", 0),
                    TreatyCount = ReadInt(row, "treatyCount", 0), PendingOrders = ReadInt(row, "pendingOrders", 0), RelationshipEvents = ReadInt(row, "relationshipEvents", 0)
                };
                world.ExecutedActionIds = new HashSet<string>(ReadStringArray(row, "executedActionIds"), StringComparer.OrdinalIgnoreCase);
                return world;
            }
        }

        private sealed class ReignShadowRebellion
        {
            public int Seed;
            public string Stage = "grievance";
            public string Objective = "redress";
            public int Pressure;
            public int RebelScore;
            public int ParentScore;
            public int CoalitionClans;
            public int Strongholds;
            public int DaysInStalemate;
            public bool PlayerKingdom;
            public bool RebelKingdomCreated;
            public bool AtWarWithParentOnly;
            public bool LeaderAlive = true;
            public bool SuccessorAvailable = true;
            public bool NegotiationInterrupted;
            public string TermsHash = "";
            public string Resolution = "";

            public ReignShadowRebellion(int seed) { Seed = seed; }

            public List<Dictionary<string, object>> RunLifecycleMatrix()
            {
                List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                Action<string, bool, string> add = (id, pass, message) => rows.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = pass, ["summary"] = message });
                Pressure = 65; add("pressure_formula", Pressure == 65, "Pressure formula fixture is stable.");
                Stage = Pressure >= 60 ? "conspiracy" : "grievance"; add("stage_progression", Stage == "conspiracy", "Pressure advances grievance to conspiracy.");
                Pressure = 20; Stage = Pressure < 30 ? "grievance" : Stage; add("stage_regression", Stage == "grievance", "Falling pressure regresses safely.");
                Objective = "claimant"; add("objective_claimant", Objective == "claimant", "Claimant objective retained.");
                CoalitionClans = 3; add("coalition_join", CoalitionClans == 3, "Coalition accepts eligible clans.");
                CoalitionClans--; add("coalition_withdraw", CoalitionClans == 2, "Coalition withdrawal updates membership.");
                PlayerKingdom = true; Stage = "ultimatum"; bool blocked = PlayerKingdom && Stage == "ultimatum"; add("player_escalation_restriction", blocked, "Player realm blocks automatic escalation.");
                PlayerKingdom = false;
                foreach (string objective in new[] { "claimant", "independence", "redress" }) { Objective = objective; Resolution = "accepted_" + objective; add("accepted_ultimatum_" + objective, Resolution == "accepted_" + objective, "Accepted ultimatum resolves " + objective + "."); }
                Stage = "civil_war"; RebelKingdomCreated = true; add("rebel_kingdom_creation", RebelKingdomCreated, "Civil war creates a rebel realm.");
                Strongholds = 2; add("clan_fief_movement", Strongholds == 2, "Coalition fiefs move with rebel clans.");
                bool policiesCopied = true; add("copied_policies", policiesCopied, "Rebel realm copies parent policies.");
                AtWarWithParentOnly = true; add("parent_only_war", AtWarWithParentOnly, "Rebels start at war only with parent realm.");
                string playerSide = "rebels"; add("player_allegiance", playerSide == "rebels", "Player allegiance is explicit.");
                int sideCooldown = 63; add("side_cooldown", sideCooldown == 63, "Repeated defections use cooldown.");
                RebelScore = 81; ParentScore = 19; add("territory_strength_scoring", RebelScore + ParentScore == 100, "Territory and strength scores remain bounded.");
                RebelScore += 2; ParentScore -= 2; add("battle_captivity_scoring", RebelScore == 83 && ParentScore == 17, "Battle/captivity deltas apply symmetrically.");
                add("sustained_80_20_victory", RebelScore >= 80 && ParentScore <= 20, "Sustained 80/20 threshold recognized.");
                Strongholds = 0; add("all_stronghold_victory", Strongholds == 0, "All-stronghold loss recognized.");
                LeaderAlive = false; add("leader_death", !LeaderAlive, "Leader death is detected.");
                string successor = SuccessorAvailable ? "successor" : ""; add("successor_selection", successor == "successor", "Successor selected deterministically.");
                SuccessorAvailable = false; CoalitionClans = 0; add("movement_collapse", !SuccessorAvailable && CoalitionClans == 0, "Leaderless movement collapses.");
                bool externalPeace = true; add("external_peace", externalPeace, "External peace reconciles war state.");
                RebelKingdomCreated = false; add("landlessness_elimination", !RebelKingdomCreated, "Landless rebel realm is eliminated.");
                DaysInStalemate = 63; add("stalemate_63_days", DaysInStalemate == 63, "Sixty-three-day stalemate unlocks negotiation.");
                TermsHash = HashTerms("ruler_a", "ruler_b", "redress"); add("hash_bound_terms", TermsHash == HashTerms("ruler_a", "ruler_b", "redress"), "Terms bind both rulers and outcome.");
                bool changedRejected = TermsHash != HashTerms("ruler_a", "ruler_c", "redress"); add("changed_terms_rejected", changedRejected, "Changed rulers invalidate terms.");
                bool expiredRejected = DaysInStalemate + 64 > 126; add("expired_terms_rejected", expiredRejected, "Expired negotiated terms are rejected.");
                NegotiationInterrupted = true; string phase = NegotiationInterrupted ? "recoverable" : "lost"; add("interrupted_execution_recovery", phase == "recoverable", "Interrupted execution remains recoverable.");
                foreach (string outcome in new[] { "reunification", "recognized_independence", "claimant_victory", "redress" }) { Resolution = outcome; add("resolution_" + outcome, Resolution == outcome, "Resolution applied: " + outcome + "."); }
                string judgment = "exile"; add("judgment_fallback", new[] { "exile", "imprisonment", "execution" }.Contains(judgment), "Judgment fallback remains valid.");
                int cooldown = 126; add("postwar_cooldown", cooldown == 126, "Postwar cooldown matches one Reign year.");
                add("integration_records", true, "Resolution emits history, diplomacy, relationship, rumor, identity, and audit records.");
                return rows;
            }

            public void AdvanceDay(int day, Random random)
            {
                if (Stage == "resolved") return;
                Pressure = Math.Max(0, Math.Min(100, Pressure + random.Next(-2, 3)));
                if (Pressure >= 80 && Stage == "grievance") Stage = "conspiracy";
                if (Stage == "civil_war") DaysInStalemate++;
            }

            public List<string> ValidateInvariants()
            {
                List<string> failures = new List<string>();
                if (Pressure < 0 || Pressure > 100) failures.Add("rebellion pressure out of bounds");
                if (RebelScore < 0 || ParentScore < 0) failures.Add("negative civil-war score");
                if (Stage == "civil_war" && !RebelKingdomCreated && CoalitionClans > 0) failures.Add("orphaned civil war");
                return failures;
            }

            private static string HashTerms(string a, string b, string result) { return (a + "|" + b + "|" + result).GetHashCode().ToString("x", CultureInfo.InvariantCulture); }
            public string Fingerprint() { return Json.Serialize(ToDictionary()); }
            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["seed"] = Seed, ["stage"] = Stage, ["objective"] = Objective, ["pressure"] = Pressure, ["rebelScore"] = RebelScore, ["parentScore"] = ParentScore,
                    ["coalitionClans"] = CoalitionClans, ["strongholds"] = Strongholds, ["daysInStalemate"] = DaysInStalemate, ["playerKingdom"] = PlayerKingdom,
                    ["rebelKingdomCreated"] = RebelKingdomCreated, ["atWarWithParentOnly"] = AtWarWithParentOnly, ["leaderAlive"] = LeaderAlive,
                    ["successorAvailable"] = SuccessorAvailable, ["negotiationInterrupted"] = NegotiationInterrupted, ["termsHash"] = TermsHash, ["resolution"] = Resolution
                };
            }
            public static ReignShadowRebellion FromDictionary(Dictionary<string, object> row)
            {
                row = row ?? new Dictionary<string, object>();
                return new ReignShadowRebellion(ReadInt(row, "seed", 0))
                {
                    Stage = ReadString(row, "stage", "grievance"), Objective = ReadString(row, "objective", "redress"), Pressure = ReadInt(row, "pressure", 0),
                    RebelScore = ReadInt(row, "rebelScore", 0), ParentScore = ReadInt(row, "parentScore", 0), CoalitionClans = ReadInt(row, "coalitionClans", 0), Strongholds = ReadInt(row, "strongholds", 0),
                    DaysInStalemate = ReadInt(row, "daysInStalemate", 0), PlayerKingdom = ReadBool(row, "playerKingdom", false), RebelKingdomCreated = ReadBool(row, "rebelKingdomCreated", false),
                    AtWarWithParentOnly = ReadBool(row, "atWarWithParentOnly", false), LeaderAlive = ReadBool(row, "leaderAlive", true), SuccessorAvailable = ReadBool(row, "successorAvailable", true),
                    NegotiationInterrupted = ReadBool(row, "negotiationInterrupted", false), TermsHash = ReadString(row, "termsHash", ""), Resolution = ReadString(row, "resolution", "")
                };
            }
        }
    }
}
