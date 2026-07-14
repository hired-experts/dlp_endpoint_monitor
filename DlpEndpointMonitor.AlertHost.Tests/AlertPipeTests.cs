using DlpEndpointMonitor.AlertContracts;
using Xunit;

namespace DlpEndpointMonitor.AlertHost.Tests;

// Covers AlertPipe.NameFor's pure string-formatting logic only - the actual session-crossing
// named-pipe behavior it fixes (AGENTS.md section 10, the Fast-User-Switching stale-owner bug)
// requires real multi-session Windows state and is not unit-testable, same reasoning as every
// other Win32-adjacent function in this repo (ai_agent_doc/TEST-PLAN.md section 1).
public class AlertPipeTests
{
    // T-AP-01: two different session ids never collide on the same pipe name - the whole point
    // of scoping the name per session.
    [Fact]
    public void NameFor_DifferentSessionIds_ProduceDifferentNames()
    {
        string first = AlertPipe.NameFor(1);
        string second = AlertPipe.NameFor(2);

        Assert.NotEqual(first, second);
    }

    // T-AP-02: the same session id always maps to the same name, so a client and the server it's
    // trying to reach agree on one pipe without any shared mutable state.
    [Fact]
    public void NameFor_SameSessionId_IsDeterministic()
    {
        string first = AlertPipe.NameFor(7);
        string second = AlertPipe.NameFor(7);

        Assert.Equal(first, second);
    }
}
