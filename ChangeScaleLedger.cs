using System.Collections.Generic;

namespace RCM_Randomizer
{
    // One record of every card-change asset a roll has scaled, shared by UpgradeRolls and RelicRolls.
    // Change assets are shared between cards - the "-10 crystals" of the Discount upgrade and of the
    // Cheaper Materials hack is ONE object - and each roller used to keep its own "already scaled"
    // set and its own saved values by index. So the object was scaled twice (x1.13 then x1.15), each
    // card's text named only its own factor, and restoring wrote back whichever card's "original" came
    // last: the hack's, which was already the upgrade's scaled value, so every re-apply drifted further
    // and switching the mod off left the stock card at -11.3.
    //
    // Here an asset is scaled once, remembers its true original and which roller owns it, and only
    // that roller puts it back. A card that meets an asset someone else already scaled takes over that
    // factor for all its numbers, so its text and its values always move together.
    public static class ChangeScaleLedger
    {
        class Entry { public float Original, Factor; public string Owner; }
        static readonly Dictionary<CardChangeScriptableObject, Entry> Scaled = new Dictionary<CardChangeScriptableObject, Entry>();

        public static bool TryFactor(CardChangeScriptableObject change, out float factor)
        {
            factor = 1f;
            if (change == null || !Scaled.TryGetValue(change, out var entry)) return false;
            factor = entry.Factor;
            return true;
        }

        public static float OriginalOf(CardChangeScriptableObject change)
            => change != null && Scaled.TryGetValue(change, out var entry) ? entry.Original : change != null ? change.value : 0f;

        // the factor every card holding one of these assets must use, if any of them is already scaled
        public static bool TryAdopt(IEnumerable<CardChangeScriptableObject> changes, out float factor)
        {
            factor = 1f;
            foreach (var change in changes)
                if (TryFactor(change, out factor)) return true;
            return false;
        }

        // true when this call scaled the asset; false when it was already scaled (by this factor or another)
        public static bool Scale(CardChangeScriptableObject change, float factor, string owner)
        {
            if (change == null || Scaled.ContainsKey(change)) return false;
            Scaled[change] = new Entry { Original = change.value, Factor = factor, Owner = owner };
            // Multiply changes carry their magnitude as (value - 1); Add changes carry it directly
            if (change.operation == CardChangeScriptableObject.Operation.Multiply)
                change.value = 1f + (change.value - 1f) * factor;
            else
                change.value *= factor;
            return true;
        }

        public static void Restore(string owner)
        {
            var done = new List<CardChangeScriptableObject>();
            foreach (var entry in Scaled)
            {
                if (entry.Value.Owner != owner) continue;
                if (entry.Key != null) entry.Key.value = entry.Value.Original;
                done.Add(entry.Key);
            }
            foreach (var change in done) Scaled.Remove(change);
        }
    }
}
