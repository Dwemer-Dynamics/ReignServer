using System.Text.Json;
using Xunit;

namespace Bannerlord.NativeCharacterImageGenerator.Tests;

public class PortraitSourceCharacterTests
{
    [Fact]
    public void ExplicitFullOutfitProfilePreservesTavernClothesWithoutInventingAResident()
    {
        using var original = Snapshot("[{\"slot\":\"Body\",\"itemId\":\"empire_dress\"},{\"slot\":\"Leg\",\"itemId\":\"ladys_shoe\"}]");
        var node = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!.AsObject();
        node["portraitSourceProfile"] = AiSourceCacheContract.ResidentOutfitRenderContractVersion;
        node["age"] = 24;
        using var data = JsonDocument.Parse(node.ToJsonString());
        var person = PortraitSourceCharacter.FromSnapshot(data.RootElement, "campaign-a", "new-hero");
        Assert.False(data.RootElement.TryGetProperty("encounteredResident", out _));
        Assert.True(person.PreserveEncounteredOutfit);
        Assert.Equal(AiSourceCacheContract.ResidentFullOutfit, PortraitSourceCharacter.RenderProfile(person));
        Assert.Equal(768, PortraitSourceCharacter.RenderProfile(person).OutputWidth);
        Assert.Equal(1024, PortraitSourceCharacter.RenderProfile(person).OutputHeight);
        Assert.Equal(1d, PortraitSourceCharacter.RenderProfile(person).CropScale);
        Assert.Equal("+6-empire_dress-@null+7-ladys_shoe-@null", NativeEngineRenderService.BuildEquipmentCode(person.CivilianEquipment, true));
        Assert.Equal("FACE-KEY", person.BodyKey);
        Assert.Equal(.25f, person.Weight);
        Assert.Equal(.7f, person.Build);
        Assert.Equal(24f, person.Age);
    }

