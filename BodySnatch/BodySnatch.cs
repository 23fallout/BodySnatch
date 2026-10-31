using System.Collections.Generic;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace BodySnatch
{
    public class BodySnatch : ModBehaviour
    {
        public Key swapKey = Key.G;
        public float maxDistance = 5f;
        public float spawnUpOffset = 1f;
        public bool scaleBody = false;
        public float heightMultiplier = 1f;
        public float minRatio = 0.5f;
        public float maxRatio = 2f;
        public float invincibleSeconds = 2f;

        private bool _inSolarSystem;

        private bool _baselineCaptured;
        private float _playerEyeHeight;
        private float _playerStandingHeight;
        private Vector3 _origCam;
        private Vector3 _origScale = Vector3.one;
        private float _currentRatio = 1f;

        private ScreenPrompt _prompt;
        private CharacterDialogueTree _focusedTree;
        private float _nextFocusScan;

        private readonly List<CharacterDialogueTree> _trees = new List<CharacterDialogueTree>();
        private float _nextTreeRefresh;
        private const float TreeRefreshInterval = 5f;

        private float _invincibleUntil;
        private bool _grantedInvincibility;
        private PlayerResources _resources;

        private static readonly string[] ReleaseMethods = { "LoseFocus", "DisableInteraction" };

        private static readonly FieldInfo InvincibleField = AccessTools.Field(typeof(PlayerResources), "_invincible");
        private static readonly FieldInfo CharacterNameField = AccessTools.Field(typeof(CharacterDialogueTree), "_characterName");
        private static readonly FieldInfo AttentionPointField = AccessTools.Field(typeof(CharacterDialogueTree), "_attentionPoint");
        private static readonly FieldInfo AttentionOffsetField = AccessTools.Field(typeof(CharacterDialogueTree), "_attentionPointOffset");
        private static readonly FieldInfo DialogueTreeField = AccessTools.Field(typeof(CharacterAnimController), "_dialogueTree");
        private static readonly FieldInfo OrigCamField = AccessTools.Field(typeof(PlayerCameraController), "_origLocalPosition");
        private static readonly FieldInfo TargetCamField = AccessTools.Field(typeof(PlayerCameraController), "_targetLocalPosition");
        private static readonly string[] HeadWords = { "head_top" };
        private static readonly string[] HeadExclude = { };
        private static readonly string[] FootWords = { "foot", "toe", "ankle" };
        private readonly HashSet<string> _loggedBoneRoots = new HashSet<string>();

        public void Start()
        {
            LoadManager.OnCompleteSceneLoad += OnSceneLoad;
            _inSolarSystem = LoadManager.GetCurrentScene() == OWScene.SolarSystem;
            if (_inSolarSystem) RefreshTrees();
        }

        private void OnDestroy()
        {
            LoadManager.OnCompleteSceneLoad -= OnSceneLoad;
            try
            {
                if (_baselineCaptured) ApplyResize(1f);
                if (_grantedInvincibility && _resources != null && InvincibleField != null)
                    InvincibleField.SetValue(_resources, false);
            }
            catch { }
        }

        private void OnSceneLoad(OWScene previous, OWScene next)
        {
            _inSolarSystem = next == OWScene.SolarSystem;
            _baselineCaptured = false;
            _currentRatio = 1f;
            _prompt = null;
            _focusedTree = null;
            _resources = null;
            _grantedInvincibility = false;
            _trees.Clear();
            _nextTreeRefresh = 0f;
            if (_inSolarSystem) RefreshTrees();
        }


        private void GrantInvincibility()
        {
            if (InvincibleField == null)
            {
                ModHelper.Console.WriteLine("PlayerResources._invincible unable to be found", MessageType.Warning);
                return;
            }

            if (_resources == null) _resources = FindObjectOfType<PlayerResources>();
            if (_resources == null) return;

            if (!_grantedInvincibility && (bool)InvincibleField.GetValue(_resources)) return;

            InvincibleField.SetValue(_resources, true);
            _grantedInvincibility = true;
            _invincibleUntil = Time.time + invincibleSeconds;
        }

        private static bool NameHas(string name, string[] words)
        {
            string n = name.ToLowerInvariant();
            foreach (var w in words)
                if (n.Contains(w)) return true;
            return false;
        }

        private void UpdateInvincibility()
        {
            if (!_grantedInvincibility || Time.time < _invincibleUntil) return;

            _grantedInvincibility = false;
            if (_resources != null && InvincibleField != null)
                InvincibleField.SetValue(_resources, false);
        }


        private void RefreshTrees()
        {
            _trees.Clear();
            _trees.AddRange(FindObjectsOfType<CharacterDialogueTree>());
            _nextTreeRefresh = Time.time + TreeRefreshInterval;
        }

        private static bool IsUsable(CharacterDialogueTree tree)
        {
            return tree != null && tree.gameObject.activeInHierarchy;
        }


        private float GetNpcEyeHeight(CharacterDialogueTree tree, GameObject npc)
        {
            var ap = AttentionPointField.GetValue(tree) as Transform;
            if (ap == null) return 0f;

            Vector3 offset = (Vector3)AttentionOffsetField.GetValue(tree);
            Vector3 head = ap.TransformPoint(offset);
            return Vector3.Dot(head - npc.transform.position, npc.transform.up);
        }

        private float GetPlayerEyeHeight(OWRigidbody player, OWCamera cam)
        {
            return Vector3.Dot(cam.transform.position - player.transform.position, player.transform.up);
        }

        private float MeasureHeadToFoot(GameObject root, Vector3 up)
        {
            float headMax = float.MinValue, footMin = float.MaxValue;
            string headName = null, footName = null;

            foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                var mesh = r.sharedMesh;
                if (mesh == null) continue;

                var bind = mesh.bindposes;
                var bones = r.bones;
                int n = Mathf.Min(bind.Length, bones.Length);
                for (int i = 0; i < n; i++)
                {
                    if (bones[i] == null) continue;

                    string bn = bones[i].name;
                    bool isHead = NameHas(bn, HeadWords) && !NameHas(bn, HeadExclude);
                    bool isFoot = NameHas(bn, FootWords);
                    if (!isHead && !isFoot) continue;

                    Vector3 local = bind[i].inverse.MultiplyPoint3x4(Vector3.zero);
                    float h = Vector3.Dot(r.transform.TransformPoint(local), up);
                    if (isHead && h > headMax) { headMax = h; headName = bn; }
                    if (isFoot && h < footMin) { footMin = h; footName = bn; }
                }
            }

            if (headName == null && _loggedBoneRoots.Add(root.name))
            {
                var names = new List<string>();
                foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>())
                    foreach (var b in r.bones)
                        if (b != null) names.Add(b.name.Substring(b.name.LastIndexOf(':') + 1));
                ModHelper.Console.WriteLine($"{root.name} bones: {string.Join(", ", names.ToArray())}", MessageType.Info);
            }

            ModHelper.Console.WriteLine($"{root.name}: head bone '{headName}', foot bone '{footName}'", MessageType.Info);

            return (headName != null && footName != null && headMax > footMin) ? headMax - footMin : 0f;
        }

        private bool CaptureBaseline(OWRigidbody player, OWCamera cam)
        {
            if (_baselineCaptured) return true;

            var camCtrl = Locator.GetPlayerCameraController();
            if (camCtrl == null) return false;

            _origScale = player.transform.localScale;
            _origCam = (Vector3)OrigCamField.GetValue(camCtrl);
            _playerEyeHeight = GetPlayerEyeHeight(player, cam);
            _playerStandingHeight = MeasureHeadToFoot(player.gameObject, player.transform.up);
            _currentRatio = 1f;
            _baselineCaptured = true;

            ModHelper.Console.WriteLine(
                $"Baseline: eye height {_playerEyeHeight:F2}, standing height {_playerStandingHeight:F2}, scale {_origScale}",
                MessageType.Info);
            return true;
        }


        private void EnsurePrompt()
        {
            if (_prompt != null) return;
            var pm = Locator.GetPromptManager();
            if (pm == null) return;

            _prompt = new ScreenPrompt("", 0);
            pm.AddScreenPrompt(_prompt, PromptPosition.Center, true);
            _prompt.SetVisibility(false);
        }

        private CharacterDialogueTree GetFocusedTree()
        {
            foreach (var tree in _trees)
            {
                if (!IsUsable(tree)) continue;
                var vol = tree.GetInteractVolume();
                if (vol != null && vol.IsFocused() && !tree.InConversation())
                    return tree;
            }
            return null;
        }

        private void UpdatePrompt()
        {
            EnsurePrompt();

            bool show = false;
            if (_inSolarSystem && OWInput.IsInputMode(InputMode.Character))
            {
                if (Time.time >= _nextFocusScan)
                {
                    _nextFocusScan = Time.time + 0.1f;
                    _focusedTree = GetFocusedTree();
                }
                show = _focusedTree != null;
            }
            else
            {
                _focusedTree = null;
            }

            if (_prompt == null) return;

            if (show)
            {
                string name = CharacterNameField.GetValue(_focusedTree) as string;
                if (string.IsNullOrEmpty(name)) name = "this person";
                _prompt.SetText($"Become {name}   [{swapKey}]");
            }
            _prompt.SetVisibility(show);
        }


        private void Update()
        {
            if (!_inSolarSystem) return;

            UpdateInvincibility();

            if (Time.time >= _nextTreeRefresh) RefreshTrees();
            UpdatePrompt();

            if (!OWInput.IsInputMode(InputMode.Character)) return;

            var kb = Keyboard.current;
            if (kb == null) return;

            if (kb[swapKey].wasPressedThisFrame)
            {
                try { TrySwap(); }
                catch (System.Exception e) { ModHelper.Console.WriteLine($"TrySwap failed: {e}", MessageType.Error); }
            }

        }


        private void TrySwap()
        {
            var cam = Locator.GetPlayerCamera();
            var player = Locator.GetPlayerBody();
            if (cam == null || player == null) return;
            if (!CaptureBaseline(player, cam)) return;

            CharacterDialogueTree best = GetFocusedTree();
            float bestDist = float.MaxValue;
            if (best == null)
            {
                foreach (var tree in _trees)
                {
                    if (!IsUsable(tree) || tree.InConversation()) continue;

                    Vector3 to = tree.transform.position - cam.transform.position;
                    float dist = to.magnitude;
                    if (dist > maxDistance) continue;
                    if (Vector3.Dot(to.normalized, cam.transform.forward) < 0.5f) continue;

                    if (dist < bestDist) { best = tree; bestDist = dist; }
                }
                if (best == null)
                {
                    ModHelper.Console.WriteLine("No NPC in front of you", MessageType.Info);
                    return;
                }
            }

            GameObject npc = GetNpcRoot(best);

            float eyeRatio = 0f, standRatio = 0f;

            float npcEye = GetNpcEyeHeight(best, npc);
            if (npcEye > 0.2f && _playerEyeHeight > 0.2f)
                eyeRatio = npcEye / _playerEyeHeight;

            float npcStanding = MeasureHeadToFoot(npc, npc.transform.up);
            if (npcStanding > 0.2f && _playerStandingHeight > 0.2f)
                standRatio = npcStanding / _playerStandingHeight;

            ModHelper.Console.WriteLine(
                $"{npc.name}: eye ratio {eyeRatio:F2}, standing ratio {standRatio:F2} (npc {npcStanding:F2} vs you {_playerStandingHeight:F2})",
                MessageType.Info);

            float ratio;
            if (standRatio >= 0.6f && standRatio <= 1.5f) ratio = standRatio;      
            else if (eyeRatio >= 0.6f && eyeRatio <= 1.5f) ratio = eyeRatio;          
            else
            {
                ratio = 1f;                                                             
                ModHelper.Console.WriteLine("No height found", MessageType.Warning);
            }

            ratio = Mathf.Clamp(ratio * heightMultiplier, minRatio, maxRatio);

            Vector3 up = npc.transform.up;
            Vector3 basePos = npc.transform.position;
            Quaternion rot = npc.transform.rotation;
            OWRigidbody surface = npc.GetAttachedOWRigidbody(false);

            ModHelper.Console.WriteLine($"Becoming {npc.name}, ratio {ratio:F2}", MessageType.Info);

            HideNpc(npc, best);
            _focusedTree = null;

            _currentRatio = ratio;
            ApplyResize(ratio);
            Physics.SyncTransforms();

            Vector3 pos = basePos + up * spawnUpOffset * Mathf.Max(1f, ratio);
            player.WarpToPositionRotation(pos, rot);
            if (surface != null) player.SetVelocity(surface.GetPointVelocity(pos));

            GrantInvincibility();
        }

        private void HideNpc(GameObject npc, CharacterDialogueTree tree)
        {
            var targets = new HashSet<object>();

            var vol = tree.GetInteractVolume();
            if (vol != null) targets.Add(vol);

            foreach (var mb in npc.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name.Contains("Interact")) targets.Add(mb);
            foreach (var mb in tree.gameObject.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb.GetType().Name.Contains("Interact")) targets.Add(mb);

            foreach (var target in targets)
            {
                foreach (var methodName in ReleaseMethods)
                {
                    var m = target.GetType().GetMethod(
                        methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, System.Type.EmptyTypes, null);
                    if (m == null) continue;

                    try
                    {
                        m.Invoke(target, null);
                        ModHelper.Console.WriteLine($"Called {methodName} on {target.GetType().Name}", MessageType.Info);
                    }
                    catch (System.Exception e)
                    {
                        ModHelper.Console.WriteLine($"{methodName} failed on {target.GetType().Name}: {e.Message}", MessageType.Warning);
                    }
                }
            }

            foreach (var c in tree.GetComponentsInChildren<Collider>(true)) c.enabled = false;
            foreach (var c in npc.GetComponentsInChildren<Collider>(true)) c.enabled = false;

            npc.SetActive(false);
            tree.gameObject.SetActive(false);
        }

        private static GameObject GetNpcRoot(CharacterDialogueTree tree)
        {
            foreach (var anim in FindObjectsOfType<CharacterAnimController>())
            {
                if ((DialogueTreeField.GetValue(anim) as CharacterDialogueTree) == tree)
                    return anim.gameObject;
            }

            for (var t = tree.transform; t != null; t = t.parent)
            {
                if (t.GetComponentsInChildren<SkinnedMeshRenderer>().Length == 0) continue;
                if (t.GetComponentsInChildren<CharacterDialogueTree>().Length > 1) break;
                return t.gameObject;
            }

            return tree.gameObject;
        }

        private void ApplyResize(float ratio)
        {
            if (!_baselineCaptured) return;

            var body = Locator.GetPlayerBody();
            var camCtrl = Locator.GetPlayerCameraController();
            if (body == null || camCtrl == null) return;

            bool cameraIsChild = camCtrl.transform.IsChildOf(body.transform);

            if (scaleBody && cameraIsChild)
            {
                body.transform.localScale = _origScale * ratio;
                OrigCamField.SetValue(camCtrl, _origCam);
                TargetCamField.SetValue(camCtrl, _origCam);
            }
            else
            {
                body.transform.localScale = _origScale;
                Vector3 p = _origCam;
                p.y *= ratio;
                OrigCamField.SetValue(camCtrl, p);
                TargetCamField.SetValue(camCtrl, p);
            }
        }
    }
}