using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Cinemachine;
using HarmonyLib;
using InControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace ShortHikeCamera
{
    public enum CameraMode { Auto, ThirdPerson, FirstPerson }

    [BepInPlugin(Guid, "Short Hike Camera Modes", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "local.ashorthike.cameramodes";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        // Config
        internal ConfigEntry<KeyboardShortcut> cycleKey;
        internal ConfigEntry<float> yawSpeed, pitchSpeed;
        internal ConfigEntry<bool> invertY;
        internal ConfigEntry<float> tpDistance, tpPivotHeight, tpFov, tpMinPitch, tpMaxPitch;
        internal ConfigEntry<float> fpEyeOffset, fpFov, fpMaxPitch;
        internal ConfigEntry<bool> fpHidePlayer;

        // State
        internal CameraMode mode = CameraMode.Auto;
        internal float yaw, pitch;
        internal float tpCurrentDistance;
        internal bool ourCameraLive;        // our vcam is enabled and driving the view

        private GameObject camObj;
        private CinemachineVirtualCamera vcam;
        private LevelController level;
        private float levelSearchTimer;
        private Player player;

        private readonly HashSet<GameObject> regionCams = new HashSet<GameObject>();
        private readonly HashSet<GameObject> baselineCams = new HashSet<GameObject>();
        private bool baselineCaptured;
        private float regionRescanTimer;
        private string lastForeignName;

        private readonly Dictionary<Renderer, ShadowCastingMode> hiddenRenderers = new Dictionary<Renderer, ShadowCastingMode>();
        private float hideRescanTimer;

        private string toast;
        private float toastUntil;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            cycleKey = Config.Bind("Controls", "CycleKey", new KeyboardShortcut(KeyCode.F5), "Keyboard key that cycles camera modes (R3 on a gamepad always works).");
            yawSpeed = Config.Bind("Controls", "YawSpeed", 180f, "Horizontal look speed, degrees/second at full stick.");
            pitchSpeed = Config.Bind("Controls", "PitchSpeed", 120f, "Vertical look speed, degrees/second at full stick.");
            invertY = Config.Bind("Controls", "InvertY", false, "Invert vertical look.");

            tpDistance = Config.Bind("ThirdPerson", "Distance", 10f, "Camera distance from the player.");
            tpPivotHeight = Config.Bind("ThirdPerson", "PivotHeight", 2.2f, "Height above the player's feet the camera orbits around.");
            tpFov = Config.Bind("ThirdPerson", "FieldOfView", 50f, "Vertical field of view.");
            tpMinPitch = Config.Bind("ThirdPerson", "MinPitch", -35f, "Lowest pitch (negative = looking up).");
            tpMaxPitch = Config.Bind("ThirdPerson", "MaxPitch", 75f, "Highest pitch (positive = looking down).");

            fpEyeOffset = Config.Bind("FirstPerson", "EyeHeightOffset", 0f, "Extra height added to the player's head position.");
            fpFov = Config.Bind("FirstPerson", "FieldOfView", 70f, "Vertical field of view.");
            fpMaxPitch = Config.Bind("FirstPerson", "MaxPitch", 80f, "Max look up/down angle.");
            fpHidePlayer = Config.Bind("FirstPerson", "HidePlayerModel", true, "Hide the player model (keeps its shadow).");

            Stereo.Init(Config);

            var harmony = new Harmony(Guid);
            harmony.PatchAll(typeof(Patches));
            harmony.PatchAll(typeof(StereoPatches));

            // Run on our own hidden, persistent object so the game can't destroy our update loop.
            var runnerObj = new GameObject("ShortHikeCamera_Runner");
            runnerObj.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(runnerObj);
            runnerObj.AddComponent<Runner>();
            Log.LogInfo("Short Hike Camera Modes loaded. R3 / " + cycleKey.Value + " cycles Auto -> 3rd person -> 1st person.");
        }

        private bool loggedTick;

        internal void Tick()
        {
            if (!loggedTick) { loggedTick = true; Log.LogInfo("Update loop running."); }
            CheckConfigReload();
            Stereo.Tick();
            UpdateGameToast();
            if (level == null)
            {
                levelSearchTimer -= Time.unscaledDeltaTime;
                if (levelSearchTimer <= 0f)
                {
                    levelSearchTimer = 1f;
                    level = Object.FindObjectOfType<LevelController>();
                }
            }
            Player p = level != null ? level.player : null;

            if (p != player)
            {
                // New scene / player: reset per-scene caches.
                RestorePlayerRenderers();
                player = p;
                Log.LogInfo(player != null ? "Player found: " + player.name : "Player lost (scene change / menu).");
                regionCams.Clear();
                baselineCams.Clear();
                baselineCaptured = false;
                regionRescanTimer = 0f;
            }

            if (player == null)
            {
                SetOurCameraLive(false);
                return;
            }

            EnsureCamera();

            bool inputFocused = player.input != null && player.input.hasFocus;
            bool cinemaCamActive = level.cinemaCamera != null && level.cinemaCamera.activeSelf;

            // Cycle mode
            bool pressed = cycleKey.Value.IsDown();
            if (inputFocused && !cinemaCamActive && InputManager.ActiveDevice != null && InputManager.ActiveDevice.RightStickButton.WasPressed)
                pressed = true;
            if (pressed)
                Log.LogInfo("Cycle pressed (inputFocused=" + inputFocused + ", cinemaCam=" + cinemaCamActive + ")");
            if (pressed && !cinemaCamActive)
                CycleMode();

            // Region camera bookkeeping
            regionRescanTimer -= Time.unscaledDeltaTime;
            if (regionRescanTimer <= 0f)
            {
                regionRescanTimer = 2f;
                RescanRegionCameras();
            }
            if (!baselineCaptured)
            {
                CaptureBaseline();
                baselineCaptured = true;
            }

            // Should the game's own (cutscene / fishing / etc.) camera take over?
            string foreign = FindForeignCamera();
            if (foreign != lastForeignName)
            {
                if (foreign != null) Log.LogInfo("Deferring to game camera: " + foreign);
                else if (lastForeignName != null) Log.LogInfo("Game camera released: " + lastForeignName);
                lastForeignName = foreign;
            }

            bool wantLive = mode != CameraMode.Auto && foreign == null && !cinemaCamActive;
            SetOurCameraLive(wantLive);

            if (ourCameraLive && inputFocused)
            {
                Vector2 look = player.input.GetLookDirection();
                float dt = Time.deltaTime;
                yaw += look.x * yawSpeed.Value * dt;
                pitch += (invertY.Value ? look.y : -look.y) * pitchSpeed.Value * dt;
            }
            ClampPitch();
            yaw = Mathf.Repeat(yaw, 360f);

            // First-person model hiding
            bool shouldHide = ourCameraLive && mode == CameraMode.FirstPerson && fpHidePlayer.Value;
            if (shouldHide)
            {
                hideRescanTimer -= Time.unscaledDeltaTime;
                if (hideRescanTimer <= 0f)
                {
                    hideRescanTimer = 0.5f;
                    HidePlayerRenderers();
                }
            }
            else if (hiddenRenderers.Count > 0)
            {
                RestorePlayerRenderers();
            }
        }

        private float configCheckTimer;
        private System.DateTime configWriteTime;

        /// Reload the .cfg when it's edited on disk, so settings can be tuned while the game runs.
        private void CheckConfigReload()
        {
            configCheckTimer -= Time.unscaledDeltaTime;
            if (configCheckTimer > 0f) return;
            configCheckTimer = 1f;
            try
            {
                var t = System.IO.File.GetLastWriteTimeUtc(Config.ConfigFilePath);
                if (configWriteTime == default(System.DateTime)) { configWriteTime = t; return; }
                if (t == configWriteTime) return;
                configWriteTime = t;
                Config.Reload();
                ApplyLensForMode();
                ShowToast("camera settings reloaded");
                Log.LogInfo("Config reloaded.");
            }
            catch (System.Exception e) { Log.LogWarning("Config reload failed: " + e.Message); }
        }

        private void CycleMode()
        {
            CameraMode prev = mode;
            mode = (CameraMode)(((int)mode + 1) % 3);

            if (prev == CameraMode.Auto)
            {
                // Start from wherever the game camera is currently looking, to avoid a snap.
                Camera main = Camera.main;
                if (main != null)
                {
                    Vector3 e = main.transform.rotation.eulerAngles;
                    yaw = e.y;
                    pitch = Mathf.DeltaAngle(0f, e.x);
                }
                CopyLensFromActiveCamera();
                tpCurrentDistance = tpDistance.Value;
            }

            ApplyLensForMode();
            ShowToast("camera: " + ModeName(mode));
            Log.LogInfo("Camera mode -> " + mode);
        }

        private static string ModeName(CameraMode m)
        {
            switch (m)
            {
                case CameraMode.ThirdPerson: return "3rd person";
                case CameraMode.FirstPerson: return "1st person";
                default: return "auto";
            }
        }

        private void ClampPitch()
        {
            if (mode == CameraMode.FirstPerson)
                pitch = Mathf.Clamp(pitch, -fpMaxPitch.Value, fpMaxPitch.Value);
            else
                pitch = Mathf.Clamp(pitch, tpMinPitch.Value, tpMaxPitch.Value);
        }

        // ---------- Camera object ----------

        private void EnsureCamera()
        {
            if (camObj != null) return;
            camObj = new GameObject("ShortHikeCamera_VCam");
            Object.DontDestroyOnLoad(camObj);
            camObj.SetActive(false);
            vcam = camObj.AddComponent<CinemachineVirtualCamera>();
            vcam.Priority = 950; // above region cameras and the fishing camera (<= 900)
            camObj.AddComponent<ModeCameraExtension>();
            ApplyLensForMode();
        }

        private void SetOurCameraLive(bool live)
        {
            if (camObj == null) { ourCameraLive = false; return; }
            if (live == ourCameraLive && camObj.activeSelf == live) return;

            if (live && player != null)
            {
                vcam.Follow = player.transform; // lets the brain pick the right update timing
                // Put the vcam at its target pose before enabling so the blend starts cleanly.
                ComputePose(out Vector3 pos, out Quaternion rot);
                camObj.transform.SetPositionAndRotation(pos, rot);
            }
            camObj.SetActive(live);
            ourCameraLive = live;
        }

        private void CopyLensFromActiveCamera()
        {
            if (vcam == null) return;
            CinemachineBrain brain = Camera.main != null ? Camera.main.GetComponent<CinemachineBrain>() : null;
            var active = brain != null ? brain.ActiveVirtualCamera as CinemachineVirtualCamera : null;
            if (active != null && active != vcam)
                vcam.m_Lens = active.m_Lens;
        }

        private void ApplyLensForMode()
        {
            if (vcam == null) return;
            LensSettings lens = vcam.m_Lens;
            if (mode == CameraMode.FirstPerson)
            {
                lens.FieldOfView = fpFov.Value;
                lens.NearClipPlane = 0.05f;
            }
            else
            {
                lens.FieldOfView = tpFov.Value;
                lens.NearClipPlane = 0.1f;
            }
            if (lens.FarClipPlane < 100f) lens.FarClipPlane = 1000f;
            vcam.m_Lens = lens;
        }

        // ---------- Pose ----------

        internal void ComputePose(out Vector3 pos, out Quaternion rot)
        {
            rot = Quaternion.Euler(pitch, yaw, 0f);
            if (player == null) { pos = camObj != null ? camObj.transform.position : Vector3.zero; return; }

            if (mode == CameraMode.FirstPerson)
            {
                pos = player.headTarget + Vector3.up * fpEyeOffset.Value;
                return;
            }

            Vector3 pivot = player.transform.position + Vector3.up * tpPivotHeight.Value;
            Vector3 back = rot * Vector3.back;
            float want = tpDistance.Value;

            int mask = Physics.DefaultRaycastLayers
                       & ~(1 << player.gameObject.layer)
                       & ~(1 << CameraCullLayers.HOLDABLE_LAYER)
                       & ~(1 << CameraCullLayers.BOBBER_LAYER)
                       & ~(1 << CameraCullLayers.WATERDRIFTERS_LAYER);
            const float radius = 0.25f;
            float allowed = want;
            if (Physics.SphereCast(pivot, radius, back, out RaycastHit hit, want, mask, QueryTriggerInteraction.Ignore))
                allowed = Mathf.Max(0.4f, hit.distance - 0.05f);

            // Pull in instantly when blocked; ease back out when clear.
            if (allowed < tpCurrentDistance || tpCurrentDistance <= 0f)
                tpCurrentDistance = allowed;
            else
                tpCurrentDistance = Mathf.MoveTowards(tpCurrentDistance, allowed, 6f * Time.deltaTime);

            pos = pivot + back * tpCurrentDistance;
        }

        // ---------- Deciding when the game's own camera should win ----------

        private void RescanRegionCameras()
        {
            foreach (var r in Object.FindObjectsOfType<VirtualCameraRegion>(true))
                if (r != null && r.virtualCamera != null)
                    regionCams.Add(r.virtualCamera.gameObject);
        }

        private void CaptureBaseline()
        {
            int n = CinemachineCore.Instance.VirtualCameraCount;
            for (int i = 0; i < n; i++)
            {
                var c = CinemachineCore.Instance.GetVirtualCamera(i);
                if (c == null || c == vcam) continue;
                if (!regionCams.Contains(c.gameObject))
                {
                    baselineCams.Add(c.gameObject);
                    Log.LogInfo("Baseline camera: " + c.name + " (priority " + c.Priority + ")");
                }
            }
        }

        /// Returns the name of an active non-region, non-baseline camera (cutscene, fishing, tower viewer...), or null.
        private string FindForeignCamera()
        {
            int n = CinemachineCore.Instance.VirtualCameraCount;
            for (int i = 0; i < n; i++)
            {
                var c = CinemachineCore.Instance.GetVirtualCamera(i);
                if (c == null || c == vcam || !c.isActiveAndEnabled) continue;
                GameObject go = c.gameObject;
                if (regionCams.Contains(go) || baselineCams.Contains(go)) continue;
                return c.name;
            }
            return null;
        }

        // ---------- First-person model hiding ----------

        private void HidePlayerRenderers()
        {
            if (player == null) return;
            foreach (var r in player.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
                if (hiddenRenderers.ContainsKey(r)) continue;
                hiddenRenderers[r] = r.shadowCastingMode;
                r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            }
        }

        private void RestorePlayerRenderers()
        {
            foreach (var kv in hiddenRenderers)
                if (kv.Key != null) kv.Key.shadowCastingMode = kv.Value;
            hiddenRenderers.Clear();
            hideRescanTimer = 0f;
        }

        // ---------- UI ----------

        private void ShowToast(string text)
        {
            toastUntil = Time.unscaledTime + 1.5f;
            if (TryShowGameToast(text))
            {
                toast = null; // game UI is showing it; skip the IMGUI fallback
                return;
            }
            toast = text;
        }

        private StatusBox gameToast;
        private StatusBarUI statusBar;
        private bool loggedToastPrefab;

        /// Show the message in the game's status-bar box (the top-left box used for coin pickups),
        /// with the item icon hidden. The game's own animator slides it in and out.
        private bool TryShowGameToast(string text)
        {
            try
            {
                if (statusBar == null) statusBar = Object.FindObjectOfType<StatusBarUI>();
                if (statusBar == null || statusBar.statusBoxPrefab == null) return false;

                if (gameToast == null)
                {
                    GameObject go = Object.Instantiate(statusBar.statusBoxPrefab);
                    go.name = "ShortHikeCamera_StatusBox";
                    go.transform.SetParent(statusBar.transform, worldPositionStays: false);
                    gameToast = go.GetComponent<StatusBox>();
                    toastBaseWidth = -1f;
                    if (gameToast == null) { Object.Destroy(go); return false; }

                    if (!loggedToastPrefab)
                    {
                        loggedToastPrefab = true;
                        foreach (var rt in go.GetComponentsInChildren<RectTransform>(true))
                        {
                            var comps = string.Join(", ", System.Linq.Enumerable.Select(rt.GetComponents<Component>(), c => c.GetType().Name));
                            Log.LogInfo("Status box part: " + rt.name + " size=" + rt.rect.size + " pos=" + rt.anchoredPosition + " [" + comps + "]");
                        }
                    }

                    if (gameToast.icon != null) gameToast.icon.gameObject.SetActive(false);

                    // Stack below any status boxes already showing (same rule the game uses).
                    float y = 0f;
                    foreach (var b in statusBar.GetComponentsInChildren<StatusBox>())
                        if (b != gameToast) y = Mathf.Min(y, b.rect.anchoredPosition.y - b.rect.sizeDelta.y);
                    gameToast.rect.anchoredPosition = new Vector2(statusBar.margins, y - statusBar.margins);
                }

                gameToast.text.text = text;
                FitToastWidth();
                gameToast.Show();
                gameToast.HideAndKill(1.5f);
                return true;
            }
            catch (System.Exception e)
            {
                Log.LogWarning("Game-style toast failed, using fallback: " + e.Message);
                return false;
            }
        }

        /// Shift the text into the hidden icon's spot and widen the box so longer text fits.
        private float toastBaseWidth = -1f;
        private void FitToastWidth()
        {
            var textRt = gameToast.text.rectTransform;
            var iconRt = gameToast.icon != null ? gameToast.icon.rectTransform : null;
            if (toastBaseWidth < 0f)
            {
                toastBaseWidth = gameToast.rect.sizeDelta.x;
                if (iconRt != null && iconRt.parent == textRt.parent)
                {
                    // Start the text where the icon used to start.
                    float shift = textRt.offsetMin.x - iconRt.offsetMin.x;
                    if (shift > 0f) textRt.offsetMin = new Vector2(textRt.offsetMin.x - shift, textRt.offsetMin.y);
                }
            }
            float extra = gameToast.text.preferredWidth - textRt.rect.width;
            gameToast.rect.sizeDelta = new Vector2(toastBaseWidth + Mathf.Max(0f, extra + 2f), gameToast.rect.sizeDelta.y);
        }

        private void UpdateGameToast() { }

        private GUIStyle toastStyle;

        internal void DrawGUI()
        {
            if (toast == null || Time.unscaledTime > toastUntil) return;
            if (toastStyle == null)
            {
                toastStyle = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.UpperCenter };
            }
            if (Stereo.On)
            {
                // Draw once per eye, squeezed horizontally like the rest of the half-SBS image.
                Matrix4x4 saved = GUI.matrix;
                GUI.matrix = Matrix4x4.Scale(new Vector3(0.5f, 1f, 1f));
                DrawToastAt(0f);
                GUI.matrix = Matrix4x4.TRS(new Vector3(Screen.width * 0.5f, 0f, 0f), Quaternion.identity, new Vector3(0.5f, 1f, 1f));
                DrawToastAt(0f);
                GUI.matrix = saved;
            }
            else
            {
                DrawToastAt(0f);
            }
        }

        private void DrawToastAt(float x)
        {
            var rect = new Rect(x, Screen.height * 0.06f, Screen.width, 50);
            var shadow = new Rect(rect.x + 2, rect.y + 2, rect.width, rect.height);
            toastStyle.normal.textColor = new Color(0, 0, 0, 0.7f);
            GUI.Label(shadow, toast, toastStyle);
            toastStyle.normal.textColor = Color.white;
            GUI.Label(rect, toast, toastStyle);
        }
    }

    internal class Runner : MonoBehaviour
    {
        private void Update()
        {
            try { Plugin.Instance?.Tick(); }
            catch (System.Exception e) { Plugin.Log.LogError(e); }
        }

        private void OnGUI()
        {
            Plugin.Instance?.DrawGUI();
        }
    }

    /// Drives our vcam's pose inside the Cinemachine pipeline, so it updates in lockstep
    /// with the brain regardless of the game's jitter-fix (FixedUpdate vs SmartUpdate) setting.
    public class ModeCameraExtension : CinemachineExtension
    {
        protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase vcam, CinemachineCore.Stage stage, ref CameraState state, float deltaTime)
        {
            if (stage != CinemachineCore.Stage.Body) return;
            var plugin = Plugin.Instance;
            if (plugin == null) return;
            plugin.ComputePose(out Vector3 pos, out Quaternion rot);
            state.RawPosition = pos;
            state.RawOrientation = rot;
            vcam.transform.SetPositionAndRotation(pos, rot);
        }
    }

    internal static class Patches
    {
        // Disable the game's small right-stick "look around" nudge while our camera is driving,
        // so the stick isn't doing two things during blends.
        [HarmonyPatch(typeof(GameVirtualCameraExtension), "PerformLookAround")]
        [HarmonyPrefix]
        private static bool PerformLookAroundPrefix()
        {
            var p = Plugin.Instance;
            return p == null || !p.ourCameraLive;
        }
    }
}
