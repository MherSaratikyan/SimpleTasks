using SimpleTasks.Contracts;
using SimpleTasks.Work.Domain;
using Xunit;

namespace SimpleTasks.UnitTests;

public class JiraDurationTests
{
    [Theory]
    [InlineData("45m", 45)]
    [InlineData("2h", 120)]
    [InlineData("1d", 480)]
    [InlineData("1w", 2400)]
    [InlineData("1d 4h", 720)]
    [InlineData("1w2d3h4m", 3544)]
    [InlineData(" 2H ", 120)]
    public void Parses_jira_format(string text, int minutes)
    {
        Assert.True(JiraDuration.TryParse(text, out var parsed));
        Assert.Equal(minutes, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("0h")]
    [InlineData("soon")]
    [InlineData("2x")]
    [InlineData("4h 1d")]
    [InlineData("99999999w")]
    public void Rejects_anything_else(string? text) => Assert.False(JiraDuration.TryParse(text, out _));

    [Theory]
    [InlineData(0, "0m")]
    [InlineData(45, "45m")]
    [InlineData(65, "1h 5m")]
    [InlineData(480, "1d")]
    [InlineData(3544, "1w 2d 3h 4m")]
    public void Formats_back(int minutes, string text) => Assert.Equal(text, JiraDuration.Format(minutes));
}

public class EstimateRulesTests
{
    [Theory]
    [InlineData(null, 500, EstimateAlertLevel.None)]
    [InlineData(100, 79, EstimateAlertLevel.None)]
    [InlineData(100, 80, EstimateAlertLevel.Warning)]
    [InlineData(100, 100, EstimateAlertLevel.Warning)]
    [InlineData(100, 101, EstimateAlertLevel.Exceeded)]
    public void Levels_follow_the_thresholds(int? estimate, int logged, EstimateAlertLevel level) =>
        Assert.Equal(level, EstimateRules.LevelFor(estimate, logged));

    [Fact]
    public void Alert_fires_once_per_level()
    {
        var first = EstimateRules.Evaluate(EstimateAlertLevel.None, 100, 85);
        Assert.Equal(EstimateAlertLevel.Warning, first.Alert);

        var repeat = EstimateRules.Evaluate(first.Stored, 100, 90);
        Assert.Null(repeat.Alert);

        var exceeded = EstimateRules.Evaluate(repeat.Stored, 100, 120);
        Assert.Equal(EstimateAlertLevel.Exceeded, exceeded.Alert);
    }

    [Fact]
    public void Jumping_straight_past_the_estimate_sends_only_exceeded()
    {
        var result = EstimateRules.Evaluate(EstimateAlertLevel.None, 60, 90);
        Assert.Equal(EstimateAlertLevel.Exceeded, result.Alert);
    }

    [Fact]
    public void Raising_the_estimate_lets_alerts_fire_again()
    {
        var raised = EstimateRules.Evaluate(EstimateAlertLevel.Exceeded, 1000, 120);
        Assert.Equal(EstimateAlertLevel.None, raised.Stored);
        Assert.Null(raised.Alert);

        Assert.Equal(EstimateAlertLevel.Warning, EstimateRules.Evaluate(raised.Stored, 1000, 800).Alert);
    }

    [Fact]
    public void Removing_the_estimate_clears_the_level() =>
        Assert.Equal(EstimateAlertLevel.None, EstimateRules.Evaluate(EstimateAlertLevel.Warning, null, 500).Stored);
}

public class ProjectAccessTests
{
    [Theory]
    [InlineData(ProjectRole.Viewer, true, false, false)]
    [InlineData(ProjectRole.Member, true, true, false)]
    [InlineData(ProjectRole.Admin, true, true, true)]
    [InlineData(null, false, false, false)]
    public void Roles_grant_the_agreed_rights(ProjectRole? role, bool view, bool edit, bool administer)
    {
        Assert.Equal(view, ProjectAccess.CanView(role));
        Assert.Equal(edit, ProjectAccess.CanEdit(role));
        Assert.Equal(administer, ProjectAccess.CanAdminister(role));
    }

    [Fact]
    public void Site_admins_act_as_project_admin_without_a_membership() =>
        Assert.Equal(ProjectRole.Admin, ProjectAccess.EffectiveRole(null, isSiteAdmin: true));

    [Fact]
    public void Non_members_have_no_role() => Assert.Null(ProjectAccess.EffectiveRole(null, isSiteAdmin: false));
}

public class TicketKeyAndRankingTests
{
    [Theory]
    [InlineData("ST", true)]
    [InlineData("WEB2", true)]
    [InlineData("S", false)]
    [InlineData("2WEB", false)]
    [InlineData("web", false)]
    [InlineData("TOOLONGPROJECT", false)]
    public void Project_keys(string key, bool valid) => Assert.Equal(valid, TicketKey.IsValidProjectKey(key));

    [Fact]
    public void Ticket_keys_round_trip()
    {
        Assert.True(TicketKey.TryParse("st-12", out var project, out var number));
        Assert.Equal(("ST", 12), (project, number));
        Assert.False(TicketKey.TryParse("nonsense", out _, out _));
    }

    [Fact]
    public void Ranks_fall_between_neighbours()
    {
        Assert.Equal(150, Ranking.Between(100, 200));
        Assert.True(Ranking.Between(null, 100) < 100);
        Assert.True(Ranking.Between(100, null) > 100);
    }
}
