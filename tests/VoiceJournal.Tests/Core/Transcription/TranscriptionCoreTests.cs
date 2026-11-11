using System.Text;
using VoiceJournal.Core.Audio;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Sessions;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Tests.TestSupport;

namespace VoiceJournal.Tests.Core.Transcription;

internal static class T
{
    public static TranscriptSegment Seg(long start, long end, string text, AudioSource source = AudioSource.Mic) =>
        new() { SegmentId = Guid.CreateVersion7(), Source = source, StartMs = start, EndMs = end, Text = text };

    public static SttProvenance Provenance(string model = "ggml-small.bin") => new()
    {
        InputHash = new string('a', 64),
        EngineName = "whisper.cpp",
        EngineVersion = "1.9.5",
        EngineSha256 = new string('b', 64),
        ModelName = model,
        ModelSha256 = new string('c', 64),
        ConfigHash = new SttConfiguration { ModelName = model }.ComputeHash(),
    };

    public static TranscriptDocument Raw(Guid session, int revision, params TranscriptSegment[] segments) => new()
    {
        SessionId = session,
        Revision = revision,
        Kind = TranscriptRevisionKind.Raw,
        RawRevision = revision,
        CreatedAtUtc = DateTimeOffset.UnixEpoch,
        Provenance = Provenance(),
        Segments = TranscriptDocument.Order(segments),
    };
}

public sealed class SttPieceTests
{
    private const long Min = 60_000;

    [Fact]
    public void Plan_SplitsIntoThirtyMinutePieces_WithTwoSecondsOfOverlapInside()
    {
        var pieces = SttPiecePlanner.Plan(65 * Min, 30 * Min, 2_000);

        Assert.Equal(3, pieces.Count);
        Assert.Equal(new SttPiece(1, 0, 30 * Min + 2_000, 0, 30 * Min), pieces[0]);
        Assert.Equal(new SttPiece(2, 30 * Min - 2_000, 60 * Min + 2_000, 30 * Min, 60 * Min), pieces[1]);
        Assert.Equal(new SttPiece(3, 60 * Min - 2_000, 65 * Min, 60 * Min, 65 * Min), pieces[2]);
    }

    [Fact]
    public void Plan_ShortRecording_IsOnePiece_AndEmptyIsNone()
    {
        Assert.Equal([new SttPiece(1, 0, 5_000, 0, 5_000)], SttPiecePlanner.Plan(5_000, 30 * Min, 2_000));
        Assert.Empty(SttPiecePlanner.Plan(0, 30 * Min, 2_000));
    }

    [Fact]
    public void Merge_KeepsEachSegmentOnce_ByMidpoint_WhenTheBoundaryIsInSilence()
    {
        var pieces = SttPiecePlanner.Plan(60_000, 30_000, 2_000);
        // The sentence at 28.5–29.5 s lies inside the overlap, so both pieces hear it whole.
        var left = new SttPieceResult(pieces[0], [T.Seg(1_000, 5_000, "最初の文"), T.Seg(28_500, 29_500, "境界の手前の文")]);
        var right = new SttPieceResult(pieces[1], [T.Seg(28_500, 29_500, "境界の手前の文"), T.Seg(40_000, 44_000, "後半の文")]);

        var merged = SttPieceMerger.Merge([left, right]);

        Assert.Equal(["最初の文", "境界の手前の文", "後半の文"], merged.Select(s => s.Text));
        Assert.DoesNotContain(merged, s => s.Has(SegmentQuality.Boundary));
    }

    [Fact]
    public void Merge_KeepsBothCandidates_AndFlagsThem_WhenASentenceIsCutByTheSplit()
    {
        var pieces = SttPiecePlanner.Plan(60_000, 30_000, 2_000);
        // A long sentence 26–35 s: the left piece only hears up to 32 s, so its text runs into the edge.
        var leftCut = T.Seg(26_000, 32_000, "長い文の前半だけ");
        var rightWhole = T.Seg(28_000, 35_000, "の前半と後半");
        var left = new SttPieceResult(pieces[0], [T.Seg(1_000, 5_000, "最初の文"), leftCut]);
        var right = new SttPieceResult(pieces[1], [rightWhole, T.Seg(40_000, 44_000, "後半の文")]);

        var merged = SttPieceMerger.Merge([left, right]);

        Assert.Equal(4, merged.Count);
        Assert.True(merged.Single(s => s.SegmentId == leftCut.SegmentId).Has(SegmentQuality.Boundary));
        Assert.True(merged.Single(s => s.SegmentId == rightWhole.SegmentId).Has(SegmentQuality.Boundary));
        Assert.False(merged.Single(s => s.Text == "最初の文").Has(SegmentQuality.Boundary));
    }
}

