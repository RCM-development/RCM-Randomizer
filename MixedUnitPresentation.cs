using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TestMod;
using UnityEngine;
using UnityEngine.Rendering;

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

        // Loca.Translate lowercases every id before looking it up (Loca.cs:289), so entries must
        // be written under the lowercased entityId or they are simply never found.
        static string LocaKey(string entityId) => entityId.Trim().ToLowerInvariant();

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
                    string baseKey = LocaKey(pair.Key);
                    if (!originals.TryGetValue(baseKey, out string baseName)) continue;
                    if (!originals.TryGetValue(LocaKey(pair.Value), out string donorName)) continue;
                    saved[baseKey] = baseName;
                    dict[baseKey] = baseName + " + " + donorName;
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

        public static void ResetPortraits()
        {
            // dropping the cache entry makes the next EntityImage() reload the stock sprite
            foreach (string entityId in PortraitDone) EntityBalancingStore.ImageOf.Remove(entityId);
            PortraitDone.Clear();
            PendingCaptures.Clear();
        }

        // Capture requests queue up and a single runner works through them, one capture per
        // interval. A production building prespawns a batch of unit types at once; giving every
        // one of them a same-frame GPU readback (ReadPixels stalls the pipeline) was a visible
        // hitch. Spread out, each readback hides in its own frame.
        static readonly Queue<KeyValuePair<string, EntityController>> PendingCaptures = new Queue<KeyValuePair<string, EntityController>>();

        public static void RequestPortrait(EntityController entity)
        {
            string entityId = entity.EntityId;
            if (PortraitDone.Add(entityId))
                PendingCaptures.Enqueue(new KeyValuePair<string, EntityController>(entityId, entity));
        }

        // Rather than photographing the unit in place, a visuals-only copy of it is built far
        // below the map and photographed there. Moving the real unit onto a spare layer did not
        // work: URP's renderer has its own opaque/transparent layer masks, so objects on an
        // unused layer are drawn by no camera at all and every capture came out empty. Down in
        // the booth nothing else is in frame, so the camera keeps normal layers.
        // The camera also has to stay ENABLED for a frame; a manual Camera.Render() draws
        // nothing under URP.
        public static IEnumerator ProcessCaptureQueue()
        {
            var pause = new WaitForSeconds(0.2f);
            while (true)
            {
                if (PendingCaptures.Count == 0) { yield return pause; continue; }
                var request = PendingCaptures.Dequeue();

                if (request.Value == null || !request.Value.gameObject.activeInHierarchy)
                {
                    PortraitDone.Remove(request.Key); // retry when the type spawns again
                    continue;
                }

                GameObject booth = BuildBooth(request.Value.gameObject, out RenderTexture rt);
                if (booth == null) { PortraitDone.Remove(request.Key); continue; }

                yield return new WaitForEndOfFrame(); // URP renders the enabled camera this frame

                // async GPU readback where supported: ReadPixels stalls the whole pipeline until
                // the GPU catches up, which showed up as a hitch whenever a factory prespawned
                // new unit types. The async request costs nothing on the main thread.
                if (SystemInfo.supportsAsyncGPUReadback)
                {
                    var readback = AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32);
                    while (!readback.done) yield return null;
                    try
                    {
                        if (!readback.hasError) CaptureFromReadback(request.Key, readback);
                        else CaptureFromBooth(request.Key, rt); // fall back to the sync path
                    }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(booth);
                    }
                }
                else
                {
                    try { CaptureFromBooth(request.Key, rt); }
                    finally
                    {
                        RenderTexture.ReleaseTemporary(rt);
                        UnityEngine.Object.Destroy(booth);
                    }
                }
                yield return pause;
            }
        }

        static void CaptureFromReadback(string entityId, AsyncGPUReadbackRequest readback)
        {
            var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.RGBA32, mipChain: false);
            texture.LoadRawTextureData(readback.GetData<byte>());
            texture.Apply();
            StorePortrait(entityId, texture);
        }

        static void StorePortrait(string entityId, Texture2D texture)
        {
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

        static void CaptureFromBooth(string entityId, RenderTexture rt)
        {
            var previousActive = RenderTexture.active;
            try
            {
                RenderTexture.active = rt;
                var texture = new Texture2D(PortraitSize, PortraitSize, TextureFormat.ARGB32, mipChain: false);
                texture.ReadPixels(new Rect(0, 0, PortraitSize, PortraitSize), 0, 0);
                texture.Apply();
                StorePortrait(entityId, texture);
            }
            finally
            {
                RenderTexture.active = previousActive;
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
                if (renderers.Length == 0) throw new Exception("no visible renderers on the copy");

                Bounds bounds = FramingBounds(renderers);

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

        // Beam and effect meshes are stretched towards their target and report enormous world
        // bounds; framing on those zooms the camera so far out that the unit is a few pixels.
        // Drop the outliers and frame on what is left.
        static Bounds FramingBounds(Renderer[] renderers)
        {
            var sizes = renderers.Select(r => r.bounds.size.magnitude).OrderBy(v => v).ToList();
            float limit = Mathf.Max(0.001f, sizes[sizes.Count / 2] * 4f);

            bool any = false;
            Bounds total = default;
            foreach (var r in renderers)
            {
                if (r.bounds.size.magnitude > limit) continue;
                if (!any) { total = r.bounds; any = true; }
                else total.Encapsulate(r.bounds);
            }
            return any ? total : renderers[0].bounds;
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
