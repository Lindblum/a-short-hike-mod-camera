using System;
using System.Collections;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ShortHikeCamera
{
    public enum StereoMode { Off, HalfSBS }

    /// Half side-by-side stereo.
    ///
    /// The game renders the world with its main camera into a low-res RenderTexture shown on a
    /// quad (renderQuad), and the UI with a separate camera into a full-res texture shown on a
    /// second quad (uiRenderQuad). In SBS mode:
    ///  - the main camera renders the LEFT eye into the game's own texture as normal;
    ///  - we render the RIGHT eye with the same camera into our own texture of the same size
    ///    (Application.onBeforeRender), so image effects like edge outlines see an ordinary
    ///    full-frame render for each eye;
    ///  - the world quad is squeezed into the left half and a copy showing the right eye into the
    ///    right half (= half SBS);
    ///  - the UI quad is shown twice, squeezed into each half (UI sits at screen depth).
    internal static class Stereo
    {
        internal static ConfigEntry<StereoMode> mode;
        internal static ConfigEntry<float> eyeSeparation, convergence;
        internal static ConfigEntry<bool> swapEyes;

        internal static bool On => mode != null && mode.Value == StereoMode.HalfSBS;

        private static PixelFilterAdjuster pfa;
        private static bool applied;
        private static StereoMode lastMode;

        private static RenderTexture rightEye;
        private static Material rightEyeMat;
        internal static readonly QuadSplit worldSplit = new QuadSplit("world");
        private static readonly QuadSplit uiSplit = new QuadSplit("ui");

        internal static void Init(ConfigFile config)
        {
            mode = config.Bind("Stereo3D", "Mode", StereoMode.Off, "Off or HalfSBS (half side-by-side). Also toggleable in Options > Graphics.");
            eyeSeparation = config.Bind("Stereo3D", "EyeSeparation", 0.1f, "Distance between the two eye cameras, in world units. Larger = stronger 3D.");
            convergence = config.Bind("Stereo3D", "Convergence", 10f, "Distance that appears at screen depth. Nearer things pop out, farther things sink in.");
            swapEyes = config.Bind("Stereo3D", "SwapEyes", false, "Swap left/right images (if depth looks inside-out).");
            flatFlip = config.Bind("Stereo3D", "FlipMenus", false, "Set true if the title screen / menus appear upside down in 3D mode.");
            lastMode = mode.Value;
            Application.onBeforeRender += OnBeforeRender;
        }

        internal static void SetMode(StereoMode m)
        {
            mode.Value = m; // saves the config
            Plugin.Instance?.MarkConfigSavedByUs();
            CheckModeChanged();
        }

        /// Called every frame from the plugin's update loop.
        internal static void Tick()
        {
            // Find the render setup exactly the way the game does (same instance its options code
            // reconfigures). Throttled, except right away when the mode changes.
            pfaCheckTimer -= Time.unscaledDeltaTime;
            bool modeChanged = mode.Value != lastMode;
            if (pfaCheckTimer > 0f && !modeChanged) { CheckModeChanged(); return; }
            pfaCheckTimer = 0.5f;

            PixelFilterAdjuster current = LocatePfa();
            if (current != pfa)
            {
                if (applied) Unapply();
                pfa = current;
                if (pfa != null && On)
                {
                    Plugin.Log.LogInfo("Stereo: new render setup found, reconfiguring.");
                    Reconfigure();
                }
            }
            CheckModeChanged();
        }

        private static float pfaCheckTimer;
        private static ServiceLocator locator;

        private static PixelFilterAdjuster LocatePfa()
        {
            try
            {
                if (locator == null) locator = UnityEngine.Object.FindObjectOfType<ServiceLocator>();
                return locator != null ? locator.Locate<PixelFilterAdjuster>(allowFail: true) : null;
            }
            catch { return null; }
        }

        private static void CheckModeChanged()
        {
            if (mode.Value == lastMode) return;
            lastMode = mode.Value;
            Plugin.Log.LogInfo("Stereo mode -> " + mode.Value);
            if (!On) Unapply();
            Reconfigure();
        }

        /// Re-run the game's render-texture setup so our SetPixelSize patch can steer it off the
        /// "render straight into the full-res UI texture" path (and back).
        private static void Reconfigure()
        {
            try { PixelFilterAdjuster.pixelWidth = PixelFilterAdjuster.pixelWidth; }
            catch (Exception e) { Plugin.Log.LogWarning("Stereo: reconfigure failed: " + e.Message); }
        }

        private static bool loggedDirectPath;

        private static void OnBeforeRender()
        {
            try
            {
                if (!On || pfa == null) { if (applied) Unapply(); return; }
                Camera cam = pfa.mainCamera;
                if (cam == null || !cam.isActiveAndEnabled) return;

                // Left eye texture = the game's own world texture. Skip the direct-to-UI-texture path
                // (it's avoided by our SetPixelSize patch once the setup re-runs).
                RenderTexture left = cam.targetTexture;
                if (left == null || left == pfa.originalRenderTexture)
                {
                    if (!loggedDirectPath) { loggedDirectPath = true; Plugin.Log.LogInfo("Stereo: waiting for render setup (direct path), reconfiguring."); Reconfigure(); }
                    return;
                }
                loggedDirectPath = false;

                EnsureRightEyeTexture(left);
                applied = true;

                float e = eyeSeparation.Value * 0.5f * (swapEyes.Value ? -1f : 1f);
                float c = Mathf.Max(0.1f, convergence.Value);

                cam.ResetWorldToCameraMatrix();
                Matrix4x4 view = cam.worldToCameraMatrix;

                // Right eye (camera at +e): render now into our texture.
                SetEye(cam, view, +e, c);
                cam.targetTexture = rightEye;
                cam.Render();
                cam.targetTexture = left;

                // Left eye (camera at -e): the game's normal render draws this.
                SetEye(cam, view, -e, c);

                // Show them side by side.
                if (pfa.renderQuad != null)
                {
                    rightEyeMat.CopyPropertiesFromMaterial(pfa.renderQuad.sharedMaterial);
                    rightEyeMat.mainTexture = rightEye;
                    worldSplit.Apply(pfa.renderQuad, rightEyeMat);
                }
                if (pfa.uiRenderQuad != null)
                    uiSplit.Apply(pfa.uiRenderQuad, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("Stereo render failed, turning 3D off: " + ex);
                mode.Value = StereoMode.Off;
            }
        }

        private static void EnsureRightEyeTexture(RenderTexture like)
        {
            if (rightEye == null || rightEye.width != like.width || rightEye.height != like.height || rightEye.format != like.format)
            {
                if (rightEye != null) rightEye.Release();
                rightEye = new RenderTexture(like.width, like.height, 24, like.format);
                rightEye.name = "ShortHikeCamera_RightEye";
                rightEye.filterMode = like.filterMode;
                rightEye.Create();
            }
            rightEye.filterMode = like.filterMode;
            if (rightEyeMat == null && pfa.renderQuad != null)
                rightEyeMat = new Material(pfa.renderQuad.sharedMaterial) { name = "ShortHikeCamera_RightEyeMat" };
        }

        private static void SetEye(Camera cam, Matrix4x4 view, float eyeOffset, float conv)
        {
            cam.ResetProjectionMatrix();
            // Move the eye sideways in view space.
            cam.worldToCameraMatrix = Matrix4x4.Translate(new Vector3(-eyeOffset, 0f, 0f)) * view;
            if (!cam.orthographic)
            {
                // Off-axis shift so points at the convergence distance have zero parallax.
                Matrix4x4 p = cam.projectionMatrix;
                p.m02 = -p.m00 * eyeOffset / conv;
                cam.projectionMatrix = p;
            }
        }

        private static void Unapply()
        {
            applied = false;
            if (pfa != null && pfa.mainCamera != null)
            {
                Camera cam = pfa.mainCamera;
                cam.ResetWorldToCameraMatrix();
                cam.ResetProjectionMatrix();
            }
            worldSplit.Restore();
            uiSplit.Restore();
            if (rightEye != null) { rightEye.Release(); rightEye = null; }
        }

        /// True while the in-game (true depth) stereo path is driving the picture.
        internal static bool WorldStereoActive => applied;

        // ---------- Fallback: flat SBS for screens without the game's render setup ----------
        // Opening logos, title screen / main menu, credits... draw straight to the screen, so at the
        // end of the frame we copy the finished image and draw it squeezed into both halves.

        private static RenderTexture capture;
        private static Material copyMat;
        private static string loggedSceneSetup;
        internal static ConfigEntry<bool> flatFlip;

        internal static IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                try { OnEndOfFrame(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("Stereo (menus) failed, turning 3D off: " + e);
                    mode.Value = StereoMode.Off;
                }
            }
        }

        private static void OnEndOfFrame()
        {
            if (!On || applied)
            {
                if (!On && capture != null) { capture.Release(); capture = null; }
                return;
            }

            int w = Screen.width, h = Screen.height;
            if (w <= 0 || h <= 0) return;
            LogSceneSetupOnce();

            if (capture == null || capture.width != w || capture.height != h)
            {
                if (capture != null) capture.Release();
                capture = new RenderTexture(w, h, 0) { name = "ShortHikeCamera_MenuCapture", filterMode = FilterMode.Bilinear };
                capture.Create();
            }
            if (copyMat == null)
            {
                Shader s = Shader.Find("Hidden/BlitCopy");
                if (s != null) copyMat = new Material(s) { name = "ShortHikeCamera_MenuCopy" };
            }

            ScreenCapture.CaptureScreenshotIntoRenderTexture(capture);

            // Captures come out upside down where UVs start at the top (D3D); SBS config can override.
            bool flip = SystemInfo.graphicsUVStartsAtTop != flatFlip.Value;
            Rect src = flip ? new Rect(0f, 1f, 1f, -1f) : new Rect(0f, 0f, 1f, 1f);

            RenderTexture.active = null;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0f, w, h, 0f);
            if (copyMat == null) GL.Clear(false, true, Color.black);
            Graphics.DrawTexture(new Rect(0f, 0f, w * 0.5f, h), capture, src, 0, 0, 0, 0, copyMat);
            Graphics.DrawTexture(new Rect(w * 0.5f, 0f, w * 0.5f, h), capture, src, 0, 0, 0, 0, copyMat);
            GL.PopMatrix();
        }

        /// Log the camera setup of screens using the fallback, to help add true depth to them later.
        private static void LogSceneSetupOnce()
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene == loggedSceneSetup) return;
            loggedSceneSetup = scene;
            Plugin.Log.LogInfo("Stereo: flat SBS fallback on scene '" + scene + "' (render setup found: " + (pfa != null) + ")");
            foreach (Camera c in Camera.allCameras)
                Plugin.Log.LogInfo("  camera " + c.name + " depth=" + c.depth + " ortho=" + c.orthographic
                    + " target=" + (c.targetTexture != null ? c.targetTexture.name + " " + c.targetTexture.width + "x" + c.targetTexture.height : "screen")
                    + " rect=" + c.rect + " mask=" + c.cullingMask);
        }

        // ---------- Options menu text (lowercase, like the game's own options) ----------

        internal static string MenuText()
        {
            string value = On ? "half sbs" : I18n.STRINGS.disabled;
            string hex = string.IsNullOrEmpty(OptionsMenu.OPTIONS_RED_HEX) ? "red" : OptionsMenu.OPTIONS_RED_HEX;
            return "3d: <color=" + hex + ">" + value + "</color>";
        }
    }

    /// Squeezes a full-screen quad into the left half and shows a copy in the right half.
    internal class QuadSplit
    {
        private readonly string label;
        private Transform t;
        private Vector3 basePos, baseScale;
        private GameObject clone;

        public QuadSplit(string label) { this.label = label; }

        /// The game re-sets the quad's X scale when the pixel size changes; take that as the new base.
        public void OnGameSetScale(Transform quad)
        {
            if (quad == t) baseScale = quad.localScale;
        }

        /// rightMaterial: material for the right half, or null to show the same image in both.
        public void Apply(MeshRenderer quad, Material rightMaterial)
        {
            if (t != quad.transform)
            {
                Restore();
                t = quad.transform;
                basePos = t.localPosition;
                baseScale = t.localScale;
            }

            Vector3 off = t.localRotation * Vector3.right * (baseScale.x * 0.25f);
            Vector3 half = new Vector3(baseScale.x * 0.5f, baseScale.y, baseScale.z);
            t.localPosition = basePos - off;
            t.localScale = half;

            if (clone == null)
            {
                clone = UnityEngine.Object.Instantiate(quad.gameObject, t.parent);
                clone.name = quad.gameObject.name + " (SBS right, " + label + ")";
            }
            clone.transform.localRotation = t.localRotation;
            clone.transform.localPosition = basePos + off;
            clone.transform.localScale = half;
            clone.GetComponent<MeshRenderer>().sharedMaterial = rightMaterial != null ? rightMaterial : quad.sharedMaterial;
            if (clone.activeSelf != quad.gameObject.activeSelf) clone.SetActive(quad.gameObject.activeSelf);
        }

        public void Restore()
        {
            if (t != null)
            {
                t.localPosition = basePos;
                t.localScale = baseScale;
            }
            t = null;
            if (clone != null) UnityEngine.Object.Destroy(clone);
            clone = null;
        }
    }

    internal static class StereoPatches
    {
        // In SBS mode never use the path where the main camera renders straight into the full-res
        // texture that also receives the UI (it happens when the pixel width equals that texture's
        // width, e.g. the "minimum" pixel size). Nudging the width by 2px keeps the separate path.
        [HarmonyPatch(typeof(PixelFilterAdjuster), nameof(PixelFilterAdjuster.SetPixelSize))]
        [HarmonyPrefix]
        private static void SetPixelSizePrefix(PixelFilterAdjuster __instance, ref int width)
        {
            if (!Stereo.On) return;
            if (__instance.originalRenderTexture != null && width == __instance.originalRenderTexture.width)
                width -= 2;
        }

        [HarmonyPatch(typeof(PixelFilterAdjuster), nameof(PixelFilterAdjuster.SetPixelSize))]
        [HarmonyPostfix]
        private static void SetPixelSizePostfix(PixelFilterAdjuster __instance)
        {
            if (__instance.renderQuad != null)
                Stereo.worldSplit.OnGameSetScale(__instance.renderQuad.transform);
        }

        // Add "3d: ..." to the bottom of Options > Graphics.
        private static bool buildingGraphicsMenu;

        [HarmonyPatch(typeof(OptionsMenu), "ShowGraphicsMenu")]
        [HarmonyPrefix]
        private static void ShowGraphicsMenuPrefix() => buildingGraphicsMenu = true;

        [HarmonyPatch(typeof(OptionsMenu), "ShowGraphicsMenu")]
        [HarmonyFinalizer]
        private static void ShowGraphicsMenuFinalizer() => buildingGraphicsMenu = false;

        [HarmonyPatch(typeof(OptionsMenu), "BuildSimpleMenu")]
        [HarmonyPrefix]
        private static void BuildSimpleMenuPrefix(object menuItems)
        {
            if (!buildingGraphicsMenu) return;
            buildingGraphicsMenu = false; // only the graphics menu itself, not submenus it may open
            try
            {
                Type itemType = AccessTools.Inner(typeof(OptionsMenu), "MenuItem");
                var ctor = AccessTools.Constructor(itemType, new[] { typeof(string), typeof(Action<BasicMenuItem>) });
                Action<BasicMenuItem> action = item =>
                {
                    Stereo.SetMode(Stereo.On ? StereoMode.Off : StereoMode.HalfSBS);
                    UI.SetGenericText(item.gameObject, Stereo.MenuText());
                };
                ((IList)menuItems).Add(ctor.Invoke(new object[] { Stereo.MenuText(), action }));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Couldn't add 3D option to graphics menu: " + e);
            }
        }
    }
}