public sealed class QualityFlaggerTests
{
    [Fact]
    public void Flags_TextInFilledLoopbackSilence_AndOverlappingMissingAudio_AndShortUtterances()
    {
        var hallucination = T.Seg(10_000, 12_000, "ご視聴ありがとうございました", AudioSource.Loopback);
        var real = T.Seg(30_000, 33_000, "本当の音声です", AudioSource.Loopback);
        var micShort = T.Seg(1_000, 1_200, "え");
        var micInGap = T.Seg(50_000, 52_000, "切断をまたぐ発話");
        var contexts = new Dictionary<AudioSource, TrackQualityContext>
        {
            [AudioSource.Loopback] = new() { SilentRanges = [new TimeRange(0, 25_000)] },
            [AudioSource.Mic] = new() { GapRanges = [new TimeRange(51_000, 60_000)] },
        };

        var flagged = QualityFlagger.Apply([micShort, hallucination, real, micInGap], contexts);

        Assert.Equal([SegmentQuality.TooShort], flagged[0].QualityFlags);
        Assert.Equal([SegmentQuality.Silence], flagged[1].QualityFlags);
        Assert.Empty(flagged[2].QualityFlags);
        Assert.Equal([SegmentQuality.AudioGap], flagged[3].QualityFlags);
    }

    [Fact]
    public void Flags_QuietInput_FromTheMeasuredLevel()
    {
        var contexts = new Dictionary<AudioSource, TrackQualityContext>
        {
            [AudioSource.Mic] = new() { LevelDbfs = (start, _) => start < 10_000 ? -75 : -30 },
        };

        var flagged = QualityFlagger.Apply([T.Seg(1_000, 3_000, "静かな所の文"), T.Seg(20_000, 23_000, "声のある文")], contexts);

        Assert.True(flagged[0].Has(SegmentQuality.Silence));
        Assert.False(flagged[1].Has(SegmentQuality.Silence));
    }

    [Theory]
    [InlineData("ありがとうございます。ありがとうございます。ありがとうございます。ありがとうございます。", true)]
    [InlineData("あああああああああああ", true)]
    [InlineData("それでは来週の水曜日までに資料をまとめてください。", false)]
    [InlineData("はいはい、そうですね。", false)]
    public void IsRepetitive_DetectsLoops_ButNotNormalSpeech(string text, bool expected) =>
        Assert.Equal(expected, QualityFlagger.IsRepetitive(text));

    [Fact]
    public void Flags_TheSameTextThreeTimesInARow_OnOneSource()
    {
        var segments = new[]
        {
            T.Seg(0, 2_000, "字幕は視聴者によって作成されました"),
            T.Seg(2_000, 4_000, "字幕は視聴者によって作成されました"),
            T.Seg(4_000, 6_000, "字幕は視聴者によって作成されました"),
            T.Seg(6_000, 8_000, "次の話題です"),
        };

        var flagged = QualityFlagger.Apply(segments, new Dictionary<AudioSource, TrackQualityContext>());

        Assert.All(flagged.Take(3), s => Assert.True(s.Has(SegmentQuality.Repetition)));
        Assert.False(flagged[3].Has(SegmentQuality.Repetition));
    }

