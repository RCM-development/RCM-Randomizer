using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace RCM_Randomizer
{
    // Rewrites the numbers in a rolled hack's or upgrade's description. It used to multiply EVERY
    // number by the roll's factor, and a description holds more than its card changes: seconds and
    // counts of the prefab behaviour ("(45 sec.)" became 51 while the buff still lasted 45), a
    // multiplier ("3x longer" became 4x on a value of 3.6), and digits inside markup - the Mantis
    // hack's "$MantisMech:Duration2$" became "Duration3", a stat that does not exist.
    //
    // Now a number is rewritten only when it IS one of the card's change magnitudes - a Multiply as
    // its percent (1.25 -> "25") or as "Nx", an Add as its size - and it becomes that same change's
    // new magnitude, so the text states the value the game applies. Everything else is left alone,
    // including numbers glued to a word or inside $...$ / #...# tags. Where the game's own text and
    // value already disagree (Focused Blast says 50%, applies 40% and 60%) the text is left as shipped.
    public static class RollText
    {
        public struct Change { public CardChangeScriptableObject.Operation Op; public float Before, After; }

        static readonly Regex Number = new Regex(@"(?<![\w.$#])\d+(?:[.,]\d+)?(?=x\b|[^\w]|$)", RegexOptions.Compiled);

        public static string Rewrite(string text, IList<Change> changes)
        {
            if (string.IsNullOrEmpty(text) || changes == null || changes.Count == 0) return text;
            return Number.Replace(text, m =>
            {
                string token = m.Value;
                if (!float.TryParse(token.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out float value)) return token;
                bool isDecimal = token.IndexOf('.') >= 0 || token.IndexOf(',') >= 0;
                int end = m.Index + m.Length;
                bool asMultiplier = end < text.Length && text[end] == 'x';
                float tolerance = isDecimal ? 0.051f : 0.5f;
                foreach (var c in changes)
                {
                    float before, after;
                    if (c.Op == CardChangeScriptableObject.Operation.Multiply)
                    {
                        if (asMultiplier) { before = c.Before; after = c.After; }
                        else { before = Math.Abs(c.Before - 1f) * 100f; after = Math.Abs(c.After - 1f) * 100f; }
                    }
                    else { before = Math.Abs(c.Before); after = Math.Abs(c.After); }
                    if (before < 0.001f || Math.Abs(value - before) > tolerance) continue;
                    if (isDecimal || asMultiplier) return after.ToString("0.#", CultureInfo.InvariantCulture);
                    return Math.Max(1, (int)Math.Round(after, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);
                }
                return token;
            });
        }

        public static List<Change> Of(IList<CardChangeScriptableObject> changes)
        {
            var list = new List<Change>();
            if (changes == null) return list;
            foreach (var c in changes)
                if (c != null) list.Add(new Change { Op = c.operation, Before = ChangeScaleLedger.OriginalOf(c), After = c.value });
            return list;
        }
    }
}
