using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;

namespace VoiceJournal.Tests.Core.Sessions;

public sealed class SessionPathsTests
{
    [Fact]
    public void SessionDirectory_UsesUtcYearMonthAndId()
    {
        var id = Guid.Parse("0192f0a1-0000-7000-8000-000000000001");
        var started = new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(9)); // 2026-10-31 15:30 UTC
        var dir = SessionPaths.SessionDirectory(@"C:\ws", id, started);
        Assert.Equal(Path.Combine(@"C:\ws", "sessions", "2026", "10", id.ToString("D")), dir);
    }

    [Fact]
    public void ChunkNames_FollowSpecLayout()
    {
        Assert.Equal("audio/mic/000001.wav", SessionPaths.ChunkFile(AudioSource.Mic, 1));
        Assert.Equal("audio/loopback/000012.partial", SessionPaths.PartialChunkFile(AudioSource.Loopback, 12));
        Assert.Equal("audio/mic/000003.recovered.wav", SessionPaths.RecoveredChunkFile(AudioSource.Mic, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => SessionPaths.ChunkFile(AudioSource.Mic, 0));
    }

    [Fact]
    public void Resolve_StaysInsideSessionFolder()
    {
        var full = SessionPaths.Resolve(@"C:\ws\sessions\s1", "audio/mic/000001.wav");
        Assert.Equal(@"C:\ws\sessions\s1\audio\mic\000001.wav", full);
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("audio/../../x.wav")]
    [InlineData("audio//x.wav")]
    [InlineData("./x.wav")]
    [InlineData(@"\\server\share\x.wav")]
    [InlineData("CON:")]
    public void Resolve_RejectsUnsafePaths(string relative)
    {
        Assert.Throws<InvalidDataException>(() => SessionPaths.Resolve(@"C:\ws\sessions\s1", relative));
    }
}

public sealed class CaptureStateMachineTests
{
    [Theory]
    [InlineData(CaptureState.Ready, CaptureState.Recording)]
    [InlineData(CaptureState.Recording, CaptureState.Partial)]
    [InlineData(CaptureState.Partial, CaptureState.Recording)]
    [InlineData(CaptureState.Recording, CaptureState.Stopping)]
    [InlineData(CaptureState.Stopping, CaptureState.Finalized)]
    [InlineData(CaptureState.Recording, CaptureState.Interrupted)]
    [InlineData(CaptureState.Interrupted, CaptureState.Recording)]
    [InlineData(CaptureState.Interrupted, CaptureState.Finalized)]
    [InlineData(CaptureState.Ready, CaptureState.Failed)]
    public void AllowedTransitions(CaptureState from, CaptureState to)
    {
        Assert.Equal(to, CaptureStateMachine.Transition(from, to));
    }

    [Theory]
    [InlineData(CaptureState.Ready, CaptureState.Finalized)]
    [InlineData(CaptureState.Recording, CaptureState.Finalized)] // must go through Stopping
    [InlineData(CaptureState.Finalized, CaptureState.Recording)]
    [InlineData(CaptureState.Failed, CaptureState.Recording)]
    [InlineData(CaptureState.Stopping, CaptureState.Recording)]
    public void ForbiddenTransitions(CaptureState from, CaptureState to)
    {
        Assert.False(CaptureStateMachine.CanTransition(from, to));
        Assert.Throws<InvalidOperationException>(() => CaptureStateMachine.Transition(from, to));
    }

    [Fact]
    public void FinalizedAndFailed_AreTerminal()
    {
        Assert.True(CaptureStateMachine.IsTerminal(CaptureState.Finalized));
        Assert.True(CaptureStateMachine.IsTerminal(CaptureState.Failed));
        Assert.False(CaptureStateMachine.IsTerminal(CaptureState.Interrupted));
    }
}
