using TaleWorlds.Core;
using TaleWorlds.MountAndBlade.CustomBattle;

namespace Bannerlord.NativePortraitRenderer
{
    /// <summary>
    /// Loads Bannerlord's lightweight CustomGame object registry without opening
    /// the Custom Battle screen or creating a campaign/save.
    /// </summary>
    internal sealed class PortraitGameManager : CustomGameManager
    {
        public override void OnAfterCampaignStart(Game game)
        {
            // CustomGame calls this lifecycle method even though it is not a campaign.
            // The stock manager initializes multiplayer here, which a portrait worker
            // neither needs nor should start.
        }

        public override void OnLoadFinished()
        {
            // Do not push CustomBattleState. The native tableau is our only consumer.
            IsLoaded = true;
        }
    }
}
