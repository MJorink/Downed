using MelonLoader;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;
using UnityEngine;
using RagdollPlayer;
using System.Collections;
using System.Linq;

namespace Downed
{
	public class DownedMod : MelonMod
	{
		public const string Title = "Downed";
		public const string Description = "A BoneLab mod that makes you go downed before dying, while downed you can get revived!";
		public const string Version = "1.2.0";

		private enum PlayerState
		{
			Default,
			Downed,
			Dead
		}

		private static MelonPreferences_Entry<bool> enableMod;
		private static MelonPreferences_Entry<bool> stayRagdolled;

		private static PlayerState state = PlayerState.Default;
		private static BaseController controller;
		private static Grip[] playerGrips = System.Array.Empty<Grip>();
		private static RigManager rig;
		private static PhysicsRig physRig;

		private static bool reviveStarted;
		private static object bleedOutCoroutine;
		private static float currentTime;

		private static float lastTimeInput;
		private static bool ragdollNextButton;

		public override void OnInitializeMelon()
		{
			SetupMelonPreferences();
			SetupBoneMenu();
			SetupHooks();
		}

		private void SetupMelonPreferences()
		{
			var category = MelonPreferences.CreateCategory("Downed");

			enableMod = category.CreateEntry("Enable Mod", true);
			stayRagdolled = category.CreateEntry("Stay Ragdolled", false);

			MelonPreferences.Save();
		}

		private void SetupBoneMenu()
		{
			BoneLib.BoneMenu.Page defaultPage = BoneLib.BoneMenu.Page.Root.CreatePage("Jorink", Color.red).CreatePage("Downed", Color.magenta); // Change color!

			defaultPage.CreateBool("Enable Mod", Color.green, enableMod.Value, (value) => { enableMod.Value = value; });
			defaultPage.CreateBool("Stay Ragdolled", Color.green, stayRagdolled.Value, (a) => { stayRagdolled.Value = a; });
			defaultPage.CreateFunction("Save Settings", Color.green, () => MelonPreferences.Save());
		}

		private static void SetupHooks()
		{
			Hooking.OnLevelLoaded += OnLevelLoaded;
			Hooking.OnPlayerDamageReceived += OnPlayerDamageReceived;
			Hooking.OnPlayerResurrected += OnPlayerResurrected;
			Hooking.OnPlayerDeath += OnPlayerDeath;
		}

		private static bool isModAllowed()
		{
			if (!enableMod.Value || !rig || !physRig || !controller) return false;
			return FusionCompat();
		}

		private static bool FusionCompat()
		{
			bool isFusionInstalled = RegisteredMelons.Any(m => m.Info.Name == "LabFusion");
			if (!isFusionInstalled) return true;

			if (!LabFusion.Network.NetworkInfo.HasServer) return true;
			if (LabFusion.SDK.Gamemodes.GamemodeManager.ActiveGamemode != null) return false;
			return !LabFusion.Preferences.Server.SavedServerSettings.Knockout.Value;
		}

		private static void OnLevelLoaded(LevelInfo levelInfo)
		{
			rig = Player.RigManager;
			physRig = Player.PhysicsRig;
			controller = Player.RightController;

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
			
			if (isModAllowed())
			{
				Revive(); // Reset everything on level load just to be sure
			}
		}

		public override void OnUpdate()
		{
			if (!isModAllowed()) return;
			currentTime = Time.time;

			if (isDowned() && !isRagdolled(physRig) && !UIRig.Instance.popUpMenu.m_IsCursorShown && !rig.activeSeat)
			{
				RagdollPlayerMod.RagdollRig(rig);
				StartBleedOut();
			}
			else if (state == PlayerState.Dead && !physRig.shutdown)
			{
				physRig.ShutdownRig();
			}
			else if (reviveChecks())
			{
				Revive();
			}
		}

		private static void OnPlayerDamageReceived(RigManager rig, float damage)
		{
		    if (!isModAllowed()) return;
		    if (rig.health.curr_Health >= 0f) return;

		    switch (state)
		    {
		    	case PlayerState.Default:
		    	    DownPlayer();
		    	    break;

		        case PlayerState.Downed:
		            KillPlayer();
		            break;
		    }
		}

		private static void OnPlayerDeath(RigManager rig)
		{
		    if (!isModAllowed() || stayRagdolled.Value) return;
		    Revive();
		}

		// Used for reviving with SDK mods
		private static void OnPlayerResurrected(RigManager rig)
		{
			if (!isModAllowed() || state == PlayerState.Default) return;
			Revive();
		}

		private static bool isDowned()
		{
			return state == PlayerState.Downed;
		}

		private static bool reviveChecks()
		{
			if (forceReviveInput(controller) && isRagdolled(physRig)) return true; // Force revive

			var grabStartTime = 0f;
			const float ReviveDuration = 5f;

			if (isDowned() && grabChecks())
			{
				if (reviveStarted)
				{
					if (currentTime - grabStartTime >= ReviveDuration) return true;
					return false;
				}
				
				reviveStarted = true;
				grabStartTime = currentTime;
				return false;
			}

			reviveStarted = false;
			return false;
		}

		private static bool grabChecks()
		{
		    foreach (var grip in playerGrips)
		    {
		        if (grip.HasAttachedHands()) return true;
		    }

		    return false;
		}

		private static void Revive()
		{
			reviveStarted = false;
			state = PlayerState.Default;
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
			Player_Health playerHealth = rig.health.TryCast<Player_Health>(); // Using Revive() from the game causes issues in fusion.
			state = PlayerState.Downed;
			playerHealth.LifeSavingDamgeDealt();
		}

		private static bool isRagdolled(PhysicsRig physRig)
		{
		    return physRig.torso.shutdown || !physRig.ballLocoEnabled;
		}

		private static bool forceReviveInput(BaseController controller)
		{
		    bool isDown = controller.GetThumbStickDown();
		    const float DoubleTapTimer = 0.32f;

		    if (isDown && ragdollNextButton) // Double click
		    {
		        if (currentTime - lastTimeInput <= DoubleTapTimer)
		        {
		            return true;
		        }

		        ragdollNextButton = false;
		        lastTimeInput = 0f;
		    }
		    else if (isDown) // First click
		    {
		        lastTimeInput = currentTime;
		        ragdollNextButton = true;
		    }
		    else if (currentTime - lastTimeInput > DoubleTapTimer) // Reset after timer
		    {
		        ragdollNextButton = false;
		        lastTimeInput = 0f;
		    }

		    return false;
		}

        private static void StartBleedOut()
        {
            if (bleedOutCoroutine != null) return;
            bleedOutCoroutine = MelonCoroutines.Start(BleedOutRoutine());
            return;
        }

        private static IEnumerator BleedOutRoutine()
        {
            float elapsed = 0f;
            const float BleedOutDuration = 20f;

            while (isDowned() && elapsed < BleedOutDuration)
            {
            	if (reviveStarted) yield return null;
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
	}
}
