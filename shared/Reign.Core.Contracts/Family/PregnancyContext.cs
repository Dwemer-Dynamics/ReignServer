using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Family
{
    // Native truth, personal knowledge and public disclosure are independent.
    // This policy never changes marriage, legal parentage or delivery ownership.
    public sealed class PregnancyFacts
    {
        public string PregnancyId { get; set; } = "";
        public string MotherId { get; set; } = "";
        public string MotherName { get; set; } = "";
        public string BiologicalFatherId { get; set; } = "";
        public string BiologicalFatherName { get; set; } = "";
        public string LegalFatherId { get; set; } = "";
        public string LegalFatherName { get; set; } = "";
        public string SpouseId { get; set; } = "";
        public string SpouseName { get; set; } = "";
        public string SpouseAtConceptionId { get; set; } = "";
        public bool MarriageAtConceptionKnown { get; set; }
        public bool Active { get; set; }
        public bool Illicit { get; set; }
        public bool TimingKnown { get; set; }
        public double ConceptionDay { get; set; }
        public double DueDay { get; set; }
        public double WorldDay { get; set; }
        public bool MotherExposed { get; set; }
        public bool FatherExposed { get; set; }
        public string Source { get; set; } = "";
    }

    public sealed class PregnancyKnowledge
    {
        public bool Pregnancy { get; set; }
        public bool Illicit { get; set; }
        public bool Paternity { get; set; }
        public string EvidenceId { get; set; } = "";
    }

    public static class PregnancyContextPolicy
    {
        public const string ActionId = "expose_concealed_pregnancy";
        public const int MotherPenalty = -50;
        public const int FatherPenalty = -5;
        public static double? Progress(PregnancyFacts facts)
        {
            if (!facts.Active || !facts.TimingKnown || !Finite(facts.ConceptionDay)
                || !Finite(facts.DueDay) || !Finite(facts.WorldDay)
                || facts.DueDay <= facts.ConceptionDay) return null;
            return Math.Max(0, Math.Min(1, (facts.WorldDay - facts.ConceptionDay) / (facts.DueDay - facts.ConceptionDay)));
        }
        public static bool Visible(PregnancyFacts facts) => Progress(facts) is double progress && progress >= .5d;
        public static bool FatherMismatch(PregnancyFacts facts) => facts.BiologicalFatherId.Length > 0
            && facts.LegalFatherId.Length > 0 && facts.BiologicalFatherId != facts.LegalFatherId;

        public static string ExposureError(PregnancyFacts facts, PregnancyKnowledge knowledge,
            string counterpartId, bool nameFather)
        {
            if (!facts.Active) return "This pregnancy is no longer active.";
            if (!facts.Illicit) return "There is no confirmed illicit pregnancy to expose.";
            if (!knowledge.Pregnancy || !knowledge.Illicit || knowledge.EvidenceId.Length == 0)
                return "Confirmed knowledge of the pregnancy and its illicit circumstances is required.";
            bool fatherTarget = counterpartId == facts.BiologicalFatherId && counterpartId != facts.MotherId;
            if (counterpartId != facts.MotherId && !fatherTarget) return "This person is not a known parent of this pregnancy.";
            if ((nameFather || fatherTarget) && (facts.BiologicalFatherId.Length == 0 || !knowledge.Paternity))
                return "Confirmed knowledge of the biological father's identity is required.";
            if (fatherTarget && !nameFather) return "Exposing the father names both parents.";
            if (facts.MotherExposed && (!nameFather || facts.FatherExposed)) return "These facts have already been exposed.";
            return "";
        }

        public static string SelfPrompt(PregnancyFacts facts)
        {
            if (!facts.Active) return "";
            var lines = new List<string> {
                "PREGNANCY — MANDATORY PRIVATE SELF-KNOWLEDGE",
                "You ARE pregnant and know this now. This is authoritative native game state, including on an existing save; absence from older dialogue is not evidence against it.",
                "Pregnancy identity: " + facts.PregnancyId + ". Mother: " + facts.MotherName + " (" + facts.MotherId + ").",
                facts.SpouseId.Length > 0 ? "Current spouse: " + facts.SpouseName + " (" + facts.SpouseId + ")." : "You currently have no spouse.",
                facts.BiologicalFatherId.Length > 0 ? "Private biological father: " + facts.BiologicalFatherName + " (" + facts.BiologicalFatherId + ")." : "Biological paternity is unknown in the supplied evidence; do not invent a father.",
                facts.LegalFatherId.Length > 0 ? "Recorded legal/acknowledged father: " + facts.LegalFatherName + " (" + facts.LegalFatherId + ")." : "Legal/acknowledged father is not established.",
                facts.MarriageAtConceptionKnown ? (facts.SpouseAtConceptionId.Length == 0 ? "You were unmarried at conception." : "Spouse at conception: " + facts.SpouseAtConceptionId + ".") : "Marriage status at conception is unverified; current marriage does not prove historical status.",
                facts.Illicit ? "This is an illicit pregnancy: unmarried conception or biological paternity outside the recorded marriage. Concealment may avert scandal until others learn the relevant facts." : "No illicit circumstance is confirmed. A child between spouses is ordinary family context and carries no scandal penalty.",
                FatherMismatch(facts) ? "Biological and legal father differ. You may deliberately let your husband or others believe the child is his; a spoken claim never changes true parentage." : "Do not invent concealed non-husband paternity.",
                Visible(facts) ? "At least halfway along: your pregnancy is unmistakable to people actually seeing you. You may evade questions, but cannot plausibly dismiss it as clothing or deny pregnancy to those observers. Paternity remains separately secret." : "Pregnancy is not yet established as visibly unmistakable. Others do not automatically know it.",
                "Disclosure of pregnancy, illicit circumstances and biological father are separate: mother exposed=" + facts.MotherExposed + "; father named=" + facts.FatherExposed + ".",
                "Choose candor, guardedness, deception or disclosure from your personality, relationships, trust and danger. Do not manufacture motives, fetal sex, past conversations or certainty about missing facts. Keep the true pregnancy in your internal decision even if your spoken words conceal it."
            };
            if (Progress(facts) is double progress) lines.Insert(3, "Pregnancy progress: " + (progress * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " percent; due campaign day " + facts.DueDay.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ".");
            else lines.Insert(3, "Pregnancy timing is unknown; do not invent a stage or due date.");
            return string.Join("\n", lines);
        }

        public static string ObserverPrompt(PregnancyFacts facts, PregnancyKnowledge knowledge, bool seeingMother)
        {
            if (!facts.Active) return "";
            if (!knowledge.Pregnancy && !(seeingMother && Visible(facts)) && !facts.MotherExposed)
                return "Pregnancy/private paternity about other people is not supplied as your knowledge. Do not infer their absence or invent a discovery.";
            string text = "KNOWN PREGNANCY: " + facts.MotherName + " (" + facts.MotherId + ") is pregnant.";
            if (seeingMother && Visible(facts)) text += " You can see the unmistakable pregnancy; denial based on clothing is implausible.";
            if (knowledge.Illicit || facts.MotherExposed) text += facts.Illicit ? " Its illicit circumstances are known to you." : " No scandal is established.";
            if (knowledge.Paternity || facts.FatherExposed) text += " Biological father: " + facts.BiologicalFatherName + " (" + facts.BiologicalFatherId + ").";
            else text += " Biological paternity is not confirmed to you. A husband's presumed fatherhood is a belief, not proof; do not assert hidden paternity.";
            return text;
        }
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
