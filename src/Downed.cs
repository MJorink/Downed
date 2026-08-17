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
        MelonPreferences_Category category;
        MelonPreferences_Entry<bool> EnableModEntry;
        MelonPreferences_Entry<float> KnockedDurationEntry;

        bool downed = false;
        bool death = false;
        float startTime = 0f;
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
            defaultPage.CreateFloat("Knocked Duration", Color.yellow, KnockedDurationEntry.Value, 1f, 1f, 10f, (a) => { KnockedDurationEntry.Value = a;});

            defaultPage.CreateFunction("Save Settings", Color.cyan, () => { MelonPreferences.Save(); });                   
        }

        private void SetupMelonPreferences()
        {
            category = MelonPreferences.CreateCategory("Downed");

            EnableModEntry = category.CreateEntry("Enable Mod", true);
            KnockedDurationEntry = category.CreateEntry("Knocked Duration", 5f);

            MelonPreferences.Save();
            category.SaveToFile();
        }

        private void OnLevelLoaded(LevelInfo levelInfo)
        {
            downed = false;
            death = false;
        }

        public override void OnUpdate()
        {
            if (!EnableModEntry.Value) return;
            
            var rig = Player.RigManager;

            // Make sure the phys rig exists
            if (rig && !rig.activeSeat && !UIRig.Instance.popUpMenu.m_IsCursorShown) 
            {
                // Toggle ragdoll
                if (downed)
                {
                    var physRig = Player.PhysicsRig;

                    bool isRagdolled = physRig.torso.shutdown || !physRig.ballLocoEnabled;

                    if (!isRagdolled)
                    {
                        RagdollPlayerMod.RagdollRig(rig);
                    }

                    bool isShutdown = physRig.shutdown;

                    if (death && !isShutdown)
                    {
                        physRig.ShutdownRig();
                    }
                }
            }

            // Timer to unragdoll
            if (downed && !death)
            {
                if (Time.time - startTime >= KnockedDurationEntry.Value)
                {
                    downed = false;
                    RagdollPlayerMod.UnragdollRig(rig);
                }
            }

            // Force unragdoll in case of bug
            var controller = GetController();
            if (!controller) return;
            bool input = GetInput(controller);

            if (input)
            {
                var physRig = Player.PhysicsRig;
                bool isRagdolled = physRig.torso.shutdown || !physRig.ballLocoEnabled;
                
                if (isRagdolled)
                {
                    downed = false;
                    death = false;
                    RagdollPlayerMod.UnragdollRig(rig);
                }
            }
        }

        private static BaseController GetController()
        {
            return Player.RightController;
        }
        
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
                else
                {
                    ragdollNextButton = false;
                    lastTimeInput = 0f;
                }
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

        private void OnPlayerDamageReceived(Il2CppSLZ.Marrow.RigManager rigManager, float damage)
        {
            if (!EnableModEntry.Value) return;
            if (Player.RigManager.health.curr_Health > 0f) return;
            
            // If already downed, set to death.
            if (downed)
            {
                death = true;
                Player.RigManager.health.Dying(5);
            }
            
            // If not downed or dead, set to downed.
            if (!downed && !death)
            {
                downed = true;
                Revive((Player_Health)Player.RigManager.health);
                startTime = Time.time;
            }
        }

        private void OnPlayerDeath(Il2CppSLZ.Marrow.RigManager rigManager)
        {
            if (!EnableModEntry.Value) return;
            // Reset states and unragdoll
            var rig = Player.RigManager;

            downed = false;
            death = false;
            RagdollPlayerMod.UnragdollRig(rig);
            
            // Fix flinging on respawn in Fusion lobbies
            var physRig = Player.PhysicsRig;
            var teleport = physRig.feet.transform.position + new Vector3(0, 0.25f, 0);
            
            rig.Teleport(teleport);
        }

        private static void Revive(Player_Health health)
        {
        	if (!health) return;

        	health.LifeSavingDamgeDealt();
        }
    }
}
