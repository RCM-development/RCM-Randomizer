using System;
using System.Collections.Generic;
using System.Diagnostics;
using TestMod;

namespace RCM_Randomizer
{
    // Attribution for frame hitches. Every hook we own reports how long it took; anything over
    // the threshold is logged immediately with its entity, and a rolling per-frame total is
    // logged when a single frame spends more than BudgetMs inside our code. Without this we are
    // guessing at which mod owns a stutter.
    public static class HookProfiler
    {
        public static bool Enabled = true;
        public const double SingleCallMs = 3.0;
        public const double FrameBudgetMs = 8.0;

        static readonly Dictionary<string, double> FrameCost = new Dictionary<string, double>();
        static int _frame = -1;

        public struct Scope : IDisposable
        {
            Stopwatch _watch;
            string _label;
            string _detail;

            public static Scope Start(string label, string detail)
            {
                if (!Enabled) return default;
                return new Scope { _watch = Stopwatch.StartNew(), _label = label, _detail = detail };
            }

            public void Dispose()
            {
                if (_watch == null) return;
                _watch.Stop();
                Report(_label, _detail, _watch.Elapsed.TotalMilliseconds);
            }
        }

        public static Scope Measure(string label, string detail = null) => Scope.Start(label, detail);

        static void Report(string label, string detail, double ms)
        {
            if (ms >= SingleCallMs)
                RCMManager.Log($"Randomizer PERF: {label} took {ms:F1}ms" + (detail != null ? " (" + detail + ")" : ""));

            int frame = UnityEngine.Time.frameCount;
            if (frame != _frame)
            {
                FlushFrame();
                _frame = frame;
            }
            FrameCost[label] = FrameCost.TryGetValue(label, out double sum) ? sum + ms : ms;
        }

        static void FlushFrame()
        {
            if (FrameCost.Count == 0) return;
            double total = 0;
            foreach (var cost in FrameCost.Values) total += cost;
            if (total >= FrameBudgetMs)
            {
                var parts = new List<string>();
                foreach (var entry in FrameCost) parts.Add($"{entry.Key} {entry.Value:F1}ms");
                RCMManager.Log($"Randomizer PERF: frame spent {total:F1}ms in our hooks [{string.Join(", ", parts)}]");
            }
            FrameCost.Clear();
        }
    }
}
