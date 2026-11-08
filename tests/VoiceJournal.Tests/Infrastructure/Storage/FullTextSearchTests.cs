using Microsoft.Data.Sqlite;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Storage;
using VoiceJournal.Tests.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Infrastructure.Storage;

public sealed class FullTextSearchTests : IDisposable
{
    private readonly TempDirectory _dir = new("vj-fts-");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public void TheTranscriptInUse_IsSearched_ByPartOfAWord() // FR-11, AT-21
    {
        var workspace = Workspace.OpenOrCreate(_dir.File("Workspace"), syncRoots: []);
        var db = VoiceJournalDatabase.Open(workspace.DatabasePath);
        var index = new SessionIndex(workspace, db, TimeProvider.System);
        var transcripts = new TranscriptIndex(workspace, db);

        var (a, idA) = JobSession.Create(Path.Combine(workspace.SessionsRoot, "2026", "11"));
        var (b, idB) = JobSession.Create(Path.Combine(workspace.SessionsRoot, "2026", "11"));
        index.Upsert(a);
        index.Upsert(b);
        var store = new TranscriptStore(a);
        var raw = T.Raw(idA, 1, T.Seg(0, 4_000, "見積書は明日までに作ります"), T.Seg(5_000, 9_000, "古い言い方の区間"));
        store.Save(raw);
        store.Save(new TranscriptDocument
        {
            SessionId = idA,
            Revision = 2,
            Kind = TranscriptRevisionKind.Edited,
            ParentRevision = 1,
            RawRevision = 1,
            CreatedAtUtc = DateTimeOffset.UnixEpoch,
            Provenance = raw.Provenance,
            Segments = [raw.Segments[0], raw.Segments[1] with { Text = "直した言い方の区間（100%）" }],
        });
        new TranscriptStore(b).Save(T.Raw(idB, 1, T.Seg(0, 4_000, "議事録を共有します")));
        transcripts.UpsertRevisions(a);
        transcripts.UpsertRevisions(b);

        Assert.Equal([idA], index.SessionsWithText("見積"));
        Assert.Equal([idA], index.SessionsWithText("直した")); // the newest revision is in use
        Assert.Empty(index.SessionsWithText("古い言い方")); // replaced by the edit
        Assert.Equal([idA], index.SessionsWithText("100%")); // 「%」 is a character
        Assert.Equal([idB], index.SessionsWithText("議"));
        Assert.Empty(index.SessionsWithText(" "));
    }
}
