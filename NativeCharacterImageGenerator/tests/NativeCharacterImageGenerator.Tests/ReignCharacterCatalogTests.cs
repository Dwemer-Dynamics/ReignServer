using Bannerlord.NativeCharacterImageGenerator;

namespace NativeCharacterImageGenerator.Tests;

public sealed class ReignCharacterCatalogTests
{
    [Fact]
    public void MergeUsesTheExplicitReignCampaignInsteadOfTheNewestCampaign()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "ReignCharacterCatalogTests",
            Guid.NewGuid().ToString("N"));
        try
        {
            WriteCampaign(testRoot, "requested-campaign", "requested_hero");
            WriteCampaign(testRoot, "newest-campaign", "wrong_hero");
            string campaignsRoot = Path.Combine(
                testRoot, "Modules", "ReignBeta", "server", "app", "data", "campaigns");
            Directory.SetLastWriteTimeUtc(
                Path.Combine(campaignsRoot, "requested-campaign"),
                DateTime.UtcNow.AddHours(-1));
            Directory.SetLastWriteTimeUtc(
                Path.Combine(campaignsRoot, "newest-campaign"),
                DateTime.UtcNow);

            ReignCatalogResult result = ReignCharacterCatalog.Merge(
                testRoot,
                Array.Empty<NativeCharacterDefinition>(),
                "requested-campaign");

            Assert.Equal("requested-campaign", result.CampaignId);
            NativeCharacterDefinition character = Assert.Single(result.Characters);
            Assert.Equal("requested_hero", character.Id);
            Assert.Equal("requested-campaign", character.ReignCampaignId);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public void MergeUsesExplicitRuntimeDataRootInsteadOfLegacyPackagedData()
    {
        string testRoot = Path.Combine(
            Path.GetTempPath(),
            "ReignCharacterCatalogTests",
            Guid.NewGuid().ToString("N"));
        string runtimeDataRoot = Path.Combine(testRoot, "runtime-data");
        try
        {
            Directory.CreateDirectory(Path.Combine(testRoot, "Modules", "ReignBeta"));
            WriteCampaign(testRoot, "active-campaign", "legacy_hero");
            WriteCampaignAtDataRoot(runtimeDataRoot, "active-campaign", "runtime_hero");

            ReignCatalogResult result = ReignCharacterCatalog.Merge(
                testRoot,
                Array.Empty<NativeCharacterDefinition>(),
                "active-campaign",
                runtimeDataRoot);

            Assert.Equal("active-campaign", result.CampaignId);
            NativeCharacterDefinition character = Assert.Single(result.Characters);
            Assert.Equal("runtime_hero", character.Id);
            NativeEquipmentAssignment equipment = Assert.Single(character.CivilianEquipment);
            Assert.Equal("Body", equipment.Slot);
            Assert.Equal("test_tunic", equipment.ItemId);
        }
        finally
        {
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    private static void WriteCampaign(string gameRoot, string campaignId, string heroId)
    {
        string dataRoot = Path.Combine(
            gameRoot, "Modules", "ReignBeta", "server", "app", "data");
        WriteCampaignAtDataRoot(dataRoot, campaignId, heroId);
    }

    private static void WriteCampaignAtDataRoot(string dataRoot, string campaignId, string heroId)
    {
        string campaignRoot = Path.Combine(dataRoot, "campaigns", campaignId);
        Directory.CreateDirectory(campaignRoot);
        File.WriteAllText(Path.Combine(campaignRoot, "campaign.json"), "{}");
        File.WriteAllText(
            Path.Combine(campaignRoot, "portrait_roster.json"),
            "{\"characters\":[{\"heroStringId\":\"" + heroId
            + "\",\"name\":\"Test Hero\",\"civilianEquipment\":[{\"slot\":\"Body\",\"itemId\":\"test_tunic\"}]}]}");
    }
}
