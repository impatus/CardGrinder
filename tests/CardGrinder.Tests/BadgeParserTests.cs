using CardGrinder.Core;
using Xunit;

namespace CardGrinder.Tests;

public sealed class BadgeParserTests
{
    private static string Row(uint id, string count, string suffix = "_0_0") => $"""
        <div class="badge_row is_link"><a class="badge_row_overlay" href="/profiles/1/gamecards/{id}/"></a><div class="badge_row_inner">
        <div class="badge_title_stats_content"><div class="card_drop_info_dialog" id="card_drop_info_gamebadge_{id}{suffix}"></div>
        <span class="progress_info_bold">{count}</span></div></div></div>
        """;

    [Fact]
    public void ParsesNormalFoilAndCommunityBadgesWithoutMixingCounts()
    {
        var page = BadgeParser.Parse("<div class='badges_sheet'>" + Row(400, "3 card drops remaining") + Row(400, "Unknown foil text", "_1_1") + Row(10, "No card drops remaining") + "<div class='badge_row_inner'>Community badge</div></div><a class='pagelink'>3</a>");
        Assert.Equal(3, page.Pages); Assert.Equal(3, page.Drops[400]); Assert.Equal(0, page.Drops[10]); Assert.Equal(2, page.Drops.Count);
    }
    [Theory]
    [InlineData("1 card drop remaining", 1)]
    [InlineData("5 card drops remaining", 5)]
    [InlineData("No card drops remaining", 0)]
    [InlineData("\n 2  card drops remaining \t", 2)]
    public void OnlyUnderstoodCountsAreAccepted(string text, int count) => Assert.Equal(count, BadgeParser.ParseRemaining(text));

    [Theory]
    [InlineData("Steam is busy")]
    [InlineData("3 cards owned")]
    [InlineData("")]
    [InlineData("1001 card drops remaining")]
    [InlineData("-1 card drops remaining")]
    public void UnknownTextNeverMeansCompleted(string text) => Assert.Throws<InvalidDataException>(() => BadgeParser.ParseRemaining(text));

    [Fact] public void LoginResponseIsRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse("<input id='steamAccountName'><div class='badges_sheet'></div>"));
    [Fact] public void PublicBadgePageWithoutPrivateCountsIsRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse("<div class='badge_row_inner'><a href='/id/test/gamecards/400/'>Portal</a></div>"));
    [Fact] public void PublicPageWithRealSiblingOverlayIsRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse("<div class='badges_sheet'><div class='badge_row is_link'><a class='badge_row_overlay' href='/id/test/gamecards/400/'></a><div class='badge_row_inner'><div class='badge_title_stats_content'></div></div></div></div>"));
    [Theory]
    [InlineData("_0_0")]
    [InlineData("_1_0")]
    [InlineData("_5_0")]
    public void CraftedLevelAndBorderFieldsAreParsedSeparately(string suffix) => Assert.Equal(2, BadgeParser.Parse(Row(400, "2 card drops remaining", suffix)).Drops[400]);
    [Fact] public void FoilRowNeverOverridesNormalDropCounts() => Assert.Equal(3, BadgeParser.Parse(Row(400, "3 card drops remaining", "_5_0") + Row(400, "No card drops remaining", "_1_1")).Drops[400]);
    [Fact] public void UnknownIdentifierFormatFailsRatherThanReportingAnEmptyLibrary() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse(Row(400, "3 card drops remaining", "_unknown")));
    [Fact]
    public void CraftedBadgeWithoutDialogDoesNotHideKnownAvailableDrops()
    {
        string crafted = "<div class='badge_row'><a class='badge_row_overlay' href='/id/test/gamecards/10/'></a><div class='badge_row_inner'>Crafted badge</div></div>";
        var parsed = BadgeParser.Parse(crafted + Row(400, "3 card drops remaining", "_0_0"));
        Assert.Equal(3, parsed.Drops[400]); Assert.False(parsed.Drops.ContainsKey(10));
    }
    [Fact]
    public void APageWithoutDialogsCanBeValidatedAlongsideOtherPages()
    {
        string crafted = "<div class='badge_row'><a class='badge_row_overlay' href='/id/test/gamecards/10/'></a><div class='badge_row_inner'>Crafted badge</div></div>";
        var first = BadgeParser.Parse(Row(400, "3 card drops remaining"), allowMissingDropData: true);
        var second = BadgeParser.Parse(crafted, allowMissingDropData: true);
        Assert.True(second.HasGameBadges); Assert.Empty(second.Drops);
        BadgeParser.ValidateDropVisibility(first.HasGameBadges || second.HasGameBadges, first.Drops.Count + second.Drops.Count);
        Assert.Throws<InvalidDataException>(() => BadgeParser.ValidateDropVisibility(second.HasGameBadges, second.Drops.Count));
    }
    [Fact] public void ConflictingRowsAreRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse(Row(400, "3 card drops remaining") + Row(400, "2 card drops remaining")));
    [Fact] public void ChangedMarkupIsRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse("<h1>Service unavailable</h1>"));
    [Fact] public void EmptyValidatedSheetIsAllowed() => Assert.Empty(BadgeParser.Parse("<div class='badges_sheet'></div>").Drops);
    [Fact] public void ExcessivePaginationIsRejected() => Assert.Throws<InvalidDataException>(() => BadgeParser.Parse("<div class='badges_sheet'></div><a class='pagelink'>201</a>"));
    [Theory]
    [InlineData("pagelink active")]
    [InlineData("page_current")]
    public void CurrentLastPageSpanIsIncludedInTheTotalPageCount(string currentClass) => Assert.Equal(3, BadgeParser.Parse($"<div class='badges_sheet'></div><a class='pagelink'>1</a><a class='pagelink'>2</a><span class='{currentClass}'>3</span>").Pages);
}
