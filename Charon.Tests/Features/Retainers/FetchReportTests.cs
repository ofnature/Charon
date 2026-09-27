using System.Text.Json;
using Charon.Features.Retainers;

namespace Charon.Tests.Features.Retainers;

public sealed class FetchReportTests
{
    private static JsonElement Parse(FetchReport report) => JsonDocument.Parse(report.ToJson()).RootElement;

    [Theory]
    [InlineData(FetchState.Idle, "idle")]
    [InlineData(FetchState.WaitingForPerson, "waitingForPerson")]
    [InlineData(FetchState.Moving, "moving")]
    [InlineData(FetchState.Done, "done")]
    [InlineData(FetchState.Refused, "refused")]
    public void StateIsTheOneFieldACallerBranchesOn(FetchState state, string expected)
    {
        var json = Parse(FetchReport.Idle with { State = state });
        Assert.Equal(expected, json.GetProperty("state").GetString());
    }

    [Fact]
    public void WaitingForAPersonIsDistinctFromMoving()
    {
        // The whole reason this gate exists: a caller must be able to pause its queue and say
        // "someone has to walk to a bell" without reading prose.
        Assert.NotEqual(
            Parse(FetchReport.Idle with { State = FetchState.WaitingForPerson }).GetProperty("state").GetString(),
            Parse(FetchReport.Idle with { State = FetchState.Moving }).GetProperty("state").GetString());
    }

    [Fact]
    public void MovedIsBothQualitiesTogether()
    {
        var report = new FetchReport(FetchState.Done, "T'sola", 5106, 12, 8, 4, 0, "done");
        Assert.Equal(12, report.Moved);
        Assert.Equal(12, Parse(report).GetProperty("moved").GetInt32());
    }

    [Fact]
    public void ThePayloadCarriesTheDetailAConsumerLogs()
    {
        var json = Parse(new FetchReport(FetchState.Moving, "T'sala", 5106, 12, 3, 0, 2, "moving a stack"));

        Assert.Equal("T'sala", json.GetProperty("retainer").GetString());
        Assert.Equal(5106u, json.GetProperty("itemId").GetUInt32());
        Assert.Equal(12, json.GetProperty("wanted").GetInt32());
        Assert.Equal(3, json.GetProperty("movedNq").GetInt32());
        Assert.Equal(2, json.GetProperty("queued").GetInt32());
        Assert.Equal("moving a stack", json.GetProperty("reason").GetString());
    }
}