    [Theory]
    [InlineData("\"unsupported-profile\"")]
    [InlineData("42")]
    public void InvalidExplicitProfileFailsInsteadOfSilentlyCropping(string profile)
    {
        using var original = Snapshot();
        string json = original.RootElement.GetRawText().TrimEnd('}') + ",\"portraitSourceProfile\":" + profile + "}";
        using var data = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => PortraitSourceCharacter.FromSnapshot(data.RootElement, "campaign-a", "new-hero"));
    }

    [Fact]
    public void ResidentOutfitRetainsCurrentHeadwearModifiersAndDyesOnEveryPortrait()
    {
        using var original = Snapshot("[{\"slot\":\"Head\",\"itemId\":\"guard_helmet\",\"modifierId\":\"fine\"},{\"slot\":\"Body\",\"itemId\":\"work_coat\"}]");
        var node = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!.AsObject();
        node["encounteredResident"] = new System.Text.Json.Nodes.JsonObject {
            ["schema"] = "reign-encountered-resident-v1", ["initialPortraitCompleted"] = false };
        node["clothingColor1"] = 0xFF846E54u; node["clothingColor2"] = 0xFF293347u;
        using var initial = JsonDocument.Parse(node.ToJsonString());
        var character = PortraitSourceCharacter.FromSnapshot(initial.RootElement, "campaign-a", "new-hero");
        Assert.True(character.PreserveEncounteredOutfit);
        Assert.Equal(0xFF846E54u, character.ClothingColor1);
        Assert.Contains("+5-guard_helmet-fine", NativeEngineRenderService.BuildEquipmentCode(character.CivilianEquipment, character.PreserveEncounteredOutfit));
        Assert.Contains("+6-work_coat-@null", NativeEngineRenderService.BuildEquipmentCode(character.CivilianEquipment, true));
        node["encounteredResident"]!["initialPortraitCompleted"] = true;
        node["civilianEquipment"]![1]!["itemId"] = "new_work_dress";
        using var later = JsonDocument.Parse(node.ToJsonString());
        var laterCharacter = PortraitSourceCharacter.FromSnapshot(later.RootElement, "campaign-a", "new-hero");
        Assert.True(laterCharacter.PreserveEncounteredOutfit);
        string laterCode = NativeEngineRenderService.BuildEquipmentCode(laterCharacter.CivilianEquipment, laterCharacter.PreserveEncounteredOutfit);
        Assert.Contains("guard_helmet", laterCode);
        Assert.Contains("new_work_dress", laterCode);
        Assert.DoesNotContain("work_coat", laterCode);
        Assert.Equal(AiSourceCacheContract.ResidentFullOutfit, PortraitSourceCharacter.RenderProfile(character));
        Assert.Equal(1d, PortraitSourceCharacter.RenderProfile(laterCharacter).CropScale);
        Assert.NotEqual(AiSourceCacheContract.DefaultProfile.RenderContractHash, PortraitSourceCharacter.RenderProfile(character).RenderContractHash);
        Assert.Empty(PortraitSourceCharacter.WithRenderableClothing(character with { CivilianEquipment = [] }).CivilianEquipment);
    }
    [Fact]
    public void LegacyProfilePercentUnitsAreExplicitNotGuessed()
    {
        using var data = JsonDocument.Parse("{\"bodyWeight\":1,\"bodyBuild\":74}");
        Assert.Equal(.01f, ReignCharacterCatalog.RosterUnit(data.RootElement, "bodyWeight", "weight", .5f, true));
        Assert.Equal(.74f, ReignCharacterCatalog.RosterUnit(data.RootElement, "bodyBuild", "build", .5f, true));
        Assert.Equal(1f, ReignCharacterCatalog.RosterUnit(data.RootElement, "bodyWeight", "weight", .5f));
        Assert.Throws<InvalidDataException>(() => ReignCharacterCatalog.RosterUnit(data.RootElement, "bodyBuild", "build", .5f));
    }
    [Theory]
    [InlineData("-0.01")]
    [InlineData("1.01")]
    [InlineData("74")]
    [InlineData("null")]
    [InlineData("\"0.5\"")]
    public void SuppliedPhysiqueRejectsInvalidValuesAndPercentages(string value)
    {
        using var data = JsonDocument.Parse("{\"bodyWeight\":" + value + "}");
        Assert.Throws<InvalidDataException>(() => PortraitSourceCharacter.Unit(data.RootElement, "bodyWeight"));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.4064667f)]
    [InlineData(1f)]
    public void NativeUnitsRemainUnscaled(float value)
    {
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(new { bodyWeight = value }));
        Assert.Equal(value, PortraitSourceCharacter.Unit(data.RootElement, "bodyWeight"));
    }

    [Fact]
    public void MissingStatsHaveExplicitDefaultProvenance()
    {
        using var original = Snapshot();
        var node = System.Text.Json.Nodes.JsonNode.Parse(original.RootElement.GetRawText())!.AsObject();
        node.Remove("bodyWeight"); node.Remove("bodyBuild");
        using var data = JsonDocument.Parse(node.ToJsonString());
        var character = PortraitSourceCharacter.FromSnapshot(data.RootElement, "campaign-a", "new-hero");
        Assert.Equal("generator_default_0.5", character.WeightSource);
        Assert.Equal("generator_default_0.5", character.BuildSource);
        Assert.Equal(.5f, character.Weight);
    }
    private static JsonDocument Snapshot(string equipment = "[]") => JsonDocument.Parse(
        "{\"schema\":\"reign-native-portrait-snapshot-v1\",\"campaignId\":\"campaign-a\",\"heroStringId\":\"new-hero\","
        + "\"characterObjectId\":\"CharacterObject_123\",\"name\":\"Fresh character\",\"bodyKey\":\"FACE-KEY\","
        + "\"age\":47,\"bodyWeight\":0.25,\"bodyBuild\":0.7,\"cultureId\":\"battania\",\"isFemale\":true,\"civilianEquipment\":" + equipment + "}");

    [Fact]
    public void NewHeroNeedsNoCatalogOrCapturedImageAndRetainsAppearance()
    {
        using var data = Snapshot();
        var result = PortraitSourceCharacter.FromSnapshot(data.RootElement, "campaign-a", "new-hero");
        Assert.Equal("FACE-KEY", result.BodyKey);
        Assert.Equal(47f, result.Age);
        Assert.Equal(.25f, result.Weight);
        Assert.Equal("battania", result.Culture);
        Assert.Equal("CharacterObject_123", result.CharacterObjectId);
        Assert.Contains(result.CivilianEquipment, x => x.Slot == "Body");
        Assert.Empty(result.CharacterCode);
    }

    [Fact]
    public void CurrentCivilianClothingIsRetained()
    {
        using var data = Snapshot("[{\"slot\":\"Body\",\"itemId\":\"custom_mod_outfit\"}]");
        var result = PortraitSourceCharacter.FromSnapshot(data.RootElement, "campaign-a", "new-hero");
        Assert.Equal("custom_mod_outfit", Assert.Single(result.CivilianEquipment).ItemId);
        Assert.Equal(AiSourceCacheContract.DefaultProfile, PortraitSourceCharacter.RenderProfile(result));
    }

    [Theory]
    [InlineData("campaign-b", "new-hero")]
    [InlineData("campaign-a", "different-hero")]
    public void CrossCampaignOrCharacterSnapshotsFailClosed(string campaign, string hero)
    {
        using var data = Snapshot();
        Assert.Throws<InvalidDataException>(() => PortraitSourceCharacter.FromSnapshot(data.RootElement, campaign, hero));
    }

    [Fact]
    public void MissingFaceFailsBeforeRendering()
    {
        using var original = Snapshot();
        using var missing = JsonDocument.Parse(original.RootElement.GetRawText().Replace("FACE-KEY", ""));
        Assert.Throws<InvalidDataException>(() => PortraitSourceCharacter.FromSnapshot(missing.RootElement, "campaign-a", "new-hero"));
    }
}
