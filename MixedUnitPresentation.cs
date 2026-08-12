using System;
using System.Collections;
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

        public static bool NeedsPortrait(string entityId) => !PortraitDone.Contains(entityId);

        public static void ResetPortraits()
        {
            // dropping the cache entry makes the next EntityImage() reload the stock sprite
            foreach (string entityId in PortraitDone) EntityBalancingStore.ImageOf.Remove(entityId);
            PortraitDone.Clear();
        }

        // Started from the EntityController.Init postfix, once per entity type. The game uses URP,
        // where a manual Camera.Render() draws nothing, so instead the portrait camera stays
        // ENABLED for one frame and the pipeline renders it into the target texture; the unit's
        // renderers sit on a spare layer for that frame so the camera sees only them.
        public static IEnumerator CapturePortrait(EntityController entity)
        {
            string entityId = entity.EntityId;
            if (!PortraitDone.Add(entityId)) yield break;

            yield return null; // let spawn setup settle
            if (entity == null || !entity.gameObject.activeInHierarchy) { PortraitDone.Remove(entityId); yield break; }

            var renderers = entity.gameObject.GetComponentsInChildren<Renderer>()
                .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && r.enabled)
                .ToArray();
            if (renderers.Length == 0) { PortraitDone.Remove(entityId); yield break; }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

            var savedLayers = new Dictionary<GameObject, int>();
            foreach (var r in renderers)
            {
                if (r == null) continue;
                savedLayers[r.gameObject] = r.gameObject.layer;
                r.gameObject.layer = PortraitLayer;
            }

            var camObj = new GameObject("RCM_PortraitCam");
            var cam = camObj.AddComponent<Camera>();
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

            var rt = RenderTexture.GetTemporary(PortraitSize, PortraitSize, 24, RenderTextureFormat.ARGB32);
            cam.targetTexture = rt;

            yield return new WaitForEndOfFrame(); // URP renders the enabled camera this frame

            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.ARGB32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0);
                texture.Apply();

                if (LooksEmpty(texture))
                {
                    // keep the stock sprite rather than caching a black square
                    UnityEngine.Object.Destroy(texture);
                    RCMManager.Log("Randomizer: portrait of " + entityId + " came out empty, keeping stock image");
                }
                else
                {
                    EntityBalancingStore.ImageOf[entityId] = Sprite.Create(
                        texture, new Rect(0, 0, PortraitSize, PortraitSize), new Vector2(0.5f, 0.5f), 100f);
                    Game.UpdateAllCachedCards();
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                cam.targetTexture = null;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(camObj);
                foreach (var entry in savedLayers)
                    if (entry.Key != null) entry.Key.layer = entry.Value;
            }
        }

        static bool LooksEmpty(Texture2D texture)
        {
            int step = PortraitSize / 8;
            for (int x = step; x < PortraitSize; x += step)
                for (int y = step; y < PortraitSize; y += step)
                    if (texture.GetPixel(x, y).a > 0.05f) return false;
            return true;
        }
    }
}
