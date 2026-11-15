using System;
using System.Linq;
using Reign.Relationships;

namespace Reign.Relationships.Tests;

public sealed class ArrangedMarriagePolicyTests
{
    [Fact]
    public void WeddingReceiptAcknowledgesOnlyTheSameTerminalStateAndBooking()
    {
        Assert.False(ArrangedMarriagePolicy.CanAcknowledgeWeddingReceipt(false, 1, "a", true, 2, "a"));
        Assert.False(ArrangedMarriagePolicy.CanAcknowledgeWeddingReceipt(true, 2, "a", true, 2, "b"));
        Assert.False(ArrangedMarriagePolicy.CanAcknowledgeWeddingReceipt(true, 2, "a", false, 1, "a"));
        Assert.True(ArrangedMarriagePolicy.CanAcknowledgeWeddingReceipt(true, 2, "a", true, 2, "a"));
        Assert.False(ArrangedMarriagePolicy.CanAcknowledgeWeddingReceipt(true, 2, "", true, 2, ""));
    }

    [Fact]
    public void ReceiptFeeMetadataHandlesMalformedAndOverflowingQuotesWithoutThrowing()
    {
        Assert.True(ArrangedMarriagePolicy.TryReceiptFee(new[] { "20000", "1000" }, out int fee));
        Assert.Equal(21000, fee);
        foreach (var input in new[] { new[] { "" }, new[] { "invalid" }, new[] { "-1" }, new[] { "2,000" },
            new[] { int.MaxValue.ToString(), "1" } })
        {
            Assert.False(ArrangedMarriagePolicy.TryReceiptFee(input, out fee));
            Assert.Equal(0, fee);
        }
    }
    [Theory]
    [InlineData(6, 1, 100000)] [InlineData(1, 6, 100000)]
    [InlineData(3, 3, 0)] [InlineData(1, 2, 20000)]
    public void FeeIsSymmetricAndHasNoTierGapVeto(int a, int b, int expected)
        => Assert.Equal(expected, ArrangedMarriagePolicy.Fee(a, b));

    [Fact]
    public void PeerPreferenceDoesNotAlterApproval()
    {
        Assert.Equal(45, ArrangedMarriagePolicy.Rank(25, 80, 3, 4, 1, 4));
        Assert.Equal(25, ArrangedMarriagePolicy.Rank(25, 80, 1, 6, 0, 4));
        Assert.Equal(25, ArrangedMarriagePolicy.Rank(25, 80, 3, 3, 0, 4));
    }
    [Theory]
    [InlineData(6.9, 0)] [InlineData(7, 1)] [InlineData(13.99, 1)] [InlineData(21, 3)]
    [InlineData(91076.9999, 13010)] [InlineData(91077.0001, 13011)]
    public void ExactWeeklyBoundary(double day, long expected)
        => Assert.Equal(expected, ArrangedMarriagePolicy.Period(day));

    [Theory]
    [InlineData(1, 1, true, false, true)]
    [InlineData(1, 1, true, true, false)]
    [InlineData(1, 1, false, false, false)]
    [InlineData(1, 2, true, false, false)]
    public void SkippedOrWrongPeriodResponseCannotDropCapturedWork(long expected,long observed,bool ok,bool skipped,bool acknowledged)
        => Assert.Equal(acknowledged,ArrangedMarriagePolicy.CanAcknowledge(expected,observed,ok,skipped));

    [Fact]
    public void CompetingPaymentsCannotOverdrawSameNativeWallet()
    {
        int payer=100000,payee=0;
        bool Run(string id)
        {
            bool married=false;
            return MarriagePaymentTransaction.Execute(new MarriagePaymentJournal{Id=id,Fee=80000},()=>married,()=>true,
                ()=>payer,()=>payee,n=>{payer-=n;payee+=n;},()=>married=true,()=>{});
        }
        Assert.True(Run("first"));Assert.False(Run("second"));
        Assert.Equal(20000,payer);Assert.Equal(80000,payee);
    }

    [Fact]
    public void RoyalChanceIsStableAndApproximatelyFivePercent()
    {
        int passed = 0;
        for (int i = 0; i < 100000; i++)
        {
            double roll = ArrangedMarriagePolicy.RoyalRoll("campaign", "royal" + i, 1090);
            Assert.Equal(roll, ArrangedMarriagePolicy.RoyalRoll("campaign", "royal" + i, 1090));
            if (roll < .05) passed++;
        }
        Assert.InRange(passed, 4700, 5300);
        Assert.True(ArrangedMarriagePolicy.RoyalAllowed("c", "h", false, 0, 1090));
        Assert.True(ArrangedMarriagePolicy.RoyalAllowed("c", "h", true, 1, 1090));
    }

    [Theory]
    [InlineData(100000, true)] [InlineData(60000, false)]
    public void IraQuoteRequiresActualHundredThousand(int cash, bool succeeds)
    {
        int payer = cash, payee = 5, calls = 0; bool spouses = false;
        var j = new MarriagePaymentJournal { Id = "ira", Fee = ArrangedMarriagePolicy.Fee(1, 6) };
        bool Apply() => MarriagePaymentTransaction.Execute(j, () => spouses, () => true,
            () => payer, () => payee, amount => { payer -= amount; payee += amount; },
            () => { calls++; spouses = true; }, () => { });
        Assert.Equal(succeeds, Apply());
        if (succeeds) { Assert.True(Apply()); Assert.Equal(1, calls); Assert.Equal(100005, payee); }
        else { Assert.Equal(0, calls); Assert.Equal(cash, payer); }
    }
    [Fact]
    public void FailedUnionRefundsOnlyTheFeeAndCannotReplay()
    {
        int payer = 100000, payee = 0;
        var j = new MarriagePaymentJournal { Id = "failure", Fee = 100000 };
        void Apply() => MarriagePaymentTransaction.Execute(j, () => false, () => true,
            () => payer, () => payee, amount => { payer -= amount; payee += amount; },
            () => throw new InvalidOperationException("native failed"), () => { });
        Assert.Throws<InvalidOperationException>(Apply);
        Assert.Equal("refunded", j.State); Assert.Equal(100000, payer); Assert.Equal(0, payee);
        Assert.Throws<InvalidOperationException>(Apply);
    }
    [Fact]
    public void PartiallyEstablishedMarriageIsNeverRefundedOrRepeated()
    {
        int payer = 100000, payee = 0; bool married = false;
        var j = new MarriagePaymentJournal { Fee = 100000 };
        Assert.Throws<InvalidOperationException>(() => MarriagePaymentTransaction.Execute(j,
            () => married, () => true, () => payer, () => payee,
            amount => { payer -= amount; payee += amount; },
            () => { married = true; throw new InvalidOperationException("callback failed"); }, () => { }));
        Assert.Equal("recovery", j.State); Assert.Equal(0, payer); Assert.Equal(100000, payee);
    }
}
