using Reign.Relationships;
using ReignBeta.Shared;

namespace Reign.Relationships.Tests;

public sealed class RelationshipCompatibilityPolicyTests
{
    [Fact]
    public void ContainsEveryDirectionalCompatibilityScore()
    {
        var scores = RelationshipCompatibilityPolicy.AllBaseScores();

        Assert.Equal(256, scores.Count);
        Assert.Equal(46, scores.Min());
        Assert.Equal(93, scores.Max());
        Assert.Equal(91,
            RelationshipCompatibilityPolicy.BaseScore("ENTJ", "INTJ"));
        Assert.Equal(88,
            RelationshipCompatibilityPolicy.BaseScore("INTJ", "ENTJ"));
    }

    [Theory]
    [InlineData(46, 19)]
    [InlineData(54, 30)]
    [InlineData(62, 40)]
    [InlineData(70, 51)]
    [InlineData(93, 81)]
    public void PreservesApprovedAdjustmentAnchors(int input, int expected)
    {
        Assert.Equal(expected,
            RelationshipCompatibilityPolicy.AdjustedScore(input));
    }

    [Fact]
    public void UnknownPersonalityUsesStableFallback()
    {
        Assert.Equal(60,
            RelationshipCompatibilityPolicy.BaseScore("XXXX", "INTJ"));
    }

    [Theory]
    [InlineData(true, true, true, 50)]
    [InlineData(false, true, true, 20)]
    [InlineData(false, false, true, 10)]
    [InlineData(false, false, false, int.MinValue)]
    public void BaselinePrecedenceIsStable(
        bool spouses,
        bool family,
        bool lordRuler,
        int expected)
    {
        Assert.Equal(expected,
            ReignRelationshipBaselinePolicy.ResolveStartingBaseline(
                spouses, family, lordRuler));
    }
}
