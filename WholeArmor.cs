using HarmonyLib;
using UnityEngine;

namespace RCM_Randomizer
{
    // Armor is a whole number on every stock unit, and the badge beside the health bar prints it with
    // a bare ToString(). Stat rolls already snap to a whole result (RollEngine.SnapToWholeResult), but
    // armor is also changed by RELATIVE effects stacked at runtime - hacks, the Harvest Tuner's aura,
    // veterancy on top of a vanilla percentage - and any of those on a 1-armor unit prints 3.192925
    // next to twenty Claw Bots. Rounded at the source so the number the badge shows is the number
    // the damage formula uses.
    [HarmonyPatch(typeof(EntityController), "ArmorProtection", MethodType.Getter)]
    static class WholeArmor
    {
        static void Postfix(ref float __result)
        {
            ModCost.ArmorCalls++;
            if (__result > -1e30f) __result = Mathf.Round(__result);
        }
    }
}
