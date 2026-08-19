using System.Collections;
using System.Linq;
using MelonLoader;
using BoneLib;
using BoneLib.BoneMenu;
using RagdollPlayer;
using UnityEngine;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;

[assembly: MelonInfo(typeof(Downed.Core), "Downed", "1.1.3", "jorink")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace Downed
{
    public class Core : MelonMod
    {
        private enum PlayerState
        {
            Healthy,
            Downed,
            Dead
        }

        MelonPreferences_Category category;
        MelonPreferences_Entry<bool> EnableModEntry;
        MelonPreferences_Entry<bool> StayRagdolledEntry;

        private PlayerState state = PlayerState.Healthy;

        private const float ReviveGrabDuration = 5f;
        private const float BleedOutDuration = 20f;

        private static Grip[] playerGrips = System.Array.Empty<Grip>();
        private bool isBeingGrabbed;
        private float grabStartTime;

        private static float lastTimeInput;
        private static bool ragdollNextButton;
        private const float DoubleTapTimer = 0.32f;

        private RigManager rig;
        private PhysicsRig physRig;

        private object bleedOutCoroutine;

        private static bool? fusionInstalled;

        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();
            SetupMelonPreferences();
            SetupBoneMenu();
            Hooking.OnLevelLoaded += OnLevelLoaded;
            Hooking.OnPlayerDamageReceived += OnPlayerDamageReceived;
            Hooking.OnPlayerResurrected += OnPlayerResurrected;
            Hooking.OnPlayerDeath += OnPlayerDeath;
        }

        public override void OnDeinitializeMelon()
        {
            base.OnDeinitializeMelon();
            Hooking.OnLevelLoaded -= OnLevelLoaded;
            Hooking.OnPlayerDamageReceived -= OnPlayerDamageReceived;
            Hooking.OnPlayerResurrected -= OnPlayerResurrected;
            Hooking.OnPlayerDeath -= OnPlayerDeath;
        }

        private void SetupBoneMenu()
        {
            BoneLib.BoneMenu.Page defaultPage = BoneLib.BoneMenu.Page.Root.CreatePage("Jorink", Color.red).CreatePage("Downed", Color.magenta);
            defaultPage.CreateBool("Enable Mod", Color.blue, EnableModEntry.Value, (a) => { EnableModEntry.Value = a; });
            defaultPage.CreateBool("Stay Ragdolled", Color.green, StayRagdolledEntry.Value, (a) => { StayRagdolledEntry.Value = a; });
            defaultPage.CreateFunction("Save Settings", Color.cyan, () => { MelonPreferences.Save(); });
        }

        private void SetupMelonPreferences()
        {
            category = MelonPreferences.CreateCategory("Downed");
            EnableModEntry = category.CreateEntry("Enable Mod", true);
            StayRagdolledEntry = category.CreateEntry("Stay Ragdolled", false);
            MelonPreferences.Save();
            category.SaveToFile();
        }

        private void OnLevelLoaded(LevelInfo levelInfo)
        {
            rig = Player.RigManager;
            physRig = Player.PhysicsRig;

            var torso = physRig.torso;
            var leftHand = physRig.leftHand.physHand;
            var rightHand = physRig.rightHand.physHand;

            playerGrips = new Grip[]
            {
                torso.gChest,
                torso.gHead,
                torso.gNeck,
                torso.gPelvis,
                torso.gSpine,

                leftHand.gShoulder,
                leftHand.gElbow,

                rightHand.gShoulder,
                rightHand.gElbow,
            };
            
            Revive(); // Reset everything on level load just to be sure
        }

        private bool IsModAllowed()
        {
            if (!EnableModEntry.Value) return false;

            fusionInstalled ??= RegisteredMelons.Any(m => m.Info.Name == "LabFusion");
            if (!fusionInstalled.Value) return true;

            try
            {
                return FusionCompat.IsModAllowed();
            }
            catch
            {
                return true;
            }
        }

        private static bool CheckBeingGrabbed()
        {
            foreach (var grip in playerGrips)
            {
                if (grip.HasAttachedHands())
                {
                    return true;
                }
            }

            return false;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!IsModAllowed()) return;

            // Make sure the phys rig exists and the player isn't seated or in a menu
            if (rig && !rig.activeSeat && !UIRig.Instance.popUpMenu.m_IsCursorShown)
            {
                switch (state)
                {
                    case PlayerState.Downed:
                    case PlayerState.Dead:
                        if (!IsRagdolled(physRig))
                        {
                            RagdollPlayerMod.RagdollRig(rig);
                            StartBleedOut();
                        }

                        if (state == PlayerState.Dead && !physRig.shutdown)
                        {
                            physRig.ShutdownRig();
                        }
                        break;
                }
            }

            // Revive
            if (state == PlayerState.Downed)
            {
                if (CheckBeingGrabbed())
                {
                    if (!isBeingGrabbed)
                    {
                        isBeingGrabbed = true;
                        grabStartTime = Time.time;
                    }
                    else if (Time.time - grabStartTime >= ReviveGrabDuration)
                    {
                    	Revive();
                    }
                }
                else
                {
                    isBeingGrabbed = false;
                }
            }

            // Force unragdoll in case of bug
            var controller = GetController();
            if (!controller) return;

            if (GetInput(controller) && IsRagdolled(physRig))
            {
                Revive();
            }
        }

        private void StartBleedOut()
        {
            if (bleedOutCoroutine != null) return;
            bleedOutCoroutine = MelonCoroutines.Start(BleedOutRoutine());
        }

        private IEnumerator BleedOutRoutine()
        {
            float elapsed = 0f;

            while (state == PlayerState.Downed && elapsed < BleedOutDuration)
            {
                elapsed += Time.deltaTime;
                yield return null;
            }

            bleedOutCoroutine = null;

            if (state == PlayerState.Downed)
            {
                KillPlayer();
            }
        }

        private void StopBleedOut()
        {
            if (bleedOutCoroutine == null) return;
            MelonCoroutines.Stop(bleedOutCoroutine);
            bleedOutCoroutine = null;
        }

        private void Revive()
        {
			if (state == PlayerState.Downed || state == PlayerState.Dead)
			{
				rig.Teleport(physRig.feet.transform.position + new Vector3(0, 0.25f, 0));
			}
      
        	StopBleedOut();
        	state = PlayerState.Healthy;
        	isBeingGrabbed = false;
        	RagdollPlayerMod.UnragdollRig(rig);
        }

        private void KillPlayer()
        {
			state = PlayerState.Dead;
			rig.health.curr_Health = 0f;
        	rig.health.Dying(5);
        }

        private void DownPlayer()
        {
        	PreventDeath((Player_Health)rig.health);
        	state = PlayerState.Downed;
        	isBeingGrabbed = false;
        }

        private static BaseController GetController() => Player.RightController;

        private static bool GetInput(BaseController controller)
        {
            bool isDown = controller.GetThumbStickDown();
            float time = Time.time;

            if (isDown && ragdollNextButton)
            {
                if (time - lastTimeInput <= DoubleTapTimer)
                {
                    return true;
                }

                ragdollNextButton = false;
                lastTimeInput = 0f;
            }
            else if (isDown)
            {
                lastTimeInput = time;
                ragdollNextButton = true;
            }
            else if (time - lastTimeInput > DoubleTapTimer)
            {
                ragdollNextButton = false;
                lastTimeInput = 0f;
            }

            return false;
        }

        private static bool IsRagdolled(PhysicsRig physRig)
        {
            return physRig.torso.shutdown || !physRig.ballLocoEnabled;
        }

        private void OnPlayerDamageReceived(RigManager rigManager, float damage)
        {
            if (!IsModAllowed()) return;
            if (rigManager.health.curr_Health >= 0f) return;

            switch (state)
            {
                case PlayerState.Downed:
                    KillPlayer();
                    break;

                case PlayerState.Healthy:
                    DownPlayer();
                    break;
            }
        }

        private void OnPlayerDeath(RigManager rigManager)
        {
            if (!IsModAllowed()) return;
            if (StayRagdolledEntry.Value) return;

            Revive();
        }

        private void OnPlayerResurrected(Il2CppSLZ.Marrow.RigManager rigManager)
        {
        	if (!IsModAllowed()) return;
        	if (state == PlayerState.Healthy) return;
        	
        	Revive();
        }

        private static void PreventDeath(Player_Health health)
        {
            if (!health) return;
            health.LifeSavingDamgeDealt();
        }
    }
}
