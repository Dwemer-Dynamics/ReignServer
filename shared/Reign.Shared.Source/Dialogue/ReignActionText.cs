using System;
using System.Collections.Generic;
using System.Text;

namespace ReignBeta.Dialogue
{
    // Shared by the native renderer and server contracts. Only our generated span
    // tags are executable markup; model supplied tags can never become UI controls.
    public static class ReignActionText
    {
        public sealed class Segment
        {
            public string Text { get; internal set; }
            public bool IsAction { get; internal set; }
        }

        public static List<Segment> Parse(string text)
        {
            text = text ?? string.Empty;
            var result = new List<Segment>();
            var plain = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] == '*')
                { plain.Append('*'); i++; continue; }
                if (text[i] != '*' || (i > 0 && text[i - 1] == '*')
                    || (i + 1 < text.Length && (text[i + 1] == '*' || char.IsWhiteSpace(text[i + 1]))))
                { plain.Append(text[i]); continue; }
                int close = i + 1;
                for (; close < text.Length; close++)
                {
                    if (text[close] == '\\' && close + 1 < text.Length && text[close + 1] == '*') { close++; continue; }
                    if (text[close] == '*') break;
                }
                if (close == text.Length || close == i + 1 || char.IsWhiteSpace(text[close - 1])
                    || (close + 1 < text.Length && text[close + 1] == '*'))
                { plain.Append(text[i]); continue; }
                if (plain.Length > 0) { result.Add(new Segment { Text = plain.ToString() }); plain.Clear(); }
                result.Add(new Segment { Text = text.Substring(i + 1, close - i - 1).Replace("\\*", "*"), IsAction = true });
                i = close;
            }
            if (plain.Length > 0) result.Add(new Segment { Text = plain.ToString() });
            return result;
        }

        public static string ToRichText(string text, bool actionsEnabled = true)
        {
            // Bound before parsing, so truncation cannot leave an open native tag.
            text = text ?? string.Empty;
            bool truncated = text.Length > 4000;
            if (truncated) text = text.Substring(0, 4000);
            var builder = new StringBuilder();
            foreach (Segment segment in actionsEnabled ? Parse(text) : new List<Segment> { new Segment { Text = text } })
            {
                if (segment.IsAction) builder.Append("<span style=\"Action\">");
                int run = 0;
                foreach (char ch in segment.Text)
                {
                    if (ch == '\r' || (char.IsControl(ch) && ch != '\n' && ch != '\t')) continue;
                    // Native rich text does not implement general HTML entity escaping.
                    // Neutralize all supplied delimiters before adding trusted tags.
                    char safe = ch == '<' ? '(' : ch == '>' ? ')' : ch == '\t' ? ' ' : ch;
                    if (char.IsWhiteSpace(safe)) run = 0;
                    else if (++run > 64) { builder.Append(' '); run = 1; }
                    builder.Append(safe);
                }
                if (segment.IsAction) builder.Append("</span>");
            }
            if (truncated) builder.Append("...");
            return builder.ToString();
        }
    }
}
