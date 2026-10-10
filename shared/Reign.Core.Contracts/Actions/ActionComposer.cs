using System;

namespace Reign.Core.Contracts.Actions
{
    /// <summary>A plain-text composer with one attached, indivisible structured agreement.</summary>
    public sealed class ActionComposer
    {
        private string _attachedHash = "";
        public string Text { get; private set; } = "";
        public ActionAgreement? Draft { get; private set; }
        public int ActionStart { get; private set; } = -1;
        public int ActionLength { get; private set; }
        public int Caret { get; private set; }
        public int SelectionStart { get; private set; }
        public int SelectionEnd { get; private set; }
        public int Version { get; private set; }

        public void Select(int caret, int start, int end)
        {
            Caret = Clamp(caret);
            SelectionStart = Clamp(Math.Min(start, end));
            SelectionEnd = Clamp(Math.Max(start, end));
        }

        public void Replace(int start, int length, string replacement)
        {
            if (start < 0 || length < 0 || start > Text.Length - length)
                throw new ArgumentOutOfRangeException(nameof(start));
            replacement = replacement ?? "";
            int end = start + length;
            if (Draft != null)
            {
                int actionEnd = ActionStart + ActionLength;
                bool touches = length == 0
                    ? start > ActionStart && start < actionEnd
                    : start < actionEnd && end > ActionStart;
                if (touches)
                {
                    start = Math.Min(start, ActionStart);
                    end = Math.Max(end, actionEnd);
                    Detach();
                }
                else if (end <= ActionStart)
                    ActionStart += replacement.Length - length;
            }
            Text = Text.Substring(0, start) + replacement + Text.Substring(end);
            Select(start + replacement.Length, start + replacement.Length, start + replacement.Length);
            Version++;
        }

        public void Insert(ActionAgreement agreement)
        {
            if (agreement == null || string.IsNullOrWhiteSpace(agreement.Sentence))
                throw new ArgumentException("An action sentence is required.", nameof(agreement));
            int start = Draft == null ? SelectionStart : ActionStart;
            int end = Draft == null ? SelectionEnd : ActionStart + ActionLength;
            Detach();
            string prefix = start > 0 && !char.IsWhiteSpace(Text[start - 1]) ? " " : "";
            string suffix = end < Text.Length && !char.IsWhiteSpace(Text[end]) ? " " : "";
            string sentence = agreement.Sentence;
            Replace(start, end - start, prefix + sentence + suffix);
            Draft = agreement;
            ActionStart = start + prefix.Length;
            ActionLength = sentence.Length;
            _attachedHash = agreement.TermsHash();
        }

        // Programmatic replacements are deliberate resets. Native edits use exact ranges;
        // searching for an equal sentence could cancel or move the wrong occurrence.
        public void SetText(string text)
        {
            text = text ?? "";
            if (text == Text) return;
            Detach();
            Text = text;
            Select(text.Length, text.Length, text.Length);
            Version++;
        }

        public bool HasValidAttachment => Draft != null && ActionStart >= 0 && ActionLength > 0
            && ActionStart <= Text.Length - ActionLength
            && string.Equals(_attachedHash, Draft.TermsHash(), StringComparison.Ordinal)
            && string.Equals(Text.Substring(ActionStart, ActionLength), Draft.Sentence, StringComparison.Ordinal);

        private int Clamp(int value) => Math.Max(0, Math.Min(Text.Length, value));
        private void Detach() { Draft = null; ActionStart = -1; ActionLength = 0; _attachedHash = ""; }
    }
}
