using System.Collections;
using System.Linq;
using MelonLoader;
using UnityEngine;
using BoneLib;
using Il2CppSLZ.Marrow;
using jlib;

namespace downed
{
	public class Downed : MelonMod
	{
		public const string Version = "2.0.0";

		private const float ReviveDuration = 5f;
		private const float BleedOutDuration = 20f;
		private const float DoubleTapTimer = 0.32f;

		private enum PlayerState { Default, Downed, Dead }

		private MelonPreferences_Entry<bool> enableMod;
		private MelonPreferences_Entry<bool> stayRagdolled;

		private PlayerState state;
		private RigManager rig;
		private PhysicsRig physRig;
		private BaseController controller;
		private Grip[] playerGrips = System.Array.Empty<Grip>();

		private object bleedOutRoutine;
		private float grabStartTime;
		private bool reviveStarted;
		private bool firstSkipped;

		private float lastTimeInput;
		private bool ragdollNextButton;

		public override void OnInitializeMelon()
		{
			var menu = JLib.Register("Downed", Color.magenta);

			enableMod = menu.Bool("Enable Mod", true, Color.green);
			stayRagdolled = menu.Bool("Stay Ragdolled (Dead)", false, Color.yellow);

			Hooking.OnLevelLoaded += OnLevelLoaded;
			Hooking.OnPlayerDamageReceived += OnPlayerDamageReceived;
			Hooking.OnPlayerResurrected += OnPlayerResurrected;
			Hooking.OnPlayerDeath += OnPlayerDeath;
		}

		private void OnLevelLoaded(LevelInfo levelInfo)
		{
			rig = Player.RigManager;
			physRig = Player.PhysicsRig;
			controller = Player.RightController;

			state = PlayerState.Default; // Reset on level load just to be sure.

			var torso = physRig.torso;
			var leftHand = physRig.leftHand.physHand;
			var rightHand = physRig.rightHand.physHand;

			playerGrips = new Grip[]
			{
				torso.gChest, torso.gHead, torso.gNeck, torso.gPelvis, torso.gSpine,
				leftHand.gShoulder, leftHand.gElbow,
				rightHand.gShoulder, rightHand.gElbow,
			};
		}

		private bool isModAllowed => enableMod.Value && rig && FusionCompat();
		private bool isDowned => state == PlayerState.Downed;
		private bool isRagdolled => physRig.torso.shutdown || !physRig.ballLocoEnabled;
		private bool isLocalRig(RigManager hookRig) => isModAllowed && hookRig == Player.RigManager;

		public override void OnUpdate()
		{
			if (!isModAllowed) return;

			if (isDowned)
			{
				if (!isRagdolled)
				{
					RagdollPlayer();
					StartBleedOut();
				}

				// Stop regenerating while downed
				var health = JLib.playerHealth;
				if (health != null && health.regenRoutine != null) health.StopCoroutine(health.regenRoutine);
			}
			else firstSkipped = false;

			if (state == PlayerState.Dead && !physRig.shutdown) physRig.ShutdownRig();

			if (reviveChecks()) Revive();
		}

		private void RagdollPlayer()
		{
			physRig.RagdollRig();
			physRig.DisableBallLoco();
			physRig.PhysicalLegs();
			physRig.legLf.ShutdownLimb();
			physRig.legRt.ShutdownLimb();
		}

		private void UnragdollPlayer()
		{
			var feet = physRig.feet.transform;
			var knee = physRig.knee.transform;
			var pelvis = physRig.m_pelvis;

			physRig.TurnOnRig();
			physRig.UnRagdollRig();

			knee.SetPositionAndRotation(pelvis.position, pelvis.rotation);
			feet.SetPositionAndRotation(pelvis.position, pelvis.rotation);
		}

		private void OnPlayerDamageReceived(RigManager rig, float damage)
		{
			if (!isLocalRig(rig) || rig.health.curr_Health > 0f) return;

			if (state == PlayerState.Default) DownPlayer();
			else if (state == PlayerState.Downed) KillPlayer();
		}

		private void OnPlayerDeath(RigManager rig)
		{
			if (isLocalRig(rig)) Revive();
		}

		// Used for reviving with SDK mods
		private void OnPlayerResurrected(RigManager rig)
		{
			if (!isLocalRig(rig) || state == PlayerState.Default) return;

			// Skip first revive because LifeSavingDamgeDealt() will trigger OnPlayerResurrected() in DownPlayer().
			if (isDowned && !firstSkipped)
			{
				firstSkipped = true;
				return;
			}
			Revive();
		}

		private bool FusionCompat()
		{
			if (!JLib.isFusionInstalled) return true;
			
			if (!LabFusion.Network.NetworkInfo.HasServer) return true;
			if (LabFusion.SDK.Gamemodes.GamemodeManager.ActiveGamemode != null) return false;
			return !LabFusion.Preferences.CommonPreferences.Knockout;
		}

		private bool reviveChecks()
		{
			if (forceReviveInput())
			{
				state = PlayerState.Default;
				StopBleedOut();
				if (isRagdolled) UnragdollPlayer();
			}

			if (!isDowned || !playerGrips.Any(g => g.HasAttachedHands()))
			{
				reviveStarted = false;
				return false;
			}

			if (reviveStarted) return Time.time - grabStartTime >= ReviveDuration;

			reviveStarted = true;
			grabStartTime = Time.time;
			return false;
		}

		private void Revive()
		{
			state = PlayerState.Default;
			StopBleedOut();
			if (isRagdolled && !stayRagdolled.Value) UnragdollPlayer();
		}

		private void DownPlayer()
		{
			state = PlayerState.Downed;
			JLib.playerHealth.LifeSavingDamgeDealt(); // Using Revive() from the game's code causes flinging in Fusion.
		}

		private void KillPlayer()
		{
			state = PlayerState.Dead;
			rig.health.curr_Health = 0f;
			rig.health.Dying(5);
		}

		private bool forceReviveInput()
		{
			bool isDown = controller.GetThumbStickDown();
			bool expired = Time.time - lastTimeInput > DoubleTapTimer;

			if (isDown && ragdollNextButton) // Double click
			{
				if (!expired) return true;
			}
			else if (isDown) // First click
			{
				lastTimeInput = Time.time;
				ragdollNextButton = true;
				return false;
			}
			else if (!expired) return false;

			ragdollNextButton = false; // Reset after timer
			lastTimeInput = 0f;
			return false;
		}

		private void StartBleedOut() => bleedOutRoutine ??= MelonCoroutines.Start(BleedOutRoutine());

		private IEnumerator BleedOutRoutine()
		{
			float elapsed = 0f;

			while (isDowned && elapsed < BleedOutDuration)
			{
				if (!reviveStarted) elapsed += Time.deltaTime;
				yield return null;
			}

			if (isDowned) KillPlayer();
			bleedOutRoutine = null;
		}

		private void StopBleedOut()
		{
			if (bleedOutRoutine == null) return;
			MelonCoroutines.Stop(bleedOutRoutine);
			bleedOutRoutine = null;
		}
	}
}
