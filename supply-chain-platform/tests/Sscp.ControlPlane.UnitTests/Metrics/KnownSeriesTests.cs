// The alert rules count increases of Control Plane counters. A series that appears for the
// first time with its first event would never raise an alert, so the series the alerts
// watch must already exist, at 0, from start-up (ControlPlaneMetrics.PublishKnownSeries).
using System.Diagnostics.Metrics;
using Sscp.ControlPlane.Application.Metrics;

namespace Sscp.ControlPlane.UnitTests.Metrics;

public sealed class KnownSeriesTests
{
    [Theory]
    [InlineData("sscp_trust_decisions", "outcome=Fail", "scope=release")]
    [InlineData("sscp_trust_decisions", "outcome=Fail", "scope=source")]
    [InlineData("sscp_builds_completed", "kind=MainBranch", "status=failed")]
    [InlineData("sscp_builds_completed", "kind=DeploymentChange", "status=failed")]
    [InlineData("sscp_scanner_failures", "kind=VulnerabilityScan", null)]
    [InlineData("sscp_deployments", "result=mismatch", null)]
    public void Series_watched_by_alerts_are_published_at_zero(string name, string firstTag, string? secondTag)
    {
        var published = new List<(string Name, long Value, string[] Tags)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ControlPlaneMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            lock (published)
            {
                published.Add((instrument.Name, value, tags.ToArray().Select(t => $"{t.Key}={t.Value}").ToArray()));
            }
        });
        listener.Start();

        ControlPlaneMetrics.PublishKnownSeries();

        var expected = secondTag is null ? [firstTag] : new[] { firstTag, secondTag };
        Assert.Contains(published, p => p.Name == name && p.Value == 0 && expected.All(p.Tags.Contains));
    }
}
