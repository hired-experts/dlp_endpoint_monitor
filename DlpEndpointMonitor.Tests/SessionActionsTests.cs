using DlpEndpointMonitor.Actions;
using Xunit;

namespace DlpEndpointMonitor.Tests;

/// <summary>
/// SESSION-USER-EVENT-DESIGN.md: SessionActions.FormatSessionUser - the pure domain/username
/// combination rule factored out of the Win32-calling GetCurrentSessionUser/ResolveSessionUser
/// (same precedent as UsbMonitor.DecideGroupBlock/StartupConflictResolver.Resolve). Confirms the
/// "DOMAIN\User" convention and the edge cases a live WTSQuerySessionInformation call can't be
/// unit-tested against directly: an empty username (logon screen, no interactive user resolved
/// yet) must report null regardless of what domain came back.
/// </summary>
public class SessionActionsTests
{
    [Fact]
    public void FormatSessionUser_DomainAndUsername_ReturnsCombined()
    {
        Assert.Equal("CONTOSO\\alice", SessionActions.FormatSessionUser("CONTOSO", "alice"));
    }

    [Fact]
    public void FormatSessionUser_EmptyDomain_ReturnsBareUsername()
    {
        Assert.Equal("alice", SessionActions.FormatSessionUser("", "alice"));
    }

    [Fact]
    public void FormatSessionUser_NullDomain_ReturnsBareUsername()
    {
        Assert.Equal("alice", SessionActions.FormatSessionUser(null, "alice"));
    }

    [Fact]
    public void FormatSessionUser_EmptyUsername_ReturnsNullRegardlessOfDomain()
    {
        Assert.Null(SessionActions.FormatSessionUser("CONTOSO", ""));
    }

    [Fact]
    public void FormatSessionUser_NullUsername_ReturnsNullRegardlessOfDomain()
    {
        Assert.Null(SessionActions.FormatSessionUser("CONTOSO", null));
    }

    [Fact]
    public void FormatSessionUser_BothEmpty_ReturnsNull()
    {
        Assert.Null(SessionActions.FormatSessionUser("", ""));
    }

    [Fact]
    public void ResolveSessionUser_NullSessionId_ReturnsOkTrueWithNulls()
    {
        var (ok, sessionId, username) = SessionActions.ResolveSessionUser(null);

        Assert.True(ok);
        Assert.Null(sessionId);
        Assert.Null(username);
    }
}
