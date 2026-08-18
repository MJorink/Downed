using System.Runtime.CompilerServices;

namespace Downed
{
    internal static class FusionCompat
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool IsModAllowed()
        {
            if (!LabFusion.Network.NetworkInfo.HasServer) return true;
            if (LabFusion.SDK.Gamemodes.GamemodeManager.ActiveGamemode != null) return false;

            return !LabFusion.Preferences.Server.SavedServerSettings.Knockout.Value;
        }
    }
}
