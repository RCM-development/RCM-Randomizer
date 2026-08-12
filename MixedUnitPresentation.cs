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
        const int PortraitSize = 256;
        // far below the battlefield, where the photo booth can't catch scenery
        static readonly Vector3 BoothPosition = new Vector3(0f, -5000f, 0f);

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

        // Started from the EntityController.Init postfix, once per entity type.
        //
        // Rather than photographing the unit in place, a visuals-only copy of it is built far
        // below the map and photographed there. Moving the real unit onto a spare layer did not
        // work: URP's renderer has its own opaque/transparent layer masks, so objects on an
        // unused layer are drawn by no camera at all and every capture came out empty. Down in
        // the booth nothing else is in frame, so the camera keeps normal layers.
        // The camera also has to stay ENABLED for a frame; a manual Camera.Render() draws
        // nothing under URP.
        public static IEnumerator CapturePortrait(EntityController entity)
        {
            string entityId = entity.EntityId;
            if (!PortraitDone.Add(entityId)) yield break;

            yield return null; // let spawn setup settle
            if (entity == null || !entity.gameObject.activeInHierarchy) { PortraitDone.Remove(entityId); yield break; }

            GameObject booth = BuildBooth(entity.gameObject, out RenderTexture rt);
            if (booth == null) { PortraitDone.Remove(entityId); yield break; }

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
                    // keep the stock sprite rather than caching a blank square
                    UnityEngine.Object.Destroy(texture);
                    RCMManager.Log("Randomizer: portrait of " + entityId + " came out empty, keeping stock image");
                }
                else
                {
                    EntityBalancingStore.ImageOf[entityId] = Sprite.Create(
                        texture, new Rect(0, 0, PortraitSize, PortraitSize), new Vector2(0.5f, 0.5f), 100f);
                    Game.UpdateAllCachedCards();
                    RCMManager.Log("Randomizer: captured portrait of " + entityId);
                }
            }
            finally
            {
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.Destroy(booth);
            }
        }

        // Builds the booth: an inactive root (so the copy's components never Awake), a stripped
        // copy of the unit, and a camera framing it. Everything hangs off the returned root.
        static GameObject BuildBooth(GameObject unit, out RenderTexture rt)
        {
            rt = null;
            GameObject booth = null;
            try
            {
                booth = new GameObject("RCM_PortraitBooth");
                booth.SetActive(false);
                booth.transform.position = BoothPosition;

                GameObject model = UnityEngine.Object.Instantiate(unit, booth.transform);
                StripToVisuals(model);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;

                booth.SetActive(true);

                var renderers = model.GetComponentsInChildren<Renderer>()
                    .Where(r => (r is MeshRenderer || r is SkinnedMeshRenderer) && r.enabled)
                    .ToArray();
                if (renderers.Length == 0) throw new Exception("no renderers");

                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                var camObj = new GameObject("RCM_PortraitCam");
                camObj.transform.SetParent(booth.transform);
                var cam = camObj.AddComponent<Camera>();
                cam.orthographic = true;
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
                return booth;
            }
            catch (Exception e)
            {
                RCMManager.Log("Randomizer: portrait booth failed (" + e.Message + ")");
                if (rt != null) { RenderTexture.ReleaseTemporary(rt); rt = null; }
                if (booth != null) UnityEngine.Object.Destroy(booth);
                return null;
            }
        }

        // Leave only meshes behind: no scripts that would register the copy with the game, no
        // particles, trails, audio or health bars in the shot.
        static void StripToVisuals(GameObject model)
        {
            foreach (var component in model.GetComponentsInChildren<Component>(true))
            {
                if (component == null) continue;
                if (component is Transform || component is MeshFilter
                    || component is MeshRenderer || component is SkinnedMeshRenderer) continue;
                try { UnityEngine.Object.DestroyImmediate(component); } catch { }
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
