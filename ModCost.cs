using System.Diagnostics;

namespace RCM_Randomizer
{
    // "Is the late-game slowdown the mod or the game" cannot be answered by looking at the mod's own
    // log: the first battle census showed 93 fps at 40 units falling to 37 fps at 220, which is the
    // shape of a per-unit cost, but every RTS has per-unit costs of its own (the game re-scans for
    // enemies in range for every unit, every frame) and nothing in the log separated them.
    //
    // So the hooks that run per unit or per second add themselves up here and the census reports the
    // total alongside the frame rate. Stopwatch.GetTimestamp is a counter read of a few tens of
    // nanoseconds - affordable in a hook that runs hundreds of times a frame, not in one that runs
    // thousands, which is why the armor getter is counted rather than timed.
    public static class ModCost
    {
        public enum Slot { Watchdog, Economy, EnemyAi, Veterancy, Count }

        static readonly long[] Ticks = new long[(int)Slot.Count];
        static readonly long[] Calls = new long[(int)Slot.Count];
        public static long ArmorCalls;

        public static long Start() => Stopwatch.GetTimestamp();

        public static void Stop(Slot slot, long start)
        {
            Ticks[(int)slot] += Stopwatch.GetTimestamp() - start;
            Calls[(int)slot]++;
        }

        // ms spent in each slot since the last Take, and the slots are cleared
        public static string Take(int frames)
        {
            double total = 0;
            var parts = new System.Text.StringBuilder();
            for (int i = 0; i < (int)Slot.Count; i++)
            {
                double ms = Ticks[i] * 1000.0 / Stopwatch.Frequency;
                total += ms;
                if (Calls[i] > 0)
                {
                    if (parts.Length > 0) parts.Append(", ");
                    parts.Append($"{(Slot)i} {ms / System.Math.Max(1, frames):F3}");
                }
                Ticks[i] = 0;
                Calls[i] = 0;
            }
            string armor = ArmorCalls > 0 ? $", armor getter {ArmorCalls / System.Math.Max(1, frames)}/frame" : "";
            ArmorCalls = 0;
            return $"{total / System.Math.Max(1, frames):F3}ms/frame in mod hooks"
                + (parts.Length > 0 ? " [" + parts + armor + "]" : armor);
        }
    }
}
