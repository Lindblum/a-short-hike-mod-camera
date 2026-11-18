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
    public enum SteeringMode { Auto, Turn, Strafe }

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
        internal ConfigEntry<SteeringMode> steering;
        internal ConfigEntry<bool> itemCycle;
        internal ConfigEntry<float> turnSpeed;
        internal ConfigEntry<bool> glidePitch;
        internal ConfigEntry<float> glideDiveCameraPitch;
        internal ConfigEntry<float> tpDistance, tpPivotHeight, tpFov, tpMinPitch, tpMaxPitch;
        internal ConfigEntry<float> followGlideSpeed, followGlidePitch;
        internal ConfigEntry<float> fpEyeOffset, fpFov, fpMaxPitch;
        internal ConfigEntry<bool> fpHidePlayer;
        internal ConfigEntry<float> fpTransitionTime;
        internal ConfigEntry<float> blendTime;

        // State
        internal CameraMode mode = CameraMode.Auto;
        internal float yaw, pitch;
        internal float tpCurrentDistance;
        internal bool ourCameraLive;        // our vcam is enabled and driving the view

        private GameObject camObj;
        internal CinemachineVirtualCamera vcam;
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
            itemCycle = Config.Bind("Controls", "ItemSwitching", true, "RB/LB cycle through your items (and none), like equipping from the pause menu. Replaces LB's put-away/drop.");
            steering = Config.Bind("Controls", "Steering", SteeringMode.Auto, "3rd/1st person left stick. Auto = Turn while gliding (right thumb holds jump), Strafe otherwise. Turn = left/right turns, up/down moves. Strafe = game's original camera-relative movement.");
            glidePitch = Config.Bind("Controls", "GlidePitch", true, "While turn-steering a glide, left stick up = dive (more the further you push), neutral/down = flat glide.");
            glideDiveCameraPitch = Config.Bind("Controls", "GlideDiveCameraPitch", 30f, "How much further the camera tilts down at a full dive, degrees.");
            turnSpeed = Config.Bind("Controls", "TurnSpeed", 75f, "Turn rate at full left-stick deflection (Turn steering), degrees/second.");

            tpDistance = Config.Bind("ThirdPerson", "Distance", 10f, "Camera distance from the player.");
            tpPivotHeight = Config.Bind("ThirdPerson", "PivotHeight", 2.2f, "Height above the player's feet the camera orbits around.");
            tpFov = Config.Bind("ThirdPerson", "FieldOfView", 50f, "Vertical field of view.");
            tpMinPitch = Config.Bind("ThirdPerson", "MinPitch", -35f, "Lowest pitch (negative = looking up).");
            tpMaxPitch = Config.Bind("ThirdPerson", "MaxPitch", 75f, "Highest pitch (positive = looking down).");

            followGlideSpeed = Config.Bind("ThirdPerson", "GlideFollowSpeed", 120f, "While gliding, max rate the camera swings in behind your heading, degrees/second.");
            followGlidePitch = Config.Bind("ThirdPerson", "GlideCameraPitch", 15f, "Pitch the camera eases to while gliding (positive = looking down).");

            fpEyeOffset = Config.Bind("FirstPerson", "EyeHeightOffset", 0f, "Extra height added to Claire's eye level (negative = lower).");
            fpFov = Config.Bind("FirstPerson", "FieldOfView", 70f, "Vertical field of view.");
            fpMaxPitch = Config.Bind("FirstPerson", "MaxPitch", 80f, "Max look up/down angle.");
            fpTransitionTime = Config.Bind("FirstPerson", "TransitionTime", 1f, "Seconds for the camera to move in from 3rd person to 1st person (0 = instant).");
            blendTime = Config.Bind("General", "BlendTime", 1f, "Seconds for the camera to blend between the game's cameras and ours (entering 3rd person, leaving 1st person). 0 = instant.");
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

            if (itemCycle.Value && inputFocused && !cinemaCamActive)
                ItemCycleTick();

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
                float lookYaw = look.x * yawSpeed.Value * dt;
                yaw += lookYaw;
                pitch += (invertY.Value ? look.y : -look.y) * pitchSpeed.Value * dt;

                if (look.sqrMagnitude > 0.04f) lookIdleTime = 0f;
                else lookIdleTime += dt;

                bool turning = TurnSteeringActive(player);
                if (turning && !wasTurning) BeginTurnSteering();
                wasTurning = turning;

                if (turning)
                {
                    float turn = player.input.GetMovement().x * turnSpeed.Value * dt;
                    steerHeading = Mathf.Repeat(steerHeading + turn, 360f);
                    if (mode == CameraMode.FirstPerson)
                    {
                        // 1st person: the view is the heading (right stick still turns both).
                        steerHeading = Mathf.Repeat(steerHeading + lookYaw, 360f);
                        yaw = steerHeading;
                    }
                    else
                    {
                        // 3rd person: camera turns with you, and eases in behind the heading.
                        yaw += turn;
                        FollowHeading(dt);
                    }
                }
            }
            else
            {
                wasTurning = false;
            }
            ClampPitch();
            yaw = Mathf.Repeat(yaw, 360f);

            // First-person model hiding
            UpdateEyeHeight();
            float tp = TransitionProgress();
            if (transitioning && vcam != null)
            {
                LensSettings lens = vcam.m_Lens;
                lens.FieldOfView = Mathf.Lerp(transitionFromFov, fpFov.Value, tp);
                vcam.m_Lens = lens;
            }
            else if (tp >= 1f && mode == CameraMode.FirstPerson && vcam != null && !Mathf.Approximately(vcam.m_Lens.FieldOfView, fpFov.Value))
            {
                ApplyLensForMode();
            }

            bool shouldHide = ourCameraLive && mode == CameraMode.FirstPerson && fpHidePlayer.Value && tp >= 0.85f;
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

        /// Our own saves (e.g. the 3d menu toggle) shouldn't count as external edits.
        internal void MarkConfigSavedByUs()
        {
            try { configWriteTime = System.IO.File.GetLastWriteTimeUtc(Config.ConfigFilePath); } catch { }
        }

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

        private float lookIdleTime;

        private bool wasTurning;
        internal float steerHeading;

        /// Turn steering is active in 3rd/1st person when Steering = Turn, or Steering = Auto and
        /// Claire is gliding (the right thumb is holding jump, so it can't work the right stick).
        /// On the ground the right stick keeps full camera control.
        internal bool TurnSteeringActive(Player p)
        {
            if (!ourCameraLive || mode == CameraMode.Auto) return false;
            if (p == null || p != player || p.input == null || !p.input.hasFocus) return false;
            if (p.isClimbing || p.isMounted) return false;
            switch (steering.Value)
            {
                case SteeringMode.Turn: return true;
                case SteeringMode.Auto: return p.isGliding;
                default: return false;
            }
        }

        /// Start the turn heading from where things already are, so switching never jerks:
        /// 3rd person = the way Claire is facing, 1st person = the current view.
        private void BeginTurnSteering()
        {
            steerHeading = yaw;
            if (mode == CameraMode.ThirdPerson && player != null)
            {
                Vector3 f = player.transform.forward;
                f.y = 0f;
                if (f.sqrMagnitude > 0.01f) steerHeading = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            }
        }

        /// 3rd person turn steering: ease the camera in behind the steering heading (unless the
        /// right stick is being used), and settle the pitch while gliding.
        private void FollowHeading(float dt)
        {
            if (lookIdleTime <= 0f) return;
            float diff = Mathf.DeltaAngle(yaw, steerHeading);
            float step = Mathf.Min(Mathf.Abs(diff) * 3f, followGlideSpeed.Value) * dt;
            yaw += Mathf.Sign(diff) * Mathf.Min(step, Mathf.Abs(diff));
            FollowGlidePitch(dt, followGlidePitch.Value);
        }

        /// How far into a dive (0 = flat glide, 1 = full dive) turn steering wants Claire (3rd person: LS up = dive).
        internal float GlideDiveTarget(Player p)
        {
            return Mathf.Clamp01(p.input.GetMovement().y);
        }

        /// While gliding with the right stick idle, ease the camera pitch to basePitch, tilting further
        /// down as Claire dives (glide pitch on LS up/down).
        private void FollowGlidePitch(float dt, float basePitch)
        {
            if (lookIdleTime <= 0f || !player.isGliding) return;
            float dive = glidePitch.Value ? Patches.glideTiltRef(player) : 0f;
            float target = basePitch + Mathf.Max(0f, dive) * glideDiveCameraPitch.Value;
            pitch = Mathf.MoveTowards(pitch, target, 45f * dt);
        }

        // ---------- Item switching (RB = next, LB = previous) ----------

        /// The items the pause menu would let you equip, in the pause menu's order, plus whatever
        /// is held right now. Slot 0 (null) = none.
        private System.Collections.Generic.List<CollectableItem> ItemCycleSlots()
        {
            var items = new System.Collections.Generic.List<CollectableItem>();
            foreach (var item in Singleton<GlobalData>.instance.gameData.GetAllCollected())
                if (item != null && item.worldPrefab != null && item.worldPrefab.GetComponent<Holdable>() != null && !items.Contains(item))
                    items.Add(item);
            CollectableItem held = player.heldItem != null ? player.heldItem.associatedItem : null;
            if (held != null && !items.Contains(held)) items.Add(held);
            items.Sort((a, b) => b.priority.CompareTo(a.priority)); // pause menu: priority descending
            items.Insert(0, null);
            return items;
        }

        private void ItemCycleTick()
        {
            var actions = QuickUnityTools.Input.GameUserInput.sharedActionSet;
            if (actions == null) return;
            int dir = 0;
            if (actions.rightBumper != null && actions.rightBumper.WasPressed) dir = 1;
            else if (actions.leftBumper != null && actions.leftBumper.WasPressed) dir = -1;
            if (dir == 0) return;

            if (player.isClimbing || player.isMounted) return;
            Holdable heldNow = player.heldItem;
            if (heldNow != null && heldNow.associatedItem != null && heldNow.associatedItem.cannotStash)
            {
                ShowToast("can't put this away", heldNow.associatedItem.icon);
                return;
            }

            var slots = ItemCycleSlots();
            if (slots.Count <= 1) return; // nothing to switch to
            int cur = slots.IndexOf(heldNow != null ? heldNow.associatedItem : null);
            if (cur < 0) cur = 0;
            CollectableItem next = slots[(cur + dir + slots.Count) % slots.Count];

            if (heldNow != null) player.StashHeldItem();
            if (next != null) Holdable.EquipFromInventory(player, next);

            if (next != null) ShowToast(I18n.Localize(next.readableName).ToLowerInvariant(), next.icon);
            else ShowToast("none");
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

            if (prev == CameraMode.ThirdPerson && mode == CameraMode.FirstPerson) BeginTransition();
            else transitioning = false;

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

        // ---------- 1st-person eye height ----------
        // Player.headTarget is a look-at point around her neck/chest, so find her actual eyes:
        // objects named "eye" under the head bone (avg), else the head bone + a bit. Height only,
        // smoothed, so walking bob doesn't jolt the view.

        private Player eyeOwner;
        private Transform headBone;
        private Transform[] eyeParts = new Transform[0];
        private float fpEyeHeight = -1f;

        private void FindEyes()
        {
            eyeOwner = player;
            headBone = null;
            eyeParts = new Transform[0];
            fpEyeHeight = -1f;
            if (player == null || player.ikAnimator == null) return;

            var anim = player.ikAnimator.GetComponent<Animator>();
            if (anim != null && anim.isHuman) headBone = anim.GetBoneTransform(HumanBodyBones.Head);
            Transform searchRoot = headBone != null ? headBone : player.ikAnimator.transform;

            var found = new System.Collections.Generic.List<Transform>();
            foreach (var t in searchRoot.GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                if (n.Contains("eye") && !n.Contains("brow") && !n.Contains("lid") && !n.Contains("lash"))
                    found.Add(t);
            }
            eyeParts = found.ToArray();

            float feet = player.transform.position.y;
            Log.LogInfo("1st person eyes: head bone=" + (headBone != null ? headBone.name + " @" + (headBone.position.y - feet).ToString("0.00") : "none")
                + ", eye parts=" + (eyeParts.Length == 0 ? "none" : string.Join(", ", System.Linq.Enumerable.Select(eyeParts, e => e.name + " @" + (e.position.y - feet).ToString("0.00"))))
                + ", headTarget @" + player.headOffset.ToString("0.00"));
        }

        private void UpdateEyeHeight()
        {
            if (player == null) return;
            if (eyeOwner != player) FindEyes();

            float target;
            if (eyeParts.Length > 0)
            {
                float sum = 0f; int n = 0;
                foreach (var e in eyeParts) if (e != null) { sum += e.position.y; n++; }
                target = n > 0 ? sum / n : player.headTarget.y;
            }
            else if (headBone != null) target = headBone.position.y + 0.3f;
            else target = player.headTarget.y;
            target -= player.transform.position.y;

            if (fpEyeHeight < 0f) fpEyeHeight = target;
            else fpEyeHeight = Mathf.Lerp(fpEyeHeight, target, 1f - Mathf.Exp(-4f * Time.deltaTime));
        }

        // 3rd -> 1st person transition (both use our one vcam, so Cinemachine can't blend it).
        private bool transitioning;
        private float transitionStart;
        private Vector3 transitionFromOffset;   // camera position relative to Claire at the switch
        private Quaternion transitionFromRot;
        private float transitionFromFov;

        private float TransitionProgress()
        {
            if (!transitioning) return 1f;
            float t = (Time.unscaledTime - transitionStart) / Mathf.Max(0.01f, fpTransitionTime.Value);
            if (t >= 1f) { transitioning = false; return 1f; }
            return t * t * (3f - 2f * t); // smoothstep
        }

        private void BeginTransition()
        {
            Camera main = Camera.main;
            if (main == null || player == null || !ourCameraLive || fpTransitionTime.Value <= 0f) { transitioning = false; return; }
            transitionFromOffset = main.transform.position - player.transform.position;
            transitionFromRot = main.transform.rotation;
            transitionFromFov = main.fieldOfView;
            transitionStart = Time.unscaledTime;
            transitioning = true;
        }

        internal void ComputePose(out Vector3 pos, out Quaternion rot)
        {
            ComputeTargetPose(out pos, out rot);
            if (!transitioning || player == null) return;
            float t = TransitionProgress();
            Vector3 from = player.transform.position + transitionFromOffset;
            pos = Vector3.Lerp(from, pos, t);
            rot = Quaternion.Slerp(transitionFromRot, rot, t);
        }

        private void ComputeTargetPose(out Vector3 pos, out Quaternion rot)
        {
            rot = Quaternion.Euler(pitch, yaw, 0f);
            if (player == null) { pos = camObj != null ? camObj.transform.position : Vector3.zero; return; }

            if (mode == CameraMode.FirstPerson)
            {
                float h = fpEyeHeight > 0f ? fpEyeHeight : player.headOffset;
                pos = player.transform.position + Vector3.up * (h + fpEyeOffset.Value);
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

        private void ShowToast(string text, Sprite icon = null)
        {
            toastUntil = Time.unscaledTime + 1.5f;
            if (TryShowGameToast(text, icon))
            {
                toast = null; // game UI is showing it; skip the IMGUI fallback
                return;
            }
            toast = text;
        }

        private StatusBox gameToast;
        private bool gameToastHasIcon;
        private StatusBarUI statusBar;
        private bool loggedToastPrefab;

        /// Show the message in the game's status-bar box (the top-left box used for coin pickups),
        /// with the item icon hidden. The game's own animator slides it in and out.
        private bool TryShowGameToast(string text, Sprite icon)
        {
            try
            {
                if (statusBar == null) statusBar = Object.FindObjectOfType<StatusBarUI>();
                if (statusBar == null || statusBar.statusBoxPrefab == null) return false;

                // The text layout differs with/without an icon, so start a fresh box when that changes.
                if (gameToast != null && gameToastHasIcon != (icon != null))
                {
                    Object.Destroy(gameToast.gameObject);
                    gameToast = null;
                }

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

                    gameToastHasIcon = icon != null;
                    if (gameToast.icon != null) gameToast.icon.gameObject.SetActive(gameToastHasIcon);

                    // Stack below any status boxes already showing (same rule the game uses).
                    float y = 0f;
                    foreach (var b in statusBar.GetComponentsInChildren<StatusBox>())
                        if (b != gameToast) y = Mathf.Min(y, b.rect.anchoredPosition.y - b.rect.sizeDelta.y);
                    gameToast.rect.anchoredPosition = new Vector2(statusBar.margins, y - statusBar.margins);
                }

                gameToast.text.text = text;
                if (icon != null && gameToast.icon != null) gameToast.icon.sprite = icon;
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
                if (!gameToastHasIcon && iconRt != null && iconRt.parent == textRt.parent)
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
            if (Stereo.WorldStereoActive)
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
        private void Start()
        {
            StartCoroutine(Stereo.EndOfFrameLoop());
        }

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

        // Fixed-length blend for any switch into or out of our camera.
        [HarmonyPatch(typeof(CinemachineBrain), "LookupBlend")]
        [HarmonyPostfix]
        private static void LookupBlendPostfix(ICinemachineCamera fromKey, ICinemachineCamera toKey, ref CinemachineBlendDefinition __result)
        {
            var p = Plugin.Instance;
            if (p == null || p.vcam == null) return;
            if (!ReferenceEquals(fromKey, p.vcam) && !ReferenceEquals(toKey, p.vcam)) return;
            float t = p.blendTime.Value;
            __result = t > 0f
                ? new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, t)
                : new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.Cut, 0f);
        }

        // LB is "previous item" while item switching is on, so it mustn't also put away / drop.
        [HarmonyPatch(typeof(Player.PlayerInput), nameof(Player.PlayerInput.IsDropTapped))]
        [HarmonyPrefix]
        private static bool IsDropTappedPrefix(ref bool __result)
        {
            var p = Plugin.Instance;
            if (p == null || !p.itemCycle.Value) return true;
            __result = false;
            return false;
        }

        // Turn steering: left stick X turns the steering heading (handled in Plugin.Tick), so movement
        // only uses the forward/back axis along it. While gliding she always follows the heading
        // (up/down is used for pitch instead, see the glide patches below).
        [HarmonyPatch(typeof(Player), nameof(Player.GetInputWorldDirection))]
        [HarmonyPrefix]
        private static bool GetInputWorldDirectionPrefix(Player __instance, ref Vector3 __result)
        {
            var p = Plugin.Instance;
            if (p == null || !p.TurnSteeringActive(__instance)) return true;

            Vector2 m = __instance.input.GetMovement();
            float forward = __instance.isGliding ? 1f : m.y;
            Vector3 fwd = Quaternion.Euler(0f, p.steerHeading, 0f) * Vector3.forward;
            __result = fwd * forward;
            return false;
        }

        // Glide pitch. The game tilts Claire nose-down (dive) when the stick is released and flattens
        // the glide when it's pushed: glideTiltPercent = 1 - |stick|. In turn steering, LS up/down
        // drives that instead: up = dive (proportional), neutral/down = flat glide.
        internal static readonly AccessTools.FieldRef<Player, float> glideTiltRef =
            AccessTools.FieldRefAccess<Player, float>("glideTiltPercent");
        private static readonly AccessTools.FieldRef<Player, float> glideSpeedRef =
            AccessTools.FieldRefAccess<Player, float>("glideSpeed");
        private static readonly System.Func<Player, bool> isInUpdraft =
            AccessTools.MethodDelegate<System.Func<Player, bool>>(AccessTools.PropertyGetter(typeof(Player), "isInUpdraft"));

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPrefix]
        private static void PlayerUpdatePrefix(Player __instance, out float __state)
        {
            __state = glideTiltRef(__instance);
        }

        [HarmonyPatch(typeof(Player), "Update")]
        [HarmonyPostfix]
        private static void PlayerUpdatePostfix(Player __instance, float __state)
        {
            var p = Plugin.Instance;
            if (p == null || !p.glidePitch.Value || !__instance.isGliding || !p.TurnSteeringActive(__instance)) return;
            if (p.mode == CameraMode.FirstPerson) return; // 1st person keeps the game's glide: stick pushed = flat, released = dive
            if (isInUpdraft(__instance)) return; // keep the game's updraft behaviour

            float target = p.GlideDiveTarget(__instance);
            float tilt = Mathf.Lerp(__state, target, Time.deltaTime * __instance.glideTiltSpeed);
            glideTiltRef(__instance) = tilt;

            // Recompute what the game derived from the tilt this frame (speed cap + body pose).
            float angle = Mathf.LerpUnclamped(0f, 60f, tilt); // same constant as Player.glideAngle
            float cap = __instance.maxGlideSpeed * Mathf.Cos(angle * Mathf.Deg2Rad);
            float speed = Mathf.Min(__instance.body.velocity.magnitude, cap);
            glideSpeedRef(__instance) = Mathf.Max(__instance.maxSpeed, speed);
            if (__instance.ikAnimator != null)
            {
                Vector3 e = __instance.ikAnimator.transform.eulerAngles;
                e.x = angle;
                __instance.ikAnimator.transform.eulerAngles = e;
            }
        }
    }
}
