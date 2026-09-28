using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace RCM_Randomizer
{
    // Three-tier veterancy: bronze, silver, gold. What the stock game actually has (read off the
    // prefabs with Diagnostics.DumpPrefabFacts, not assumed): a CurrentRank counter from 0 to
    // MaxRank on every entity and one icon that lights at the last rank - and nothing else. No unit
    // prefab, prefab mod or start mod ever calls RankUp, and the only thing that reacts to a rank
    // is one upgrade card. So out of the box units neither earn ranks nor gain from them.
    //
    // This supplies the whole ladder:
    //   earning - kills bank credits, weighted by what the victim was worth. Bronze costs
    //             BronzeCost, silver three times that, gold eight times: 6 / 18 / 48 by default, so
    //             a gold unit has destroyed about 72 tanks' worth and is a rare sight;
    //   payout  - BonusPerTier damage and max health per tier (15 / 30 / 45 percent), through the
    //             game's own rank-scaled value change (one mod on OnRankChanged, rewritten at each
    //             rank, never stacking);
    //   display - the game's veteran icon, tinted bronze, silver or gold. The icon is a slot inside
    //             a HorizontalLayoutGroup, so nothing is ever cloned into that row (0.9.0 did, and
    //             the copy landed beyond the armor badge), and the sprite is already a stack of
    //             chevrons, so nothing is stacked on it either (0.9.1's fourth and fifth rank did,
    //             and read as a tall yellow ladder).
    // The table gives most units maxRank 5; only the first three are used.
    public static class Veterancy
    {
        public static bool Enabled;
        public static bool EarnFromKills = true;
        public static float BonusPerRank = 0.15f;   // damage and max health, per tier

        public static bool EscalatingRanks = true;
        public static float RankCost = 6f;          // bronze; a 100-credit victim is one credit
        static readonly float[] TierCostFactor = { 1f, 3f, 8f };

        const string MarkPrefix = "rcmRankMark";
        const string ModName = "rcmmod_veterancy";
        const string LocaKey = "rcm_randomizer_veterancy";
        public const int Tiers = 3;

        static readonly Color Bronze = new Color(0.80f, 0.50f, 0.28f, 1f);
        static readonly Color Silver = new Color(0.82f, 0.86f, 0.92f, 1f);
        static readonly Color Gold = new Color(1.00f, 0.80f, 0.22f, 1f);
        static readonly Color[] TierColor = { Bronze, Silver, Gold };

        // instance id -> credits banked toward the next rank
        static readonly Dictionary<int, float> Credits = new Dictionary<int, float>();
        static bool _grantingFromCredits;
        static EntityModScriptableObject _bonusMod, _engineerBonusMod;

        // an engineer's career is slower and worth more: see EngineerVeterancy
        // an engineer rank's price, without needing a live engineer (career files are converted at load)
        internal static float EngineerRankCost(int rank)
            => RankCost * TierCostFactor[Mathf.Clamp(rank, 1, Tiers) - 1] * EngineerVeterancy.CostFactor;

        static float CostOf(EntityController entity, int rank)
            => RankCost * TierCostFactor[Mathf.Clamp(rank, 1, Tiers) - 1] * (EngineerVeterancy.Applies(entity) ? EngineerVeterancy.CostFactor : 1f);

        static int MaxTier(EntityController entity) => Math.Min(entity.MaxRank, Tiers);

        public static string Describe(int tier) => tier <= 0 ? "unranked" : new[] { "bronze", "silver", "gold" }[Math.Min(tier, Tiers) - 1];

        static int TierOf(EntityController entity) => Mathf.Clamp(entity.CurrentRank, 0, MaxTier(entity));


        internal static void AddCredits(EntityController entity, float amount)
        {
            int max = MaxTier(entity);
            if (max <= 0 || entity.CurrentRank >= max) return;

            int id = entity.GetInstanceID();
            Credits.TryGetValue(id, out float credits);
            credits += amount;

            int rank = entity.CurrentRank, granted = 0;
            while (rank + granted < max && credits >= CostOf(entity, rank + granted + 1))
            {
                credits -= CostOf(entity, rank + granted + 1);
                granted++;
            }
            Credits[id] = credits;
            if (EngineerVeterancy.Applies(entity)) EngineerVeterancy.OnCreditsChanged(entity, credits);
            if (granted > 0) Grant(entity, granted);
        }

        internal static void SetCredits(EntityController entity, float credits) => Credits[entity.GetInstanceID()] = credits;

        // a rank that has been paid for: passes the RankUp meter instead of being banked again
        internal static void Grant(EntityController entity, int ranks)
        {
            _grantingFromCredits = true;
            try { entity.RankUp(ranks); }
            finally { _grantingFromCredits = false; }
        }

        static float KillValue(EntityController victim)
        {
            try
            {
                float cost = EntityBalancingStore.Cost(victim.entityId, returnOriginalValueFromBalancingFile: true);
                return Mathf.Clamp(cost / 100f, 0.25f, 2.5f); // swarm spawns count a quarter, a capital unit 2.5
            }
            catch { return 1f; }
        }

        static EntityModScriptableObject BuildBonusMod(string name, float perRank, string originator)
        {
            var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
            mod.name = name;
            mod.entityIdentifiers = new List<EntityIdentifier>();
            mod.events = new List<EntityEvent>
            {
                BehaviourMods.Event(EntityController.Event.OnRankChanged,
                    BehaviourMods.RankScaled(EntityController.ChangeableValue.Damage, SpecificValueChange.AddType.Relative, perRank, originator + "Damage"),
                    BehaviourMods.RankScaled(EntityController.ChangeableValue.MaxHealth, SpecificValueChange.AddType.Relative, perRank, originator + "Health")),
            };
            return mod;
        }

        static EntityModScriptableObject BonusMod(EntityController entity)
        {
            if (EngineerVeterancy.Applies(entity))
                return _engineerBonusMod ?? (_engineerBonusMod = BuildBonusMod(ModName + "_engineer", BonusPerRank * EngineerVeterancy.BonusFactor, "rcmVeterancyEngineer"));
            return _bonusMod ?? (_bonusMod = BuildBonusMod(ModName, BonusPerRank, "rcmVeterancy"));
        }

        // the bonus size is baked into the mod's actions, so a config change needs a new asset
        public static void Configure(float bonusPerRank)
        {
            if (Math.Abs(bonusPerRank - BonusPerRank) < 0.0001f) return;
            BonusPerRank = bonusPerRank;
            _bonusMod = _engineerBonusMod = null;
            Array.Clear(_inheritedMods, 0, _inheritedMods.Length);
        }

        // ---- what a veteran puts on the field ---------------------------------------------------
        // The rank bonus raises the unit's OWN damage, which is what DealDamage reads when the unit
        // itself hits. Half the game's active skills hit through something they spawn instead - a
        // mine, the Firebrand's thump, the Juggernaut's launch, a deployed turret, a creeper - and
        // that entity deals its own damage, which no rank ever reached (34 of 68 skills, counted off
        // the prefabs by the probe). A roof gun is its own entity too. So what a ranked unit spawns,
        // and the roof gun it carries, fights at the unit's tier: the same damage and health bonus,
        // through one permanent value change per tier.
        static readonly Dictionary<int, int> InheritedTier = new Dictionary<int, int>();
        static readonly HashSet<string> LoggedInheritance = new HashSet<string>();
        static readonly EntityModScriptableObject[] _inheritedMods = new EntityModScriptableObject[Tiers + 1];

        // the unit's own tier, what it inherited, and its carrier's (a roof gun is a child)
        public static int EffectiveTier(EntityController entity)
        {
            if (entity == null) return 0;
            int tier = TierOf(entity);
            if (InheritedTier.TryGetValue(entity.GetInstanceID(), out int inherited)) tier = Math.Max(tier, inherited);
            if (entity.Parent != null && entity.Parent != entity) tier = Math.Max(tier, TierOf(entity.Parent));
            return Mathf.Clamp(tier, 0, Tiers);
        }

        static EntityModScriptableObject InheritedMod(int tier)
        {
            if (_inheritedMods[tier] != null) return _inheritedMods[tier];
            var mod = ScriptableObject.CreateInstance<EntityModScriptableObject>();
            mod.name = ModName + "_inherited" + tier;
            mod.entityIdentifiers = new List<EntityIdentifier>();
            mod.events = new List<EntityEvent>
            {
                BehaviourMods.Event(EntityController.Event.OnAddedThisEntityModToEntity,
                    Permanent(EntityController.ChangeableValue.Damage, tier * BonusPerRank, "rcmVeterancyInheritedDamage"),
                    Permanent(EntityController.ChangeableValue.MaxHealth, tier * BonusPerRank, "rcmVeterancyInheritedHealth")),
            };
            return _inheritedMods[tier] = mod;
        }

        // not stackable, one originator for every tier: a higher tier REPLACES the lower grant
        static ChangeSpecificValue Permanent(EntityController.ChangeableValue value, float relative, string originator) =>
            new ChangeSpecificValue
            {
                operatingEntities = MultipleEntitiesActionWithoutUpdate.OperatingEntities.Self,
                valueToChange = value,
                addType = SpecificValueChange.AddType.Relative,
                valueToAddSource = ChangeSpecificValue.ValueToAddSource.One,
                multiplier = relative,
                isStackable = false,
                originatorIdOption = ChangeSpecificValue.OriginatorIdOption.GivenString,
                originatorId = originator,
                durationType = ChangeSpecificValue.DurationType.Unlimited,
            };

        public static void Inherit(EntityController entity, int tier)
        {
            if (!Enabled || entity == null || BonusPerRank <= 0f) return;
            tier = Mathf.Clamp(tier, 0, Tiers);
            if (tier <= 0) return;
            int id = entity.GetInstanceID();
            if (InheritedTier.TryGetValue(id, out int had) && had >= tier) return;
            InheritedTier[id] = tier;
            entity.AddEntityMod(InheritedMod(tier), new CardId(CardId.CardType.GlobalLocaId, LocaKey));
        }

        static void PassOnToChildren(EntityController entity)
        {
            int tier = EffectiveTier(entity);
            if (tier <= 0) return;
            foreach (var child in entity._registeredChildControllers)
                if (child != null && child != entity) Inherit(child, tier);
        }

        [HarmonyPatch(typeof(SpawnObject), "MakeEntityControllerAdjustments")]
        static class Patch_SpawnObject_Adjust
        {
            static void Postfix(EntityController spawnedEntity, EventPayload payload)
            {
                if (!Enabled || spawnedEntity == null) return;
                try
                {
                    var owner = payload.Self;
                    if (owner == null || owner == spawnedEntity) return;
                    int tier = EffectiveTier(owner);
                    if (tier <= 0) return;
                    Inherit(spawnedEntity, tier);
                    if (owner.IsControlledByPlayer && LoggedInheritance.Add(owner.entityId + ">" + spawnedEntity.entityId + ">" + tier))
                        TestMod.RCMManager.Log($"Randomizer: {Describe(tier)} {owner.entityId}'s {spawnedEntity.entityId} fights at its rank (+{tier * BonusPerRank:P0} damage and health)");
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy inheritance failed (" + e.Message + ")"); }
            }
        }

        static void Refresh(EntityController entity)
        {
            if (!Enabled) return;
            var baseParams = entity.baseParameters;
            var slot = baseParams != null ? baseParams.veteranIconGameObject : null;
            if (slot == null) return;

            int tier = TierOf(entity);
            if (EngineerVeterancy.Applies(entity)) EngineerVeterancy.OnDisplay(entity, tier);
            slot.SetActive(tier >= 1);
            if (tier < 1) return;

            bool tinted = false;
            foreach (var image in slot.GetComponentsInChildren<Image>(true))
            {
                // marks stacked by 0.9.1 can survive on a pooled body
                if (image.name.StartsWith(MarkPrefix, StringComparison.Ordinal)) { image.gameObject.SetActive(false); continue; }
                if (!tinted) image.color = TierColor[tier - 1];
                tinted = true;
            }
        }


        [HarmonyPatch(typeof(EntityController), "OnHasKilledEntity")]
        static class Patch_OnHasKilledEntity
        {
            static void Postfix(EntityController __instance, EntityController entityThatWillBeDestroyed)
            {
                if (!Enabled || !EarnFromKills || entityThatWillBeDestroyed == null) return;
                try
                {
                    // a roof turret is a child entity: its kills belong to the tank carrying it
                    var earner = __instance.Parent != null ? __instance.Parent : __instance;
                    AddCredits(earner, KillValue(entityThatWillBeDestroyed));
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy credit failed (" + e.Message + ")"); }
            }
        }

        [HarmonyPatch(typeof(EntityController), "RankUp")]
        static class Patch_RankUp
        {
            // A rank handed out by a card's own RankUp action goes through the same meter as a
            // kill instead of skipping the rising price. Returning false banks it without a rank
            // change, so OnRankChanged correctly does not fire.
            static bool Prefix(EntityController __instance, int amount, out int __state)
            {
                __state = __instance.CurrentRank;
                if (!Enabled || !EscalatingRanks || _grantingFromCredits || amount <= 0) return true;
                try { AddCredits(__instance, amount); return false; }
                catch { return true; } // never swallow a rank because our accounting broke
            }

            static void Postfix(EntityController __instance, int __state)
            {
                try
                {
                    Refresh(__instance);
                    if (__instance.CurrentRank == __state) return;
                    if (_grantingFromCredits && __instance.IsControlledByPlayer && !EngineerVeterancy.IsRestoring)
                        TestMod.RCMManager.Log($"Randomizer: {__instance.entityId} reached {Describe(TierOf(__instance))} ({TierOf(__instance)}/{MaxTier(__instance)})");
                    if (EngineerVeterancy.Applies(__instance)) EngineerVeterancy.OnRankReached(__instance, __state);
                    // a gold unit earns its roof gun, then everything on it fights at its tier
                    RoofTurrets.OnRankChanged(__instance, TierOf(__instance), MaxTier(__instance));
                    PassOnToChildren(__instance);
                }
                catch (Exception e) { TestMod.RCMManager.Log("Randomizer: veterancy display failed (" + e.Message + ")"); }
            }
        }

        // Init resets CurrentRank to 0 and pooled bodies are reused, so credits and marks from a
        // previous life must not carry over; the bonus mod is (re)attached here as well.
        [HarmonyPatch(typeof(EntityController), "Init")]
        static class Patch_Init
        {
            static void Postfix(EntityController __instance)
            {
                try
                {
                    Credits.Remove(__instance.GetInstanceID());
                    InheritedTier.Remove(__instance.GetInstanceID()); // a pooled body starts over
                    Refresh(__instance);
                    if (!Enabled || __instance.MaxRank <= 0) return;
                    if (BonusPerRank > 0f)
                        __instance.AddEntityMod(BonusMod(__instance), new CardId(CardId.CardType.GlobalLocaId, LocaKey));
                    // after the mod is on, so the restored ranks pay their bonus like earned ones
                    if (EngineerVeterancy.Applies(__instance)) EngineerVeterancy.OnInit(__instance);
                }
                catch { }
            }
        }

        public const string TooltipLocaKey = LocaKey;
    }
}
