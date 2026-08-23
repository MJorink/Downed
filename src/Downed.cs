using System.Collections;
using MelonLoader;
using BoneLib;
using BoneLib.BoneMenu;
using RagdollPlayer;
using UnityEngine;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;

[assembly: MelonInfo(typeof(Downed.Core), "Downed", "1.1.5", "jorink")]
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

        private static MelonPreferences_Category category;
        private static MelonPreferences_Entry<bool> EnableModEntry;
        private static MelonPreferences_Entry<bool> StayRagdolledEntry;
        
        private static PlayerState state = PlayerState.Healthy;
        
        private static BaseController GetController() => Player.RightController;
        private static Grip[] playerGrips = System.Array.Empty<Grip>();
        
        private static bool isBeingRevived;
        
        private static object bleedOutCoroutine;
        private static float currentTime;

        private static RigManager rig;
        private static PhysicsRig physRig;

        public override void OnInitializeMelon()
        {
            SetupMelonPreferences();
            SetupBoneMenu();
            SetupHooks();
        }

        private static void SetupMelonPreferences()
        {
            category = MelonPreferences.CreateCategory("Downed");
            EnableModEntry = category.CreateEntry("Enable Mod", true);
            StayRagdolledEntry = category.CreateEntry("Stay Ragdolled", false);
            MelonPreferences.Save();
            category.SaveToFile();
        }

        private static void SetupBoneMenu()
        {
            BoneLib.BoneMenu.Page defaultPage = BoneLib.BoneMenu.Page.Root.CreatePage("Jorink", Color.red).CreatePage("Downed", Color.magenta);
            defaultPage.CreateBool("Enable Mod", Color.blue, EnableModEntry.Value, (a) => { EnableModEntry.Value = a; });
            defaultPage.CreateBool("Stay Ragdolled", Color.green, StayRagdolledEntry.Value, (a) => { StayRagdolledEntry.Value = a; });
            defaultPage.CreateFunction("Save Settings", Color.cyan, () => { MelonPreferences.Save(); });
        }

        private static void SetupHooks()
        {
        	Hooking.OnLevelLoaded += OnLevelLoaded;
        	Hooking.OnPlayerDamageReceived += OnPlayerDamageReceived;
        	Hooking.OnPlayerResurrected += OnPlayerResurrected;
        	Hooking.OnPlayerDeath += OnPlayerDeath;
        }

        private static void OnLevelLoaded(LevelInfo levelInfo)
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

            if (IsModAllowed())
            {
            	Revive(); // Reset everything on level load just to be sure
            }
        }

        private static bool IsModAllowed()
        {
            if (!EnableModEntry.Value || !rig || rig.activeSeat || UIRig.Instance.popUpMenu.m_IsCursorShown) return false;

            bool? isFusionInstalled = false;
            isFusionInstalled ??= RegisteredMelons.Any(m => m.Info.Name == "LabFusion");
            if (!isFusionInstalled.Value) return true;
            
            if (!LabFusion.Network.NetworkInfo.HasServer) return true;
            if (LabFusion.SDK.Gamemodes.GamemodeManager.ActiveGamemode != null) return false;
            return !LabFusion.Preferences.Server.SavedServerSettings.Knockout.Value;
        }

        private static bool isBeingGrabbed()
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
            if (!IsModAllowed()) return;
            
            currentTime = Time.time;

            if (isDowned() || isDead())
            {
            	if (!isRagdolled(physRig))
            	{
            		RagdollPlayerMod.RagdollRig(rig);
            		StartBleedOut();
            		return;
            	}

            	if (isDead() && !physRig.shutdown)
            	{
            		physRig.ShutdownRig();
            		return;
            	}
            }

            if (isRevived())
            {
            	Revive();
            }            
        }

        private static bool isDead()
        {
        	return state == PlayerState.Dead;
        }
        
        private static bool isDowned()
        {
        	return state == PlayerState.Downed;
        }

        private static bool isRevived()
        {
        	if (GetReviveInput(GetController()) && isRagdolled(physRig)) return true; // Force revive

        	var grabStartTime = 0f;
        	const float ReviveDuration = 5f;
        	
        	if (isDowned() && isBeingGrabbed())
        	{
        		if (isBeingRevived)
        		{
        			if (currentTime - grabStartTime >= ReviveDuration) return true;
        			return false;
        		}
        		isBeingRevived = true;
        		grabStartTime = currentTime;
        		return false;
        	}
        	isBeingRevived = false;
        	return false;
        }

        private static bool StartBleedOut()
        {
            if (bleedOutCoroutine != null) return false;
            bleedOutCoroutine = MelonCoroutines.Start(BleedOutRoutine());
            return true;
        }

        private static IEnumerator BleedOutRoutine()
        {
            float elapsed = 0f;
            const float BleedOutDuration = 20f;

            while (isDowned() && elapsed < BleedOutDuration)
            {
            	if (isBeingRevived) yield return null;
                elapsed += Time.deltaTime;
                yield return null;
            }

            bleedOutCoroutine = null;

            if (isDowned())
            {
                KillPlayer();
            }
        }

        private static void StopBleedOut()
        {
            if (bleedOutCoroutine == null) return;
            MelonCoroutines.Stop(bleedOutCoroutine);
            bleedOutCoroutine = null;
        }

        private static void Revive()
        {
        	isBeingRevived = false;
        	state = PlayerState.Healthy;
        	StopBleedOut();

        	if (isRagdolled(physRig))
        	{
        		rig.Teleport(physRig.feet.transform.position + new Vector3(0, 0.25f, 0));
        		RagdollPlayerMod.UnragdollRig(rig);
        	}
        }

        private static void KillPlayer()
        {
			state = PlayerState.Dead;
			rig.health.curr_Health = 0f;
        	rig.health.Dying(5);
        }

        private static void DownPlayer()
        {
        	// Prevents death, using Revive() from the game causes flinging in fusion.
        	Player_Health playerHealth = rig.health.TryCast<Player_Health>();
        	
        	state = PlayerState.Downed;
        	playerHealth.LifeSavingDamgeDealt();
        }

        private static bool GetReviveInput(BaseController controller)
        {
            bool isDown = controller.GetThumbStickDown();
            float lastTimeInput = 0f;
            bool ragdollNextButton = false;
            const float DoubleTapTimer = 0.32f;
            

            if (isDown && ragdollNextButton)
            {
                if (currentTime - lastTimeInput <= DoubleTapTimer)
                {
                    return true;
                }

                ragdollNextButton = false;
                lastTimeInput = 0f;
            }
            else if (isDown)
            {
                lastTimeInput = currentTime;
                ragdollNextButton = true;
            }
            else if (currentTime - lastTimeInput > DoubleTapTimer)
            {
                ragdollNextButton = false;
                lastTimeInput = 0f;
            }

            return false;
        }

        private static bool isRagdolled(PhysicsRig physRig)
        {
            return physRig.torso.shutdown || !physRig.ballLocoEnabled;
        }

        private static void OnPlayerDamageReceived(RigManager rigManager, float damage)
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

        private static void OnPlayerDeath(RigManager rigManager)
        {
            if (!IsModAllowed() || StayRagdolledEntry.Value) return;
            Revive();
        }

		// Used for reviving with SDK mods
        private static void OnPlayerResurrected(Il2CppSLZ.Marrow.RigManager rigManager)
        {
        	if (!IsModAllowed() || state == PlayerState.Healthy) return;
        	Revive();
        }
    }
}
