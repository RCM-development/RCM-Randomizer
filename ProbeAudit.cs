using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace RCM_Randomizer
{
    // The balance audit's raw material. Every other probe section prints the balancing FILE's numbers,
    // and what a player actually builds is those numbers after stat rolls, weapon repricing, the
    // unlock rebuild and every card change on top - so an audit of the file is an audit of vanilla.
    // One tab-separated row per player card, original and effective side by side, written to
    // BepInEx\RandomizerAudit.tsv for analysis outside the game.
    public static class ProbeAudit
    {
        public static void Write(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join("\t", new[] {
                "card", "unit", "kind", "level", "rarity", "donor", "roles",
                "cost0", "cost", "build0", "build", "cap",
                "hp0", "hp", "shield", "armor0", "armor",
                "dmg0", "dmg", "cd0", "cd", "range0", "range", "splash", "barrels",
                "dps0", "dps", "speed", "sight", "mana", "skillCost", "income", "cap0", "unitCap", "unitCost", "template" }));
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                // Titans are measured even while locked: they are the content most likely to be mispriced
                if (!row.isAllowedAsBlueprint || (row.inactive && !Titans.IsGenerated(row.entityId))) continue;
                try
                {
                    string card = row.entityId;
                    string unit = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : card;
                    string kind = Titans.IsGenerated(card) ? "titan" : SalvagedTech.IsGenerated(card) ? "salvage"
                                : EconomyBuildings.IsGenerated(card) ? "economy" : unit == card ? "building" : "unit";
                    string donorId = donorOf != null ? donorOf(unit) : null;
                    int barrels0 = Math.Max(1, EntityBalancingStore.FirePointCount(unit));
                    // a swapped unit fires from the DONOR's fire points: counting the host's here made a
                    // 16-tube Missile Mech with a one-barrel railgun read as a 16x damage buff
                    int barrels = string.IsNullOrEmpty(donorId) ? barrels0 : Math.Max(1, EntityBalancingStore.FirePointCount(donorId));
                    float dmg0 = EntityBalancingStore.Damage1(unit, true), dmg = EntityBalancingStore.Damage1(unit);
                    float cd0 = EntityBalancingStore.Attack1Cooldown(unit, true), cd = EntityBalancingStore.Attack1Cooldown(unit);
                    float range0 = EntityBalancingStore.WeaponRange(unit, true);
                    // a swapped weapon lives in the row, so the row no longer holds the vanilla numbers
                    if (WeaponRows.TryOriginal(unit, out float vd, out float vc, out float vr)) { dmg0 = vd; cd0 = vc; range0 = vr; }
                    float dps0 = cd0 > 0.01f ? dmg0 * barrels0 / cd0 : 0f, dps = cd > 0.01f ? dmg * barrels / cd : 0f;
                    string donor = donorId;
                    sb.AppendLine(string.Join("\t", new[] {
                        card, unit, kind, row.neededExperienceLevel.ToString(), row.rarity.ToString(), string.IsNullOrEmpty(donor) ? "-" : donor,
                        EntityBalancingStore.UnitRoles(unit).ToString().Replace(", ", "|"),
                        EntityBalancingStore.Cost(card, true).ToString(), EntityBalancingStore.Cost(card).ToString(),
                        F(EntityBalancingStore.ProductionDuration(card, true)), F(EntityBalancingStore.ProductionDuration(card)),
                        EntityBalancingStore.MaxCapacity(card).ToString(),
                        F(EntityBalancingStore.MaxHealth(unit, true)), F(EntityBalancingStore.MaxHealth(unit)), F(EntityBalancingStore.MaxShield(unit)),
                        F(EntityBalancingStore.ArmorProtection(unit, true)), F(EntityBalancingStore.ArmorProtection(unit)),
                        F(dmg0), F(dmg), F(cd0), F(cd),
                        F(range0), F(EntityBalancingStore.WeaponRange(unit)),
                        F(EntityBalancingStore.EffectRadius1(unit)), barrels0 + ">" + barrels,
                        F(dps0), F(dps), F(EntityBalancingStore.MoveSpeed(unit)), F(EntityBalancingStore.SightRadius(unit)),
                        F(EntityBalancingStore.MaxMana(unit)), F(EntityBalancingStore.SkillManaCost(unit)),
                        F(EntityBalancingStore.GainCreditsAmount(unit)), EntityBalancingStore.MaxCapacity(card, true).ToString(),
                        // units a factory keeps alive come from the UNIT's row (UnitCap.MaxPlayerCapacity); the
                        // card's own capacity is how many of the building may be placed
                        EntityBalancingStore.MaxCapacity(unit).ToString(),
                        unit == card ? "0" : EntityBalancingStore.Cost(unit).ToString(), SalvagedTech.TemplateOf(card) ?? "-" }));
                }
                catch (Exception e) { sb.AppendLine(row.entityId + "\tFAILED\t" + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
            WriteChanges(Path.ChangeExtension(path, null) + "Changes.txt");
            WriteDonorConditions(Path.ChangeExtension(path, null) + "Conditions.txt", donorOf);
            WriteCardEffects(Path.ChangeExtension(path, null) + "Effects.tsv");
            WriteIncome(Path.ChangeExtension(path, null) + "Income.txt");
            WriteStatUse(Path.ChangeExtension(path, null) + "StatUse.tsv");
            WriteRolledTexts(Path.ChangeExtension(path, null) + "Texts.txt");
            WriteMeta(Path.ChangeExtension(path, null) + "Meta.txt");
            WriteSpawnCost(Path.ChangeExtension(path, null) + "Spawn.txt", donorOf);
            WriteSpawnSides(Path.ChangeExtension(path, null) + "Spawns.tsv");
            WriteSideFixTest(Path.ChangeExtension(path, null) + "SideFix.txt", donorOf);
            WriteMuzzles(Path.ChangeExtension(path, null) + "Muzzles.txt", donorOf);
            WriteAimTest(Path.ChangeExtension(path, null) + "Aim.txt", donorOf);
            WriteAnimations(Path.ChangeExtension(path, null) + "Anim.txt", donorOf);
        }

        // After the real swap: every Animate on the host that writes a transform the aiming turns, and
        // whether a Stop with its id exists on the host. An animation the aiming fights with no Stop is a
        // gun that never reports ready (Animate.Update writes localRotation every frame it runs).
        static void WriteAnimations(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder("host <- donor | event | animate id loop transforms | touchesAiming | stop\n");
            var mixer = HarmonyLib.AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
            var initType = mixer != null ? HarmonyLib.AccessTools.Inner(mixer, "Patch_EntityController_Init") : null;
            var prefix = initType != null ? HarmonyLib.AccessTools.Method(initType, "Prefix") : null;
            if (prefix == null) { File.WriteAllText(path, "swap entry not found\n"); return; }
            var hosts = new System.Collections.Generic.List<(string host, string donor)>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                string d = null;
                try { d = donorOf?.Invoke(row.entityId); } catch { }
                if (!string.IsNullOrEmpty(d) && !hosts.Exists(h => h.host == row.entityId)) hosts.Add((row.entityId, d));
            }
            foreach (var (host, donor) in hosts)
            {
                UnityEngine.GameObject prefab = null, dormant = null;
                bool was = true;
                try
                {
                    prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(host)) as UnityEngine.GameObject;
                    if (prefab == null) continue;
                    was = prefab.activeSelf;
                    prefab.SetActive(false);
                    dormant = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                    prefab.SetActive(was);
                    var c = dormant.GetComponent<EntityController>();
                    c.entityId = host;
                    prefix.Invoke(null, new object[] { c, null });
                    var turned = new System.Collections.Generic.HashSet<UnityEngine.Transform>();
                    void Flatten(SingleTargetAction a)
                    {
                        if (a is SerialSingleTargetAction s) { foreach (var x in s.actions) Flatten(x); }
                        else if (a is RotateInSingleTargetDirectionAroundAxisAction ax && ax.transformToRotate != null) turned.Add(ax.transformToRotate);
                        else if (a is RotateInSingleTargetDirectionAction d && d.transformToRotate != null) turned.Add(d.transformToRotate);
                        else if (a is RotateToBallisticAngleSingleTargetAction b && b.transformToRotate != null) turned.Add(b.transformToRotate);
                    }
                    Flatten(c.aiming);
                    var stops = new System.Collections.Generic.Dictionary<string, string>();
                    var animates = new System.Collections.Generic.List<(string ev, Animate a)>();
                    if (c.events != null)
                        foreach (var ev in c.events)
                        {
                            if (ev == null) continue;
                            var all = new System.Collections.Generic.List<IEntityAction>(ev.actions ?? new System.Collections.Generic.List<IEntityAction>());
                            if (ev.conditionalActions != null) foreach (var ca in ev.conditionalActions) if (ca?.actions != null) all.AddRange(ca.actions);
                            foreach (var a in all)
                            {
                                if (a is Stop st && !string.IsNullOrEmpty(st.idToStop)) stops[st.idToStop] = ev.@event.ToString();
                                else if (a is Animate an) animates.Add((ev.@event.ToString(), an));
                            }
                        }
                    foreach (var (ev, an) in animates)
                    {
                        bool touches = false;
                        var names = new System.Collections.Generic.List<string>();
                        if (an.transforms != null)
                            foreach (var t in an.transforms)
                            {
                                if (t == null) { names.Add("null"); continue; }
                                names.Add(t.name);
                                if (turned.Contains(t)) touches = true;
                            }
                        string stop = !string.IsNullOrEmpty(an.id) && stops.TryGetValue(an.id, out string on) ? on + ":Stop" : "none";
                        sb.AppendLine($"{host} <- {donor} | {ev} | Animate id={an.id} loop={an.loop} [{string.Join(",", names)}] | {touches} | {stop}");
                    }
                }
                catch (Exception e) { sb.AppendLine($"{host} <- {donor} FAILED {(e.InnerException ?? e).Message}"); }
                finally
                {
                    if (prefab != null) prefab.SetActive(was);
                    if (dormant != null) UnityEngine.Object.DestroyImmediate(dormant);
                }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // The real swap, run at the menu: the mixer's Init prefix on a dormant host, then the transplanted
        // aiming driven with the game's own RotateTo toward eight targets around the unit. An around-axis
        // action reports ready only when the angle left in its own plane drops below one step, so the
        // residual is the number that decides whether the gun ever fires ("Grenadier 4x4 (A Tank)":
        // target at 1 cell for 12s, aiming never ready).
        static void WriteAimTest(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder("host <- donor | action transform axis | axis tilt from host up | worst residual deg over 8 targets | per target\n");
            var mixer = HarmonyLib.AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
            var initType = mixer != null ? HarmonyLib.AccessTools.Inner(mixer, "Patch_EntityController_Init") : null;
            var prefix = initType != null ? HarmonyLib.AccessTools.Method(initType, "Prefix") : null;
            var rotateTo = HarmonyLib.AccessTools.Method(typeof(RotateInSingleTargetDirectionAroundAxisAction), "RotateTo");
            var angleOff = HarmonyLib.AccessTools.Method(typeof(RotateInSingleTargetDirectionAroundAxisAction), "AngleOffAroundAxis");
            if (prefix == null || rotateTo == null || angleOff == null) { File.WriteAllText(path, "swap entry or aiming methods not found\n"); return; }
            var hosts = new System.Collections.Generic.List<(string host, string donor)>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                string d = null;
                try { d = donorOf?.Invoke(row.entityId); } catch { }
                if (!string.IsNullOrEmpty(d) && !hosts.Exists(h => h.host == row.entityId)) hosts.Add((row.entityId, d));
            }
            foreach (var (host, donor) in hosts)
            {
                UnityEngine.GameObject prefab = null, dormant = null;
                bool was = true;
                try
                {
                    prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(host)) as UnityEngine.GameObject;
                    if (prefab == null) continue;
                    was = prefab.activeSelf;
                    prefab.SetActive(false);
                    dormant = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                    prefab.SetActive(was);
                    var c = dormant.GetComponent<EntityController>();
                    c.entityId = host;
                    prefix.Invoke(null, new object[] { c, null });
                    var flat = new System.Collections.Generic.List<SingleTargetAction>();
                    void Flatten(SingleTargetAction a) { if (a is SerialSingleTargetAction s) { foreach (var x in s.actions) Flatten(x); } else if (a != null) flat.Add(a); }
                    Flatten(c.aiming);
                    var root = dormant.transform;
                    var axes = flat.FindAll(a => a is RotateInSingleTargetDirectionAroundAxisAction ax0 && ax0.transformToRotate != null)
                                   .ConvertAll(a => (RotateInSingleTargetDirectionAroundAxisAction)a);
                    var worst = new float[axes.Count];
                    var per = new System.Collections.Generic.List<string>[axes.Count];
                    for (int i = 0; i < axes.Count; i++) per[i] = new System.Collections.Generic.List<string>();
                    UnityEngine.Vector3 AxisOf(RotateInSingleTargetDirectionAroundAxisAction ax) => ax.direction == UnityEngine.RectTransform.Axis.Vertical ? ax.transformToRotate.right : ax.transformToRotate.up;
                    for (int k = 0; k < 8; k++)
                    {
                        float ang = k * 45f * UnityEngine.Mathf.Deg2Rad;
                        // 3 cells out on the ground plane: close enough to matter, far enough that the barrel's
                        // own pitch limits are what they are in a fight
                        var targetPos = root.position + new UnityEngine.Vector3(UnityEngine.Mathf.Sin(ang), 0f, UnityEngine.Mathf.Cos(ang)) * 30f;
                        // every action of the unit each step, turret before barrel, as the game runs them
                        for (int step = 0; step < 300; step++)
                            foreach (var ax in axes) rotateTo.Invoke(ax, new object[] { targetPos });
                        for (int i = 0; i < axes.Count; i++)
                        {
                            var t = axes[i].transformToRotate;
                            float residual = (float)angleOff.Invoke(null, new object[] { (targetPos - t.position).normalized, t.forward, AxisOf(axes[i]), false });
                            worst[i] = UnityEngine.Mathf.Max(worst[i], UnityEngine.Mathf.Abs(residual));
                            per[i].Add(residual.ToString("0.#", CultureInfo.InvariantCulture));
                        }
                    }
                    for (int i = 0; i < axes.Count; i++)
                    {
                        var ax = axes[i];
                        float tilt = UnityEngine.Vector3.Angle(AxisOf(ax), ax.direction == UnityEngine.RectTransform.Axis.Vertical ? AxisOf(ax) : root.up);
                        sb.AppendLine($"{host} <- {donor} | {ax.transformToRotate.name} {ax.direction} speed={ax.degreesPerSecond} arc={ax.minDegrees}..{ax.maxDegrees} | tilt {tilt:0.#} | worst {worst[i]:0.#} | {string.Join(" ", per[i])}");
                    }
                }
                catch (Exception e) { sb.AppendLine($"{host} <- {donor} FAILED {(e.InnerException ?? e).Message}"); }
                finally
                {
                    if (prefab != null) prefab.SetActive(was);
                    if (dormant != null) UnityEngine.Object.DestroyImmediate(dormant);
                }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // The swap keeps only the donor's turret pivot subtree; the donor clone is destroyed after. A
        // ShootProjectile whose fire points hang OUTSIDE that subtree keeps a reference to a destroyed
        // transform, and ShootProjectile's own ValidateTransform then skips the shot - silently. Checked
        // per donor on a dormant instance, with the mixer's own pivot finder.
        static void WriteMuzzles(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder("donor\tpivot\tfirePoints\tinsidePivot\thosts\n");
            var mixer = HarmonyLib.AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
            var pivotOf = mixer != null ? HarmonyLib.AccessTools.Method(mixer, "GetPivotFromAiming") : null;
            if (pivotOf == null) { File.WriteAllText(path, "mixer pivot finder not found\n"); return; }
            var hostsOf = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                string d = null;
                try { d = donorOf?.Invoke(row.entityId); } catch { }
                if (string.IsNullOrEmpty(d)) continue;
                if (!hostsOf.TryGetValue(d, out var list)) hostsOf[d] = list = new System.Collections.Generic.List<string>();
                if (!list.Contains(row.entityId)) list.Add(row.entityId);
            }
            foreach (var entry in hostsOf)
            {
                string donor = entry.Key;
                UnityEngine.GameObject prefab = null, dormant = null;
                bool was = true;
                try
                {
                    prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(donor)) as UnityEngine.GameObject;
                    if (prefab == null) continue;
                    was = prefab.activeSelf;
                    prefab.SetActive(false);
                    dormant = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                    var c = dormant.GetComponent<EntityController>();
                    var pivot = c?.aiming != null ? pivotOf.Invoke(null, new object[] { c.aiming }) as UnityEngine.Transform : null;
                    var shots = new System.Collections.Generic.List<ShootProjectile>();
                    if (c?.events != null)
                        foreach (var ev in c.events)
                            if (ev != null && Array.IndexOf(Copied, ev.@event) >= 0) CollectOfType(ev, shots, new System.Collections.Generic.HashSet<object>(), 0);
                    foreach (var shot in shots)
                    {
                        var fp = shot.firePointsTransform;
                        string inside = pivot == null ? "no pivot" : fp == null ? "no fire points" : (fp == pivot || fp.IsChildOf(pivot)).ToString();
                        sb.AppendLine($"{donor}\t{(pivot != null ? pivot.name : "-")}\t{(fp != null ? fp.name : "-")}\t{inside}\t{string.Join(",", entry.Value)}");
                    }
                    if (shots.Count == 0) sb.AppendLine($"{donor}\t{(pivot != null ? pivot.name : "-")}\t(no ShootProjectile)\t-\t{string.Join(",", entry.Value)}");
                    sb.AppendLine($"  donor aiming {(c?.aiming != null ? c.aiming.GetType().Name : "none")} melee={c?.melee}");
                    var flat = new System.Collections.Generic.List<SingleTargetAction>();
                    void Flatten(SingleTargetAction a) { if (a is SerialSingleTargetAction s) { foreach (var x in s.actions) Flatten(x); } else if (a != null) flat.Add(a); }
                    Flatten(c?.aiming);
                    foreach (var a in flat)
                    {
                        if (a is RotateInSingleTargetDirectionAroundAxisAction ax)
                            sb.AppendLine($"    around-axis {ax.transformToRotate?.name} dir={ax.direction} speed={ax.degreesPerSecond} min={ax.minDegrees} max={ax.maxDegrees} localRot={ax.transformToRotate?.localEulerAngles} up={ax.transformToRotate?.up}");
                        else if (a is RotateInSingleTargetDirectionAction d)
                            sb.AppendLine($"    direction {d.transformToRotate?.name} speed={d.degreesPerSecond}");
                        else sb.AppendLine($"    {a.GetType().Name}");
                    }
                    // the attack is built from the HOST's own settings, only aiming and melee come from the donor
                    foreach (string host in entry.Value)
                    {
                        try
                        {
                            var hp = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(host)) as UnityEngine.GameObject;
                            var h = hp != null ? hp.GetComponent<EntityController>() : null;
                            if (h == null) continue;
                            sb.AppendLine($"  host {host}: canAttack={h.canAttack} auto={h.shootAutomaticallyOnEnemiesWithinRange}/{h.rangeToChooseNewTargetAutomatically} whileMoving={h.canAttackWhileMoving} melee={h.melee} aiming={(h.aiming != null ? h.aiming.GetType().Name : "none")} skillAiming={(h.skillAiming != null)} range={EntityBalancingStore.WeaponRange(host):0.##}");
                        }
                        catch (Exception e) { sb.AppendLine("  host " + host + " FAILED " + e.Message); }
                    }
                }
                catch (Exception e) { sb.AppendLine(donor + "\tFAILED\t" + e.Message); }
                finally
                {
                    if (prefab != null) prefab.SetActive(was);
                    if (dormant != null) UnityEngine.Object.DestroyImmediate(dormant);
                }
            }
            File.WriteAllText(path, sb.ToString());
        }

        static void CollectOfType<T>(object o, System.Collections.Generic.List<T> into, System.Collections.Generic.HashSet<object> seen, int depth) where T : class
        {
            if (o == null || depth > 10) return;
            if (o is T hit) { if (seen.Add(hit)) into.Add(hit); return; }
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal) return;
            if (o is UnityEngine.Object && !(o is UnityEngine.ScriptableObject)) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) CollectOfType(item, into, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                CollectOfType(v, into, seen, depth + 1);
            }
        }

        // SpawnSides.Fix run for real on a dormant instance of what each entity is built from: a player copy
        // is its enemy prefab; a mixed host fires its DONOR's events (the mixer adds the donor clone's own
        // event objects to the host), so the donor prefab stands in with the host's id and side. Reports how
        // many spawns were switched and that the prefab asset itself was left untouched.
        static void WriteSideFixTest(string path, Func<string, string> donorOf)
        {
            var sb = new StringBuilder();
            var cases = new System.Collections.Generic.List<(string id, string prefabOf, string tag)>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (string.IsNullOrEmpty(row.entityId)) continue;
                if (PlayerCopies.IsCopy(row.entityId) && !SalvagedTech.IsGenerated(row.entityId)) cases.Add((row.entityId, row.entityId, "Player"));
                string donor = null;
                try { donor = donorOf?.Invoke(row.entityId); } catch { }
                if (!string.IsNullOrEmpty(donor)) cases.Add((row.entityId, donor, row.isAllowedForAi && !row.isAllowedAsBlueprint ? "AI" : "Player"));
            }
            SpawnSides.Reset();
            foreach (var (id, prefabOf, tag) in cases)
            {
                try
                {
                    var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(prefabOf)) as UnityEngine.GameObject;
                    if (prefab == null) continue;
                    string before = SideSignature(prefab.GetComponent<EntityController>());
                    bool was = prefab.activeSelf;
                    prefab.SetActive(false);
                    UnityEngine.GameObject dormant = null;
                    int fixedCount;
                    try
                    {
                        dormant = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                        var c = dormant.GetComponent<EntityController>();
                        c.entityId = id;
                        dormant.tag = tag;
                        fixedCount = SpawnSides.Fix(c);
                    }
                    finally
                    {
                        prefab.SetActive(was);
                        if (dormant != null) UnityEngine.Object.DestroyImmediate(dormant);
                    }
                    bool untouched = before == SideSignature(prefab.GetComponent<EntityController>());
                    if (fixedCount > 0 || !untouched) sb.AppendLine($"sidefix {id} ({tag}) events of {prefabOf}: switched {fixedCount}, asset untouched={untouched}");
                }
                catch (Exception e) { sb.AppendLine($"sidefix {id} FAILED {e.Message}"); }
            }
            SpawnSides.Reset();
            sb.AppendLine($"# {cases.Count} entities checked");
            File.WriteAllText(path, sb.ToString());
        }

        static string SideSignature(EntityController c)
        {
            var spawns = new System.Collections.Generic.List<SpawnObject>();
            if (c?.events != null) CollectSpawns(c.events, spawns, new System.Collections.Generic.HashSet<object>(), 0);
            return string.Join(",", spawns.ConvertAll(s => s.tagHandling + ":" + s.overwriteTag));
        }

        // Every SpawnObject in every prefab, and which side what it spawns ends up on. A spawn either takes
        // its owner's tag, a fixed string, or keeps the spawned prefab's own tag - so an enemy unit's
        // weapon handed to the player (salvage, a donor turret) can keep spawning things for the ENEMY.
        static void WriteSpawnSides(string path)
        {
            var sb = new StringBuilder("owner\tevent\tspawnKind\tspawned\ttagHandling\toverwriteTag\tspawnedPrefabTag\tspawnedHasController\tspawnedForAi\tinitController\n");
            var done = new System.Collections.Generic.HashSet<string>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (string.IsNullOrEmpty(row.prefabLocation) || !done.Add(row.entityId)) continue;
                try
                {
                    var prefab = UnityEngine.Resources.Load(row.prefabLocation) as UnityEngine.GameObject;
                    var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    if (c?.events == null) continue;
                    var found = new System.Collections.Generic.List<(string ev, SpawnObject s)>();
                    foreach (var ev in c.events)
                    {
                        if (ev == null) continue;
                        var spawns = new System.Collections.Generic.List<SpawnObject>();
                        CollectSpawns(ev, spawns, new System.Collections.Generic.HashSet<object>(), 0);
                        foreach (var s in spawns) found.Add((ev.@event.ToString(), s));
                    }
                    foreach (var (ev, s) in found)
                    {
                        string spawned = s.spawn == SpawnObject.Spawn.EntityId ? s.entityId
                                       : s.spawn == SpawnObject.Spawn.Prefab ? (s.prefab != null ? "prefab:" + s.prefab.name : "prefab:null")
                                       : s.spawn.ToString();
                        string tag = "-"; bool controller = false; string forAi = "-";
                        if (s.spawn == SpawnObject.Spawn.EntityId && !string.IsNullOrEmpty(s.entityId))
                        {
                            try
                            {
                                var sp = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(s.entityId)) as UnityEngine.GameObject;
                                if (sp != null) { tag = sp.tag; controller = sp.GetComponent<EntityController>() != null; }
                                forAi = EntityBalancingStore.IsAllowedForAi(s.entityId).ToString();
                            }
                            catch { }
                        }
                        else if (s.spawn == SpawnObject.Spawn.Prefab && s.prefab != null)
                        {
                            tag = s.prefab.tag; controller = s.prefab.GetComponent<EntityController>() != null;
                        }
                        sb.AppendLine($"{row.entityId}\t{ev}\t{s.spawn}\t{spawned}\t{s.tagHandling}\t{s.overwriteTag}\t{tag}\t{controller}\t{forAi}\t{s.initEntityController}");
                    }
                }
                catch (Exception e) { sb.AppendLine(row.entityId + "\tFAILED\t" + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
        }

        static void CollectSpawns(object o, System.Collections.Generic.List<SpawnObject> into, System.Collections.Generic.HashSet<object> seen, int depth)
        {
            if (o == null || depth > 10) return;
            if (o is SpawnObject s) { if (seen.Add(s)) into.Add(s); return; }
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal) return;
            if (o is UnityEngine.Object && !(o is UnityEngine.ScriptableObject)) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) CollectSpawns(item, into, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                CollectSpawns(v, into, seen, depth + 1);
            }
        }

        // A mixed unit's spawn instantiates its donor whole (16-24ms in battle logs) only to take its
        // turret. How much of that is the donor's own Awake/OnEnable - which an inactive instantiate
        // skips - and how much is copying the object? Timed per donor, warm, both ways.
        static void WriteSpawnCost(string path, Func<string, string> donorOf)
        {
            var donors = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                string d = null;
                try { d = donorOf?.Invoke(row.entityId); } catch { }
                if (!string.IsNullOrEmpty(d)) donors.Add(d);
            }
            var sb = new StringBuilder("donor\tactiveMs\tinactiveMs\trenderers\tparticles\tcomponents\n");
            double sumA = 0, sumI = 0;
            var watch = new System.Diagnostics.Stopwatch();
            foreach (string id in donors)
            {
                try
                {
                    var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(id)) as UnityEngine.GameObject;
                    if (prefab == null) continue;
                    double a = 0, i = 0;
                    for (int pass = 0; pass < 2; pass++) // first pass warms caches, second is measured
                    {
                        watch.Restart();
                        var live = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                        a = watch.Elapsed.TotalMilliseconds;
                        UnityEngine.Object.DestroyImmediate(live);
                        bool was = prefab.activeSelf;
                        prefab.SetActive(false);
                        try
                        {
                            watch.Restart();
                            var dormant = UnityEngine.Object.Instantiate(prefab, new UnityEngine.Vector3(0f, -10000f, 0f), UnityEngine.Quaternion.identity);
                            i = watch.Elapsed.TotalMilliseconds;
                            UnityEngine.Object.DestroyImmediate(dormant);
                        }
                        finally { prefab.SetActive(was); }
                    }
                    sumA += a; sumI += i;
                    sb.AppendLine($"{id}\t{a:0.00}\t{i:0.00}\t{prefab.GetComponentsInChildren<UnityEngine.Renderer>(true).Length}\t{prefab.GetComponentsInChildren<UnityEngine.ParticleSystem>(true).Length}\t{prefab.GetComponentsInChildren<UnityEngine.Component>(true).Length}");
                }
                catch (Exception e) { sb.AppendLine(id + "\tFAILED\t" + e.Message); }
            }
            sb.AppendLine($"TOTAL\t{sumA:0.0}\t{sumI:0.0}\t{donors.Count} donors");
            var mixer = HarmonyLib.AccessTools.TypeByName("RCM_UnitsMixNMatch.UnitMixer");
            sb.AppendLine("battle census can read the mixer's swap totals: "
                + (mixer != null && HarmonyLib.AccessTools.Field(mixer, "UnitSwapCount") != null && HarmonyLib.AccessTools.Field(mixer, "UnitSwapMs") != null));

            // what the mod itself spawns for the player must be a player-side id (see PlayerCopies)
            foreach (string roof in RoofTurrets.AvailableIds())
            {
                string used = RoofTurrets.PlayerSideId(roof);
                bool ai = false;
                try { ai = EntityBalancingStore.IsAllowedForAi(used); } catch { }
                sb.AppendLine($"modspawn roof gun {roof} -> {used} forAi={ai}");
            }

            // Every appended row that borrows another entity's prefab spawns with THAT entity's serialized
            // id unless something stamps ours on - an unstamped salvage foundry came out as the Tier 2 Tank
            // Factory it was copied from. Each active generated row, and whether a stamper covers it.
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (row.entityId == null || !row.entityId.StartsWith("rcmgen_", StringComparison.Ordinal)) continue; // locked ones too: Titans, captured tech
                try
                {
                    var prefab = UnityEngine.Resources.Load(row.prefabLocation ?? "") as UnityEngine.GameObject;
                    var c = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    if (c == null || c.entityId == row.entityId) continue;
                    bool stamped = PlayerCopies.IsCopy(row.entityId) || Titans.IsGenerated(row.entityId)
                                || EconomyBuildings.IsGenerated(row.entityId) || GeneratedDrops.IsGenerated(row.entityId);
                    // Stamping fixes the entity's own id, not ids its events NAME: a borrowed factory prefab
                    // whose OnStart spawns "its" unit by id would still hand out the original's unit.
                    var named = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
                    NamedIds(c.events, named, new System.Collections.Generic.HashSet<object>(), 0);
                    NamedIds(c.entityIdentifiers, named, new System.Collections.Generic.HashSet<object>(), 0);
                    string product = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : "-";
                    string prefabProduct = EntityBalancingStore.ParameterListIndexOf.ContainsKey(c.entityId) ? (EntityBalancingStore.ProductEntityId(c.entityId) ?? "-") : "-";
                    sb.AppendLine((stamped ? "stamped   " : "UNSTAMPED ") + row.entityId + " (prefab says " + c.entityId + ")"
                        + "\tproduct=" + product + "\tprefabProduct=" + prefabProduct + "\tnames=" + string.Join(",", named));
                }
                catch (Exception e) { sb.AppendLine("stamp check FAILED " + row.entityId + " " + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // every string an action or identifier holds in a field that names an entity (entityId, EntityId,
        // spawnEntityId, ...), nested entity mods included
        static void NamedIds(object o, System.Collections.Generic.ISet<string> ids, System.Collections.Generic.HashSet<object> seen, int depth)
        {
            if (o == null || depth > 10) return;
            var t = o.GetType();
            if (t.IsPrimitive || t.IsEnum || o is string || o is decimal) return;
            if (o is UnityEngine.Object && !(o is UnityEngine.ScriptableObject)) return;
            if (!t.IsValueType && !seen.Add(o)) return;
            if (o is System.Collections.IEnumerable list)
            {
                foreach (var item in list) NamedIds(item, ids, seen, depth + 1);
                return;
            }
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                object v;
                try { v = f.GetValue(o); } catch { continue; }
                if (v is string s)
                {
                    if (!string.IsNullOrEmpty(s) && f.Name.IndexOf("ntity", StringComparison.OrdinalIgnoreCase) >= 0
                        && EntityBalancingStore.ParameterListIndexOf.ContainsKey(s)) ids.Add(s);
                }
                else if (v is System.Collections.Generic.IEnumerable<string> strings && f.Name.IndexOf("ntity", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foreach (var x in strings) if (!string.IsNullOrEmpty(x) && EntityBalancingStore.ParameterListIndexOf.ContainsKey(x)) ids.Add(x);
                }
                else NamedIds(v, ids, seen, depth + 1);
            }
        }

        // Account (meta) upgrades carry card changes of their own and are loaded into the balancing
        // store per run, so the menu-time table can miss them. Every one, owned or not, with its changes.
        static void WriteMeta(string path)
        {
            var sb = new StringBuilder();
            try
            {
                foreach (var up in MetaProgressionUpgrade.MetaProgressionUpgrades())
                {
                    int owned = 0;
                    try { owned = MetaGame.Instance.MetaProgressionMultiplier(up.metaProgressionUpgradeId); } catch { }
                    sb.AppendLine($"{up.metaProgressionUpgradeId}\towned x{owned}\tdeactivated={up.isDeactivated}");
                    if (up.cardChanges == null) continue;
                    foreach (var c in up.cardChanges)
                        if (c != null)
                            sb.AppendLine($"    {c.valueToChange} {c.operation} {F(c.value)} side={c.side} oneOf={c.cardMustHaveOneOfTheseRoles} allOf={c.cardMustHaveAllOfTheseRoles} notOf={c.cardMustNotHaveOneOfTheseRoles} only={(c.onlyForTheseEntityIds == null ? "" : string.Join(",", c.onlyForTheseEntityIds))}");
                }
            }
            catch (Exception e) { sb.AppendLine("FAILED " + e.Message); }
            File.WriteAllText(path, sb.ToString());
        }

        // Rolled vanilla hacks and upgrades rewrite every number in their description by the roll's
        // factor. That is only honest where each number IS one of the scaled change values - so the
        // text before and after, the factor, and each change with its value before and after and the
        // identity of the change asset (shared assets are scaled once, by whichever card came first).
        static void WriteRolledTexts(string path)
        {
            var sb = new StringBuilder();
            void Card(string kind, string id, float factor, System.Collections.Generic.List<float> originals, string before, string after,
                      System.Collections.Generic.List<CardChangeScriptableObject> changes)
            {
                sb.AppendLine($"{kind}\t{id}\tfactor={F(factor)}");
                sb.AppendLine("  before: " + (before ?? "").Replace("\n", " "));
                sb.AppendLine("  after:  " + (after ?? "").Replace("\n", " "));
                for (int i = 0; changes != null && i < changes.Count; i++)
                {
                    var c = changes[i];
                    if (c == null) continue;
                    float o = originals != null && i < originals.Count ? originals[i] : float.NaN;
                    sb.AppendLine($"  change {c.valueToChange} {c.operation} {F(o)} -> {F(c.value)} asset#{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(c)}");
                }
            }
            foreach (var r in RelicRolls.Report())
                try { Card("hack", r.id, r.factor, r.originals, r.before, r.after, RelicBalancingStore.ScriptableObject(r.id)?.cardChanges); } catch (Exception e) { sb.AppendLine("hack " + r.id + " FAILED " + e.Message); }
            foreach (var u in UpgradeRolls.Report())
                try { Card("upgrade", u.id, u.factor, u.originals, u.before, u.after, UpgradeBalancingStore.ScriptableObject(u.id)?.cardChanges); } catch (Exception e) { sb.AppendLine("upgrade " + u.id + " FAILED " + e.Message); }
            File.WriteAllText(path, sb.ToString());
        }

        // A roll only needs the stat to be above zero in the row, and a row carries numbers its prefab
        // never reads - a mana pool on a unit with no skill and no mana-driven event. Which stats a
        // prefab reads is spread over dozens of enum fields (condition subjects, identifier radii,
        // value sources, calculation parameters), so every enum value reachable from its events and
        // identifiers is collected and the audit matches the rolled stat against that set.
        static void WriteStatUse(string path)
        {
            var sb = new StringBuilder("entity\thasActiveSkill\ttokens\tname\tlevel\tdamage1\theal1\tduration1\tduration2\tforAi\tblueprint\tfactory\tproduces\tupgradesCopyTo\troles\ttags\n");
            var done = new System.Collections.Generic.HashSet<string>();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (row.inactive || string.IsNullOrEmpty(row.prefabLocation) || !done.Add(row.entityId)) continue;
                try
                {
                    if (!StatUse.TryTokens(row.entityId, out bool activeSkill, out var found)) continue;
                    var tokens = new System.Collections.Generic.SortedSet<string>(found, StringComparer.Ordinal);
                    sb.Append(row.entityId).Append('\t').Append(activeSkill).Append('\t').Append(string.Join(",", tokens))
                      .Append('\t').Append(MixedUnitPresentation.BaseName(row.entityId)).Append('\t').Append(row.neededExperienceLevel)
                      .Append('\t').Append(F(row.damage1)).Append('\t').Append(F(row.healAmount1))
                      .Append('\t').Append(F(row.duration1)).Append('\t').Append(F(row.duration2))
                      .Append('\t').Append(row.isAllowedForAi).Append('\t').Append(row.isAllowedAsBlueprint)
                      .Append('\t').Append(EntityBalancingStore.FactoryEntityId(row.entityId) ?? "-")
                      .Append('\t').Append(row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : "-")
                      .Append('\t').Append(row.copyUpgradesToEntityId.hasValue ? row.copyUpgradesToEntityId.value : "-")
                      .Append('\t').Append(row.roles.ToString().Replace(", ", "|"))
                      .Append('\t').AppendLine(row.offeredSystemTags.ToString().Replace(", ", "|"));
                }
                catch (Exception e) { sb.AppendLine(row.entityId + "\tFAILED\t" + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // Every percentage every active hack and upgrade applies, vanilla and generated side by side:
        // "is +100 percent turret fire rate in band" is a question about what the game's own cards do,
        // and this is the answer to it. kind | id | generated | level | coins | stat | op | value | roles
        static void WriteCardEffects(string path)
        {
            var sb = new StringBuilder();
            sb.AppendLine("kind\tid\tgenerated\tlevel\tcoins\tstat\top\tvalue\troles\tside");
            try
            {
                foreach (var row in RelicBalancingStore._relicBalancingScriptableObject.parameters)
                {
                    if (row.inactive || row.isForSpecialists || row.scriptableObject == null || row.scriptableObject.cardChanges == null) continue;
                    foreach (var c in row.scriptableObject.cardChanges)
                        if (c != null)
                            sb.AppendLine($"hack\t{row.relicId}\t{GeneratedHacks.IsGenerated(row.relicId)}\t{row.neededExperienceLevel}\t{row.coinsAmount}\t{c.valueToChange}\t{c.operation}\t{F(c.value)}\t{c.cardMustHaveOneOfTheseRoles}\t{c.side}");
                }
            }
            catch (Exception e) { sb.AppendLine("hacks FAILED " + e.Message); }
            try
            {
                foreach (var row in UpgradeBalancingStore._upgradeBalancingScriptableObject.parameters)
                {
                    if (row.inactive || row.isForSpecialists || row.scriptableObject == null || row.scriptableObject.cardChanges == null) continue;
                    foreach (var c in row.scriptableObject.cardChanges)
                        if (c != null)
                            sb.AppendLine($"upgrade\t{row.upgradeId}\t{GeneratedUpgrades.IsGenerated(row.upgradeId)}\t{row.neededExperienceLevel}\t{row.coinsAmount}\t{c.valueToChange}\t{c.operation}\t{F(c.value)}\t{c.cardMustHaveOneOfTheseRoles}\t{c.side}");
                }
            }
            catch (Exception e) { sb.AppendLine("upgrades FAILED " + e.Message); }
            File.WriteAllText(path, sb.ToString());
        }

        // What the economy buildings actually pay: the vanilla ones carry their income inside a
        // GainCredits action, not in a stat column, so a comparison needs the action's own numbers.
        static void WriteIncome(string path)
        {
            var sb = new StringBuilder();
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                if (!row.isAllowedAsBlueprint || row.inactive || ((row.roles & UnitRole.Refinery) == 0 && !EconomyBuildings.IsGenerated(row.entityId))) continue;
                try
                {
                    var prefab = UnityEngine.Resources.Load(row.prefabLocation ?? "") as UnityEngine.GameObject;
                    var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    sb.AppendLine($"{row.entityId}\tcost={row.cost}\tgainCreditsAmount={F(row.gainCreditsAmount)}\tmaxArmor={F(row.maxArmor)}");
                    if (controller?.events != null) IncomeEvents(sb, controller.events, "    ", 0);
                }
                catch (Exception e) { sb.AppendLine("    FAILED " + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // A refund (the Reclaimer's) is a mod the building puts on OTHER units, whose OnWillBeDestroyed
        // pays - so the mods an event adds are followed too, one level of nesting per mod.
        static void IncomeEvents(StringBuilder sb, System.Collections.Generic.IEnumerable<EntityEvent> events, string indent, int depth)
        {
            foreach (var ev in events)
            {
                if (ev == null) continue;
                var all = new System.Collections.Generic.List<IEntityAction>(ev.actions ?? new System.Collections.Generic.List<IEntityAction>());
                if (ev.conditionalActions != null) foreach (var ca in ev.conditionalActions) if (ca?.actions != null) all.AddRange(ca.actions);
                foreach (var a in all)
                {
                    if (a is GainCredits g)
                        sb.AppendLine($"{indent}{ev.@event}: GainCredits amount={g.creditAmount} x{F(g.multiplier)} receiver={g.creditReceiver} on={g.operatingEntities} ident={g.entityIdentifierWithTargetAsOrigin}");
                    else if (a is AddEntityMod m && m.entityMod != null && depth < 3)
                    {
                        sb.AppendLine($"{indent}{ev.@event}: AddEntityMod {m.entityMod.name} on={m.operatingEntities} ident={m.entityIdentifierWithTargetAsOrigin}");
                        if (m.entityMod.events != null) IncomeEvents(sb, m.entityMod.events, indent + "    ", depth + 1);
                    }
                }
            }
        }

        // A donor's weapon travels as its five firing events, and the weapon audit assumed that any
        // condition inside them passes. A condition on the FIRING UNIT's own state - a behaviour status,
        // a flag, a stored value - is set by the donor's other events (OnStart, OnEachSecond), which do
        // not travel. On a host that never sets it, the gated action never runs. Written for every donor
        // actually in the map, with the fields that decide whether a host can ever satisfy it.
        static readonly EntityController.Event[] Copied =
        {
            EntityController.Event.OnReadyToShoot, EntityController.Event.OnHasShot, EntityController.Event.OnAttackHitTarget,
            EntityController.Event.OnAttackMissedTarget, EntityController.Event.OnAttackWarmUpStarted,
        };

        static void WriteDonorConditions(string path, Func<string, string> donorOf)
        {
            var donors = new System.Collections.Generic.SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in EntityBalancingStore.EntityBalancingParametersList)
            {
                string unit = row.factoryForEntityId.hasValue ? row.factoryForEntityId.value : row.entityId;
                string donor = donorOf != null ? donorOf(unit) : null;
                if (!string.IsNullOrEmpty(donor)) donors.Add(donor);
            }
            var sb = new StringBuilder();
            foreach (string donor in donors)
            {
                try
                {
                    var prefab = UnityEngine.Resources.Load(EntityBalancingStore.PrefabLocation(donor)) as UnityEngine.GameObject;
                    var controller = prefab != null ? prefab.GetComponent<EntityController>() : null;
                    if (controller == null || controller.events == null) continue;
                    foreach (var ev in controller.events)
                    {
                        if (ev == null || Array.IndexOf(Copied, ev.@event) < 0 || ev.conditionalActions == null) continue;
                        foreach (var conditional in ev.conditionalActions)
                        {
                            if (conditional?.eventConditions == null) continue;
                            string actions = string.Join(",", conditional.actions.Select(a => a?.GetType().Name ?? "null"));
                            foreach (var c in conditional.eventConditions)
                                sb.AppendLine($"{donor}\t{ev.@event}\t{c.type}\tentity={c.entity}\tstatus={c.behaviourStatus}\top={c.roleOperation}"
                                    + $"\tbool={c.entityBoolSubject}\tint={c.intSubject}\tfloat={c.entityFloatSubject}\t-> {actions}");
                        }
                    }
                }
                catch (Exception e) { sb.AppendLine(donor + "\tFAILED\t" + e.Message); }
            }
            File.WriteAllText(path, sb.ToString());
        }

        // Every card change aimed at a unit by id, with where it came from. An effective number that
        // looks wrong is the product of several changes - a roll, the weapon repricing, a hack - and
        // the product alone cannot say which one is wrong.
        static void WriteChanges(string path)
        {
            var byUnit = new System.Collections.Generic.SortedDictionary<string, System.Collections.Generic.List<string>>(StringComparer.Ordinal);
            foreach (var entry in EntityBalancingStore.InGameCardChanges)
            {
                string source = EntityBalancingStore.SourceOfInGameCardChangesFromUniqueEntityId.TryGetValue(entry.Key, out var id)
                    ? id.cardType + ":" + id.id : "?";
                foreach (var change in entry.Value)
                {
                    if (change == null || change.onlyForTheseEntityIds == null) continue;
                    foreach (string target in change.onlyForTheseEntityIds)
                    {
                        if (!byUnit.TryGetValue(target, out var lines)) byUnit[target] = lines = new System.Collections.Generic.List<string>();
                        lines.Add($"{change.valueToChange} {change.operation} {F(change.value)}  [{source} #{entry.Key}]");
                    }
                }
            }
            var sb = new StringBuilder();
            foreach (var unit in byUnit)
            {
                sb.AppendLine(unit.Key);
                foreach (var line in unit.Value) sb.AppendLine("    " + line);
            }
            File.WriteAllText(path, sb.ToString());
        }

        static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
