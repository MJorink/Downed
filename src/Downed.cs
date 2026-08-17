using MelonLoader;
using BoneLib;
using BoneLib.BoneMenu;
using RagdollPlayer;
using UnityEngine;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;

[assembly: MelonInfo(typeof(Downed.Core), "Downed", "1.1.0", "jorink")]
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

        private PlayerState state = PlayerState.Healthy;
        private float startTime;
        private const float ReviveDuration = 5f;

        private static float lastTimeInput;
        private static bool ragdollNextButton;
        private const float DoubleTapTimer = 0.32f;

        public override void OnInitializeMelon()
        {
            base.OnInitializeMelon();
            SetupMelonPreferences();
            SetupBoneMenu();
            Hooking.OnLevelLoaded += OnLevelLoaded;
            Hooking.OnPlayerDamageReceived += OnPlayerDamageReceived;
            Hooking.OnPlayerDeath += OnPlayerDeath;
        }

        public override void OnDeinitializeMelon()
        {
            base.OnDeinitializeMelon();
            Hooking.OnLevelLoaded -= OnLevelLoaded;
            Hooking.OnPlayerDamageReceived -= OnPlayerDamageReceived;
            Hooking.OnPlayerDeath -= OnPlayerDeath;
        }

        private void SetupBoneMenu()
        {
            BoneLib.BoneMenu.Page defaultPage = BoneLib.BoneMenu.Page.Root.CreatePage("Jorink", Color.red).CreatePage("Downed", Color.magenta);
            defaultPage.CreateBool("Enable Mod", Color.blue, EnableModEntry.Value, (a) => { EnableModEntry.Value = a; });
            defaultPage.CreateFunction("Save Settings", Color.cyan, () => { MelonPreferences.Save(); });
        }

        private void SetupMelonPreferences()
        {
            category = MelonPreferences.CreateCategory("Downed");
            EnableModEntry = category.CreateEntry("Enable Mod", true);
            MelonPreferences.Save();
            category.SaveToFile();
        }

        private void OnLevelLoaded(LevelInfo levelInfo)
        {
            state = PlayerState.Healthy;
        }

        public override void OnUpdate()
        {
            base.OnUpdate();
            if (!EnableModEntry.Value) return;

            var rig = Player.RigManager;

            // Make sure the phys rig exists and the player isn't seated or in a menu
            if (rig && !rig.activeSeat && !UIRig.Instance.popUpMenu.m_IsCursorShown)
            {
                switch (state)
                {
                    case PlayerState.Downed:
                    case PlayerState.Dead:
                        var physRig = Player.PhysicsRig;

                        if (!IsRagdolled(physRig))
                        {
                            RagdollPlayerMod.RagdollRig(rig);
                        }

                        if (state == PlayerState.Dead && !physRig.shutdown)
                        {
                            physRig.ShutdownRig();
                        }
                        break;
                }
            }

            // Timer to unragdoll
            if (state == PlayerState.Downed && Time.time - startTime >= ReviveDuration)
            {
                state = PlayerState.Healthy;
                RagdollPlayerMod.UnragdollRig(rig);
            }

            // Force unragdoll in case of bug
            var controller = GetController();
            if (!controller) return;

            if (GetInput(controller) && IsRagdolled(Player.PhysicsRig))
            {
                state = PlayerState.Healthy;
                RagdollPlayerMod.UnragdollRig(rig);
            }
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
            if (!EnableModEntry.Value) return;
            if (rigManager.health.curr_Health > 0f) return;

            switch (state)
            {
                case PlayerState.Downed:
                    state = PlayerState.Dead;
                    rigManager.health.Dying(5);
                    break;

                case PlayerState.Healthy:
                    state = PlayerState.Downed;
                    Revive((Player_Health)rigManager.health);
                    startTime = Time.time;
                    break;
            }
        }

        private void OnPlayerDeath(RigManager rigManager)
        {
            if (!EnableModEntry.Value) return;

            state = PlayerState.Healthy;
            RagdollPlayerMod.UnragdollRig(rigManager);

            // Fix flinging on respawn in Fusion lobbies
            var physRig = Player.PhysicsRig;
            var teleport = physRig.feet.transform.position + new Vector3(0, 0.25f, 0);
            rigManager.Teleport(teleport);
        }

        private static void Revive(Player_Health health)
        {
            if (!health) return;
            health.LifeSavingDamgeDealt();
        }
    }
}