    [Fact]
    public void Echo_MarksMicSegmentsWhosWordsAreInThePcAudioAtTheSameTime()
    {
        var far = T.Seg(10_000, 14_000, "先週月曜日は40度ぐらいまで熱が上がって", AudioSource.Loopback);
        var echo = T.Seg(10_400, 13_800, "先週月曜日は40度くらいまで熱があがって"); // the same words, recognized a little differently
        var own = T.Seg(14_500, 16_000, "大丈夫ですか、無理しないでください"); // the user: not in the PC audio
        var reply = T.Seg(14_200, 15_000, "40度ですか"); // reuses the other side's words: kept (share below the threshold)
        var later = T.Seg(30_000, 33_000, "先週月曜日は40度ぐらいまで熱が上がって"); // same words, far from them in time
        var hallucination = T.Seg(40_000, 41_000, "ご視聴ありがとうございました", AudioSource.Loopback).WithFlag(SegmentQuality.Silence);
        var underIt = T.Seg(40_000, 41_000, "ご視聴ありがとうございました");

        var flagged = QualityFlagger.Apply([far, echo, own, reply, later, hallucination, underIt], new Dictionary<AudioSource, TrackQualityContext>());

        Assert.Equal([echo.SegmentId], flagged.Where(s => s.Has(SegmentQuality.Echo)).Select(s => s.SegmentId));
        Assert.Equal(echo.Text, flagged.Single(s => s.SegmentId == echo.SegmentId).Text); // the text stays
    }

    [Fact]
    public void Echo_IsNotJudgedWithoutPcAudio_AndRangesAreJudgedAgainstTheRevision()
    {
        var mic = T.Seg(0, 2_000, "提案をぶつけてみて");
        Assert.False(QualityFlagger.Apply([mic], new Dictionary<AudioSource, TrackQualityContext>()).Single().Has(SegmentQuality.Echo));

        var revision = new[] { T.Seg(0, 2_100, "提案をぶつけてみてそれに対して", AudioSource.Loopback), T.Seg(0, 2_000, "古い認識") };
        Assert.True(QualityFlagger.WithEcho([mic], revision).Single().Has(SegmentQuality.Echo));
    }

    [Theory]
    [InlineData("あいう", "xxあいうxx", 1.0)]
    [InlineData("あいう", "あいx", 0.5)]
    [InlineData("あ", "xあx", 1.0)]
    [InlineData("あいう", "", 0.0)]
    [InlineData("", "あいう", 0.0)]
    public void SharedPairShare_CountsTheTextsCharacterPairsFoundInTheOther(string text, string other, double expected) =>
        Assert.Equal(expected, QualityFlagger.SharedPairShare(text, other), 3);

    [Fact]
    public void Context_FromManifest_FindsUncoveredTimeAndCrashTail()
    {
        var track = new TrackManifest
        {
            TrackId = Guid.NewGuid(),
            Source = AudioSource.Mic,
            DeviceId = "d",
            DeviceName = "マイク",
            Format = AudioFormatInfo.IeeeFloat32Bit(48_000, 1),
            Chunks =
            {
                new ChunkManifest { Sequence = 1, File = "audio/mic/000001.wav", StartMs = 1_000, FrameCount = 48_000 * 9, State = ChunkState.Completed },
                // Unplugged 10–20 s, reconnected as a new chunk.
                new ChunkManifest { Sequence = 2, File = "audio/mic/000002.wav", StartMs = 20_000, FrameCount = 48_000 * 10, State = ChunkState.Recovered },
            },
            Events = { new AudioEventManifest { Kind = AudioEventKind.Interrupted, StartMs = 30_000 } },
        };

        var context = TrackQualityContext.FromTrack(track, 40_000);

        Assert.Equal([new TimeRange(0, 1_000), new TimeRange(10_000, 20_000), new TimeRange(30_000, 40_000), new TimeRange(30_000, 40_000)], context.GapRanges);
    }
}

public sealed class CharacterErrorRateTests
{
    [Theory]
    [InlineData("今日は、二〇二六年 十一月 一日です。", "今日は2026年11月1日です")]
    [InlineData("参加者は二十五人、予算は三千五百万円", "参加者は25人予算は35000000円")]
    [InlineData("ＡＢＣ　１２３「テスト」", "abc123テスト")]
    [InlineData("一緒に十分確認する", "1緒に10分確認する")]
    [InlineData("百二十三万四千五百六十七", "1234567")]
    public void Normalize_FollowsTheFixedRules(string input, string expected) =>
        Assert.Equal(expected, CerNormalizer.Normalize(input));

