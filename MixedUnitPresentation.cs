using System;
using System.Collections.Generic;
using System.Linq;
using TestMod;
using UnityEngine;

namespace RCM_Randomizer
{
    // Makes mixed units look like what they are: renames them "Base x Donor" and replaces their
    // portrait with a snapshot of the first actually-spawned (turret-swapped, scaled) instance.
    // Both ride game-owned caches, so every UI surface follows: names resolve through
    // Loca.BlueprintName, images through EntityBalancingStore.EntityImage's ImageOf cache.
    public static class MixedUnitPresentation
    {
        const int PortraitLayer = 31; // borrowed for a single synchronous off-screen render
        const int PortraitSize = 256;

        // originals of the loca entries we overwrote, per language, for clean restore
        static readonly Dictionary<string, Dictionary<string, string>> SavedNames = new Dictionary<string, Dictionary<string, string>>();
        static readonly HashSet<string> PortraitDone = new HashSet<string>();

        // ---- Names -----------------------------------------------------------------------------

        public static void ApplyMixedNames(Dictionary<string, string> donorMap)
        {
            if (Loca.BlueprintNameDictionary.Count < 1) Loca.Init();
            RestoreNames();
            foreach (var language in Loca.BlueprintNameDictionary)
            {
                var dict = language.Value;
                var originals = new Dictionary<string, string>(dict);
                var saved = new Dictionary<string, string>();
                foreach (var pair in donorMap)
                {
                    if (!originals.TryGetValue(pair.Key, out string baseName)) continue;
                    if (!originals.TryGetValue(pair.Value, out string donorName)) continue;
                    saved[pair.Key] = baseName;
                    dict[pair.Key] = baseName + " + " + donorName;
                }
                SavedNames[language.Key] = saved;
            }
        }

        public static void RestoreNames()
        {
            foreach (var language in SavedNames)
            {
                if (!Loca.BlueprintNameDictionary.TryGetValue(language.Key, out var dict)) continue;
                foreach (var entry in language.Value) dict[entry.Key] = entry.Value;
            }
            SavedNames.Clear();
        }

        // ---- Portraits -------------------------------------------------------------------------

        // Called once per entity type from the EntityController.Init postfix, after the mixer has
        // transplanted and scaled the turret, so the snapshot shows the real combination.
        public static void TryCapturePortrait(EntityController entity)
        {
            string entityId = entity.EntityId;
            if (PortraitDone.Contains(entityId)) return;
            PortraitDone.Add(entityId);
            try
            {
                Sprite portrait = RenderPortrait(entity.gameObject);
                EntityBalancingStore.ImageOf[entityId] = portrait;
                Game.UpdateAllCachedCards();
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer: portrait of " + entityId + " failed (" + e.Message + ")");
            }
        }

        public static void ResetPortraits()
        {
            // dropping the cache entry makes the next EntityImage() reload the stock sprite
            foreach (string entityId in PortraitDone) EntityBalancingStore.ImageOf.Remove(entityId);
            PortraitDone.Clear();
        }

        // Synchronous off-screen render of just this unit: its renderers move to a spare layer
        // for the duration of one manual Camera.Render() (between visible frames, so nothing
        // flickers), then everything is restored.
        static Sprite RenderPortrait(GameObject unit)
        {
            var renderers = unit.GetComponentsInChildren<Renderer>()
                .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && r.enabled)
                .ToArray();
            if (renderers.Length == 0) throw new Exception("no renderers");

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            var savedLayers = new Dictionary<GameObject, int>();
            foreach (var r in renderers)
            {
                savedLayers[r.gameObject] = r.gameObject.layer;
                r.gameObject.layer = PortraitLayer;
            }

            var camObj = new GameObject("RCM_PortraitCam");
            RenderTexture rt = null;
            var previousActive = RenderTexture.active;
            try
            {
                var cam = camObj.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.cullingMask = 1 << PortraitLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);

                float radius = Mathf.Max(0.5f, bounds.extents.magnitude);
                cam.orthographicSize = radius * 1.05f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = radius * 8f;
                Vector3 viewDir = Quaternion.Euler(30f, 45f, 0f) * Vector3.forward; // 3/4 view like the stock portraits
                cam.transform.position = bounds.center - viewDir * radius * 4f;
                cam.transform.rotation = Quaternion.LookRotation(viewDir);

                rt = RenderTexture.GetTemporary(PortraitSize, PortraitSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();
                cam.targetTexture = null;

                RenderTexture.active = rt;
                var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.ARGB32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0);
                texture.Apply();

                return Sprite.Create(texture, new Rect(0, 0, PortraitSize, PortraitSize), new Vector2(0.5f, 0.5f), 100f);
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(camObj);
                foreach (var entry in savedLayers)
                    if (entry.Key != null) entry.Key.layer = entry.Value;
            }
        }
    }
}