    [Fact]
    public void Compute_CountsSubstitutionsDeletionsAndInsertions()
    {
        // 田中さん → 田舎さん (1 substitution); 来週 dropped (2 deletions); 、ね added (1 insertion).
        var r = CharacterErrorRate.Compute("田中さんは来週水曜に発表します。", "田舎さんは水曜に発表しますね");

        Assert.Equal(15, r.ReferenceLength);
        Assert.Equal(1, r.Substitutions);
        Assert.Equal(2, r.Deletions);
        Assert.Equal(1, r.Insertions);
        Assert.Equal(4.0 / 15, r.Rate, 6);
    }

    [Fact]
    public void Compute_TreatsNumberSpellingsAndPunctuationAsEqual()
    {
        var r = CharacterErrorRate.Compute("会議は三時から、二人で行います。", "会議は3時から 2人で行います");
        Assert.Equal(0, r.Errors);
    }
}

public sealed class ProcessingStateMachineTests
{
    [Fact]
    public void FollowsTheJobLifecycle_AndAllowsRequeueForResumeAndReprocessing()
    {
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.None, ProcessingState.Queued));
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.Queued, ProcessingState.Normalizing));
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.Normalizing, ProcessingState.Transcribing));
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.Transcribing, ProcessingState.Transcribed));
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.Transcribing, ProcessingState.Queued));
        Assert.True(ProcessingStateMachine.CanTransition(ProcessingState.Transcribed, ProcessingState.Queued));
        Assert.False(ProcessingStateMachine.CanTransition(ProcessingState.None, ProcessingState.Transcribing));
        Assert.Throws<InvalidOperationException>(() => ProcessingStateMachine.Transition(ProcessingState.Transcribed, ProcessingState.Transcribing));
    }

    [Fact]
    public void ConfigHash_IgnoresThreads_ButNotModelOrVad()
    {
        var a = new SttConfiguration { ModelName = "ggml-small.bin", Threads = 4 };
        Assert.Equal(a.ComputeHash(), (a with { Threads = 12 }).ComputeHash());
        Assert.NotEqual(a.ComputeHash(), (a with { ModelName = "ggml-medium.bin" }).ComputeHash());
        Assert.NotEqual(a.ComputeHash(), (a with { UseVad = false }).ComputeHash());
        Assert.Matches("^[0-9a-f]{64}$", a.ComputeHash());
    }
}

public sealed class TranscriptRevisionTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly Guid _session = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private (TranscriptStore Store, TranscriptDocument Raw) SavedRaw()
    {
        var store = new TranscriptStore(_dir.Path);
        var raw = T.Raw(_session, 1,
            T.Seg(1_000, 4_000, "田舎さんが説明します"),
            T.Seg(5_000, 9_000, "資料は来週配ります"),
            T.Seg(2_000, 6_000, "PC の音です", AudioSource.Loopback));
        store.Save(raw, durationMs: 10_000);
        return (store, raw);
    }

    [Fact]
    public void Save_WritesRawJsonTextAndMarkdown_InTheRevisionFolder()
    {
        var (store, _) = SavedRaw();

        var dir = store.RevisionDirectory(1);
        Assert.True(File.Exists(Path.Combine(dir, TranscriptStore.RawFileName)));
        var txt = File.ReadAllText(Path.Combine(dir, TranscriptStore.RawTextFileName), Encoding.UTF8);
        Assert.StartsWith("[00:00:01.000 → 00:00:04.000] マイク: 田舎さんが説明します\n[00:00:02.000 → 00:00:06.000] PC内部音声: PC の音です\n", txt, StringComparison.Ordinal);
        var md = File.ReadAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName), Encoding.UTF8);
        Assert.Contains("**00:00:05 マイク**", md, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"raw\"", File.ReadAllText(Path.Combine(dir, TranscriptStore.RawFileName)), StringComparison.Ordinal);

        var loaded = store.Load(1);
        Assert.Equal(3, loaded.Segments.Count);
        Assert.Equal("ggml-small.bin", loaded.Provenance!.ModelName);
    }

    [Fact]
    public void EditText_MakesTheNextRevision_KeepsTheSegmentId_AndLeavesTheRawFileUntouched()
    {
        var (store, raw) = SavedRaw();
        var rawPath = Path.Combine(store.RevisionDirectory(1), TranscriptStore.RawFileName);
        var rawBytes = File.ReadAllBytes(rawPath);
        var target = raw.Segments[0];

        var edited = TranscriptEditing.EditText(raw, store.NextRevision(), target.SegmentId, "田中さんが説明します", Now);
        store.Save(edited, 10_000);

        Assert.Equal([1, 2], store.Revisions());
        Assert.Equal(rawBytes, File.ReadAllBytes(rawPath));
        var latest = store.LoadLatest()!;
        Assert.Equal(TranscriptRevisionKind.Edited, latest.Kind);
        Assert.Equal(1, latest.ParentRevision);
        Assert.Equal(1, latest.RawRevision);
        var seg = latest.Find(target.SegmentId)!;
        Assert.Equal("田中さんが説明します", seg.Text);
        Assert.True(seg.Has(SegmentQuality.UserEdited));
        Assert.Equal("田舎さんが説明します", store.Load(1).Find(target.SegmentId)!.Text);
        Assert.Equal(TranscriptChangeKind.TextEdited, latest.Changes.Single().Kind);
        Assert.Contains("修正、元の原文は Revision 1", File.ReadAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName), Encoding.UTF8), StringComparison.Ordinal);
    }

    [Fact]
    public void EditingAnEcho_RejudgesItAgainstThePcAudio_AndIncludesTheCorrectedSpeechInAnalysis()
    {
        var mic = T.Seg(1_000, 4_000, "明日までに提出します").WithFlag(SegmentQuality.Echo);
        var pc = T.Seg(1_000, 4_000, "明日までに提出します", AudioSource.Loopback);
        var raw = T.Raw(_session, 1, mic, pc);
        var changed = TranscriptEditing.EditText(raw, 2, mic.SegmentId, "別の案件は担当者に確認して折り返す", Now);

        Assert.False(changed.Find(mic.SegmentId)!.Has(SegmentQuality.Echo));
        Assert.True(changed.Find(mic.SegmentId)!.Has(SegmentQuality.UserEdited));
        var plan = VoiceJournal.Core.Analysis.AnalysisPlanner.Plan(changed, 1_000, new VoiceJournal.Core.Analysis.TokenEstimator());
        Assert.DoesNotContain(plan.Skipped, s => s.SegmentId == mic.SegmentId);
        var unchanged = TranscriptEditing.EditText(raw, 2, mic.SegmentId, mic.Text, Now);
        Assert.True(unchanged.Find(mic.SegmentId)!.Has(SegmentQuality.Echo));
    }

    [Fact]
    public void CorrectingPcAudio_RejudgesExistingMicrophoneEcho()
    {
        var mic = T.Seg(1_000, 4_000, "明日までに提出します").WithFlag(SegmentQuality.Echo);
        var pc = T.Seg(1_000, 4_000, "明日までに提出します", AudioSource.Loopback);
        var raw = T.Raw(_session, 1, mic, pc);

        var changed = TranscriptEditing.EditText(raw, 2, pc.SegmentId, "別の案件は担当者に確認して折り返す", Now);

        Assert.False(changed.Find(mic.SegmentId)!.Has(SegmentQuality.Echo));
        Assert.DoesNotContain(changed.Find(mic.SegmentId)!.QualityFlags, f => f == SegmentQuality.UserEdited);
    }

    [Fact]
    public void CompletingARawSave_WithoutAPointer_RetainsLegacyEditsAndRepairsMarkdown()
    {
        var (store, raw) = SavedRaw();
        store.Save(TranscriptEditing.EditText(raw, 2, raw.Segments[0].SegmentId, "残すべき修正", Now));
        var pointer = Path.Combine(store.RootDirectory, TranscriptStore.CurrentFileName);
        File.Delete(pointer); // a legacy session uses its latest edited revision
        store.Save(T.Raw(_session, 3, T.Seg(0, 1_000, "後から終わった原文")));
        // Simulate the raw JSON reaching disk before Save wrote down the retained legacy revision.
        File.Delete(pointer);
        File.WriteAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName), "古い本文");

        store.CompleteRawSave(3);

        Assert.Equal(2, store.CurrentRevision());
        Assert.Equal(TranscriptAdoptedBy.User, store.ReadAdoption()!.By);
        Assert.Equal(TranscriptRendering.ToMarkdown(store.Load(2)), File.ReadAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName)));
    }

    [Fact]
    public void ConcurrentRawSaves_AllocateDistinctConsecutiveRevisions()
    {
        var (store, _) = SavedRaw();

        Parallel.For(0, 16, _ => store.SaveNextRaw(revision => T.Raw(_session, revision, T.Seg(0, 1_000, "認識した本文"))));

        Assert.Equal(Enumerable.Range(1, 17), store.Revisions());
        Assert.Equal(17, store.CurrentRevision());
        Assert.Equal(TranscriptRendering.ToMarkdown(store.Load(17)), File.ReadAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName)));
    }

    [Fact]
    public void ReplaceRange_SwapsTheOverlappingSegmentsOfThatSource_ForNewOnes_RecordingWhatRecognizedThem()
    {
        var (store, raw) = SavedRaw();
        var range = new TimeRange(500, 9_500);
        var recognized = new[] { T.Seg(900, 4_100, "田中さんが説明します"), T.Seg(5_000, 9_000, "資料は来週配ります") };

        var next = TranscriptEditing.ReplaceRange(raw, 2, AudioSource.Mic, range, recognized, T.Provenance("ggml-medium.bin"), Now);
        store.Save(next, 10_000);

        var mic = next.Segments.Where(s => s.Source == AudioSource.Mic).ToList();
        Assert.Equal(2, mic.Count);
        Assert.All(mic, s => Assert.DoesNotContain(s.SegmentId, raw.Segments.Select(r => r.SegmentId)));
        Assert.All(mic, s => Assert.True(s.Has(SegmentQuality.UserEdited)));
        Assert.Equal(raw.Segments.Where(s => s.Source == AudioSource.Mic).Select(s => s.SegmentId).Order(), mic[0].Origins.Order());
        Assert.Contains(next.Segments, s => s.Source == AudioSource.Loopback); // the other source is untouched
        var change = next.Changes.Single();
        Assert.Equal(TranscriptChangeKind.ReRecognized, change.Kind);
        Assert.Equal("ggml-medium.bin", change.Provenance!.ModelName);
        Assert.Equal(range, change.Range);
    }

    [Fact]
    public void RevertToRaw_RestoresTheRawSegments_AsANewRevision()
    {
        var (store, raw) = SavedRaw();
        var replaced = TranscriptEditing.ReplaceRange(raw, 2, AudioSource.Mic, new TimeRange(0, 10_000), [T.Seg(1_000, 9_000, "一つにまとまった文")], T.Provenance(), Now);
        store.Save(replaced, 10_000);
        var merged = replaced.Segments.Single(s => s.Source == AudioSource.Mic);

        var reverted = TranscriptEditing.RevertToRaw(replaced, raw, 3, merged.SegmentId, Now);
        store.Save(reverted, 10_000);

        Assert.Equal(raw.Segments.Select(s => (s.SegmentId, s.Text)), reverted.Segments.Select(s => (s.SegmentId, s.Text)));
        Assert.Equal(TranscriptChangeKind.RevertedToOriginal, reverted.Changes.Single().Kind);
        Assert.Equal([1, 2, 3], store.Revisions());
    }

    [Fact]
    public void Save_RefusesToReplaceARevision_OrSkipANumber_OrBadSegments()
    {
        var (store, raw) = SavedRaw();

        Assert.Throws<InvalidOperationException>(() => store.Save(raw, 10_000));
        Assert.Throws<InvalidOperationException>(() => store.Save(T.Raw(_session, 3, T.Seg(0, 1_000, "x")), 10_000));
        Assert.Throws<ArgumentException>(() => store.Save(T.Raw(_session, 2, T.Seg(2_000, 1_000, "x")), 10_000));
        Assert.Throws<ArgumentException>(() => store.Save(T.Raw(_session, 2, T.Seg(9_000, 11_000, "x")), 10_000));
        var dup = T.Seg(0, 1_000, "x");
        Assert.Throws<ArgumentException>(() => store.Save(T.Raw(_session, 2, dup, dup), 10_000));
    }

    private string Markdown() => File.ReadAllText(Path.Combine(_dir.Path, TranscriptStore.MarkdownFileName), Encoding.UTF8);

    [Fact]
    public void ANewRawRevision_IsUsed_UntilTheUserEdits_ThenALateRawDoesNotHideTheEdits()
    {
        var (store, _) = SavedRaw();
        Assert.Equal(1, store.CurrentRevision());
        Assert.True(store.Save(T.Raw(_session, 2, T.Seg(0, 1_000, "再処理の原文")), 10_000)); // nothing chosen yet
        Assert.Equal(2, store.CurrentRevision());
        Assert.Equal(TranscriptAdoptedBy.Automatic, store.ReadAdoption()!.By);

        var segment = store.Load(2).Segments[0].SegmentId;
        Assert.True(store.Save(TranscriptEditing.EditText(store.Load(2), 3, segment, "利用者の修正", Now), 10_000));
        Assert.Equal(TranscriptAdoptedBy.User, store.ReadAdoption()!.By);

        // A full job that ends later (queued before the edit, or resumed after a restart) adds its revision beside the edits.
        Assert.False(store.Save(T.Raw(_session, 4, T.Seg(0, 1_000, "遅れて終わった原文")), 10_000));
        Assert.Equal([1, 2, 3, 4], store.Revisions());
        Assert.Equal(4, store.LoadLatest()!.Revision);
        Assert.Equal(3, store.CurrentRevision());
        Assert.Contains("利用者の修正", Markdown(), StringComparison.Ordinal);
        Assert.DoesNotContain("遅れて終わった原文", Markdown(), StringComparison.Ordinal);

        // Edits start from the revision in use, never from another one.
        var fromOther = TranscriptEditing.EditText(store.Load(4), 5, store.Load(4).Segments[0].SegmentId, "使っていない版への修正", Now);
        Assert.Throws<InvalidOperationException>(() => store.Save(fromOther, 10_000));
        Assert.Equal([1, 2, 3, 4], store.Revisions());

        // The user may choose the new raw revision; a chosen revision is not replaced by the next job either.
        store.Adopt(4, Now);
        Assert.Equal(4, store.CurrentRevision());
        Assert.Contains("遅れて終わった原文", Markdown(), StringComparison.Ordinal);
        Assert.False(store.Save(T.Raw(_session, 5, T.Seg(0, 1_000, "さらに別の原文")), 10_000));
        Assert.Equal(4, store.CurrentRevision());
        Assert.Throws<FileNotFoundException>(() => store.Adopt(9, Now));
    }

    [Fact]
    public void WithoutAChoiceOnFile_TheLatestIsInUse_AndAnEditedLatestStaysInUse_WhenARawArrives()
    {
        var (store, raw) = SavedRaw();
        store.Save(TranscriptEditing.EditText(raw, 2, raw.Segments[0].SegmentId, "以前の修正", Now), 10_000);
        var pointer = Path.Combine(store.RootDirectory, TranscriptStore.CurrentFileName);
        File.Delete(pointer); // a session written before the choice was recorded
        Assert.Equal(2, store.CurrentRevision());

        Assert.False(store.Save(T.Raw(_session, 3, T.Seg(0, 1_000, "新しい原文")), 10_000));
        Assert.Equal(2, store.CurrentRevision()); // written down now, so "latest" no longer decides

        File.WriteAllText(pointer, "{ 壊れた");
        Assert.Null(store.ReadAdoption());
        Assert.Equal(3, store.CurrentRevision()); // a broken pointer falls back to the latest; no revision is lost
    }

    [Fact]
    public void Save_CleansUpWhatACrashedSaveLeftBehind()
    {
        var (store, raw) = SavedRaw();
        var dir = store.RevisionDirectory(2);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, TranscriptStore.RawTextFileName), "途中まで");

        Assert.Equal(2, store.NextRevision());
        store.Save(T.Raw(_session, 2, T.Seg(0, 1_000, "再処理の結果")), 10_000);

        Assert.Equal("再処理の結果", store.Load(2).Segments.Single().Text);
        Assert.Equal(raw.Segments.Count, store.Load(1).Segments.Count);
    }
}
