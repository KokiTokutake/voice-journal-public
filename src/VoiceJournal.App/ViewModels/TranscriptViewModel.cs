using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Capture;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Transcription;
using VoiceJournal.Infrastructure.Stt;

namespace VoiceJournal.App.ViewModels;

/// <summary>One segment in UI-06. Setting <see cref="Text"/> asks the owner to save a new revision.</summary>
public sealed partial class SegmentRowViewModel : ObservableObject
{
    private readonly Func<SegmentRowViewModel, string, bool>? _onEdited;

    internal SegmentRowViewModel(TranscriptSegment segment, Func<SegmentRowViewModel, string, bool>? onEdited)
    {
        Segment = segment;
        _onEdited = onEdited;
    }

    public TranscriptSegment Segment { get; private set; }

    public Guid SegmentId => Segment.SegmentId;

    public AudioSource Source => Segment.Source;

    public long StartMs => Segment.StartMs;

    public string TimeText => TranscriptRendering.Time(Segment.StartMs, false);

    public string RangeText => $"{TranscriptRendering.Time(Segment.StartMs, true)} – {TranscriptRendering.Time(Segment.EndMs, true)}";

    public string SourceText => UserMessages.SourceName(Segment.Source);

    public bool IsEditable => _onEdited is not null;

    /// <summary>Two-way bound. A change is saved as a new revision; if saving fails the old text comes back.</summary>
    public string Text
    {
        get => Segment.Text;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == Segment.Text || _onEdited is null) return;
            if (!_onEdited(this, text)) OnPropertyChanged();
        }
    }

    public bool HasFlags => Segment.QualityFlags.Any(f => f != SegmentQuality.UserEdited);

    public string FlagsText => string.Join("・", Segment.QualityFlags.Where(f => f != SegmentQuality.UserEdited).Select(TranscriptRendering.FlagLabel));

    public bool IsEdited => Segment.Has(SegmentQuality.UserEdited);

    public string EditedText => IsEdited ? "修正" : string.Empty;

    public string HintText => ReviewHints.For(Segment.Text);

    internal void Update(TranscriptSegment segment)
    {
        Segment = segment;
        OnPropertyChanged(string.Empty);
    }
}

public sealed record RevisionItem(int Revision, string Label);

public enum SourceFilter
{
    All,
    Mic,
    Loopback,
}

/// <summary>
/// UI-06 transcript (detail spec 8.2, 18.2): source label, time, text, quality flags and history; segment playback,
/// editing, revert and range re-recognition. Only the revision in use can be changed, and every change is a new
/// revision that becomes the one in use (AT-07). Other revisions are read-only until the user chooses one.
/// </summary>
public sealed partial class TranscriptViewModel : ObservableObject
{
    private readonly TranscriptionCoordinator _transcription;
    private readonly IAudioPlayer _player;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private string? _directory;
    private TranscriptStore? _store;
    private TranscriptDocument? _document;
    private List<SegmentRowViewModel> _rows = [];
    private Guid? _pendingRangeJob;
    private bool _switching;

    public TranscriptViewModel(TranscriptionCoordinator transcription, IAudioPlayer player, IUiDispatcher ui, SttToolSettings tools, TimeProvider? time = null)
    {
        _transcription = transcription;
        _player = player;
        _ui = ui;
        _time = time ?? TimeProvider.System;
        ModelOptions = [.. tools.Models.Keys.Order(StringComparer.Ordinal).Select(m => new ModelOption(m, UserMessages.ModelLabel(m)))];
        ReRecognizeModel = transcription.CurrentConfiguration.ModelName;
        _transcription.JobChanged += (_, change) => _ui.Post(() => OnJobChanged(change));
        _player.Ended += (_, _) => _ui.Post(() => NowPlaying = null);
    }

    public sealed record ModelOption(string Name, string Label);

    public IReadOnlyList<ModelOption> ModelOptions { get; }

    [ObservableProperty]
    public partial string ReRecognizeModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    public partial string? SessionTitle { get; set; }

    public bool HasSession => SessionTitle is not null;

    public string? SessionDirectory => _directory;

    public ObservableCollection<RevisionItem> Revisions { get; } = [];

    [ObservableProperty]
    public partial RevisionItem? SelectedRevision { get; set; }

    public BulkObservableCollection<SegmentRowViewModel> Segments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand), nameof(PlaySelectedCommand))]
    public partial SegmentRowViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial SourceFilter Filter { get; set; } = SourceFilter.All;

    [ObservableProperty]
    public partial bool FlaggedOnly { get; set; }

    /// <summary>The shown revision is the one in use (editable). Others are read-only and can be chosen.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevertCommand), nameof(ReRecognizeCommand), nameof(AdoptCommand))]
    public partial bool IsCurrent { get; set; }

    [ObservableProperty]
    public partial string? ProvenanceText { get; set; }

    [ObservableProperty]
    public partial string? UnrecognizedText { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? NowPlaying { get; set; }

    public string CountText => $"{Segments.Count} 区間" + (Segments.Count != _rows.Count ? $"（全 {_rows.Count}）" : string.Empty);

    partial void OnFilterChanged(SourceFilter value) => ApplyFilter();

    partial void OnFlaggedOnlyChanged(bool value) => ApplyFilter();

    partial void OnSelectedRevisionChanged(RevisionItem? value)
    {
        if (_switching || value is null || _store is null) return;
        Show(value.Revision);
    }

    /// <summary>Opens the revision in use of a session.</summary>
    public void Open(string sessionDirectory, string title)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        _directory = sessionDirectory;
        _store = new TranscriptStore(sessionDirectory);
        _pendingRangeJob = null;
        SessionTitle = title;
        Message = null;
        Reload(null);
    }

    private void Reload(int? revision)
    {
        if (_store is null) return;
        IReadOnlyList<int> revisions;
        try
        {
            revisions = _store.Revisions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"文字起こしを読み込めませんでした（{ex.Message}）。";
            return;
        }
        _switching = true;
        Revisions.Clear();
        foreach (var r in revisions.Reverse()) Revisions.Add(new RevisionItem(r, Label(r)));
        _switching = false;
        if (revisions.Count == 0)
        {
            _document = null;
            _rows = [];
            ApplyFilter();
            IsCurrent = false;
            ProvenanceText = null;
            UnrecognizedText = null;
            Message = "まだ文字起こしがありません。ライブラリの「文字起こし」で開始できます。";
            return;
        }
        Show(revision ?? _store.CurrentRevision() ?? revisions[^1]);
    }

    private const string InUseMark = "　［使用中］";

    private string Label(int revision)
    {
        try
        {
            var doc = _store!.Load(revision);
            var label = doc.Kind == TranscriptRevisionKind.Raw
                ? $"Rev {revision}  原文（{UserMessages.ModelLabel(doc.Provenance?.ModelName ?? "?")}）"
                : $"Rev {revision}  修正（{ChangeName(doc.Changes.Count > 0 ? doc.Changes[0].Kind : null)}）";
            return revision == _store.CurrentRevision() ? label + InUseMark : label;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return $"Rev {revision}  （読み込めません）";
        }
    }

    private static string ChangeName(TranscriptChangeKind? kind) => kind switch
    {
        TranscriptChangeKind.TextEdited => "本文",
        TranscriptChangeKind.RevertedToOriginal => "原文へ戻す",
        TranscriptChangeKind.ReRecognized => "区間の再認識",
        _ => "—",
    };

    private void Show(int revision)
    {
        try
        {
            var doc = _store!.Load(revision);
            var raw = doc.Kind == TranscriptRevisionKind.Raw ? doc : _store.Load(doc.RawRevision);
            _document = doc;
            IsCurrent = revision == _store.CurrentRevision();
            _rows = [.. doc.Segments.Select(s => new SegmentRowViewModel(s, IsCurrent ? SaveEdit : null))];
            _switching = true;
            SelectedRevision = Revisions.FirstOrDefault(r => r.Revision == revision);
            _switching = false;
            var p = raw.Provenance;
            var parameters = p?.Parameters ?? new Dictionary<string, string>();
            ProvenanceText = $"原文 Rev {raw.Revision}: {UserMessages.ModelLabel(p?.ModelName ?? "?")}、VAD {(parameters.TryGetValue("vad", out var vad) && vad == "on" ? "あり" : "なし")}、" +
                $"{p?.EngineName} {p?.EngineVersion}、{raw.CreatedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)} 作成" +
                (IsCurrent ? string.Empty : "　※ 使用中ではない Revision は表示のみです（修正するときは「この Revision を使う」）");
            var unrecognized = raw.Sources.SelectMany(s => s.UnrecognizedRanges.Select(r => $"{UserMessages.SourceName(s.Source)} {TranscriptRendering.Time(r.StartMs, false)}–{TranscriptRendering.Time(r.EndMs, false)}")).ToList();
            UnrecognizedText = unrecognized.Count == 0 ? null : "認識されなかった範囲（STT-003）: " + string.Join("、", unrecognized);
            ApplyFilter();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Message = $"Revision {revision} を読み込めませんでした（{ex.Message}）。";
        }
    }

    private void ApplyFilter()
    {
        var selectedId = Selected?.SegmentId;
        Segments.ReplaceAll(_rows.Where(row =>
            !(Filter == SourceFilter.Mic && row.Source != AudioSource.Mic)
            && !(Filter == SourceFilter.Loopback && row.Source != AudioSource.Loopback)
            && !(FlaggedOnly && !row.HasFlags && row.HintText.Length == 0)));
        Selected = Segments.FirstOrDefault(r => r.SegmentId == selectedId);
        OnPropertyChanged(nameof(CountText));
    }

    /// <summary>Saves an edit as the next revision; returns false (and shows why) when it cannot.</summary>
    private bool SaveEdit(SegmentRowViewModel row, string text)
    {
        if (_store is null || _document is null || !IsCurrent) return false;
        try
        {
            var previous = _document.Revision;
            var next = TranscriptEditing.EditText(_document, _store.NextRevision(), row.SegmentId, text, _time.GetUtcNow());
            _store.Save(next);
            _document = next;
            row.Update(next.Find(row.SegmentId)!);
            _switching = true;
            // Not a reload: this runs inside the grid's cell commit, which must not have its rows replaced.
            var index = Revisions.IndexOf(Revisions.First(r => r.Revision == previous));
            Revisions[index] = new RevisionItem(previous, Label(previous));
            Revisions.Insert(0, new RevisionItem(next.Revision, Label(next.Revision)));
            SelectedRevision = Revisions[0];
            _switching = false;
            Message = $"Rev {next.Revision} として保存しました（原文は変わりません）。";
            _ = _transcription.RevisionSavedAsync(_directory!);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            // InvalidOperationException: another revision was written or chosen meanwhile (e.g. a re-recognition finished).
            Message = $"保存できませんでした（{ex.Message}）。使用中の Revision を開き直してから修正してください。";
            return false;
        }
    }

    private bool CanRevert() => IsCurrent && Selected?.IsEdited == true;

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void Revert()
    {
        if (_store is null || _document is null || Selected is not { } row) return;
        try
        {
            var raw = _store.Load(_document.RawRevision);
            var next = TranscriptEditing.RevertToRaw(_document, raw, _store.NextRevision(), row.SegmentId, _time.GetUtcNow());
            _store.Save(next);
            Message = $"原文に戻し、Rev {next.Revision} として保存しました。";
            _ = _transcription.RevisionSavedAsync(_directory!);
            Reload(next.Revision);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or InvalidDataException)
        {
            Message = $"原文に戻せませんでした（{ex.Message}）。";
        }
    }

    private bool CanReRecognize(IList? selection) => IsCurrent;

    /// <summary>Re-recognizes the time span of the selected segments of one source (AT-07).</summary>
    [RelayCommand(CanExecute = nameof(CanReRecognize))]
    private void ReRecognize(IList? selection)
    {
        if (_directory is null || _document is null) return;
        var rows = (selection?.OfType<SegmentRowViewModel>() ?? []).ToList();
        if (rows.Count == 0 && Selected is { } single) rows.Add(single);
        if (rows.Count == 0)
        {
            Message = "再認識する区間を選んでください。";
            return;
        }
        if (rows.Select(r => r.Source).Distinct().Count() > 1)
        {
            Message = "再認識は 1 つの音源ごとに行います。マイクか PC 内部音声のどちらかの区間だけを選んでください。";
            return;
        }
        var range = new TimeRange(rows.Min(r => r.Segment.StartMs), rows.Max(r => r.Segment.EndMs));
        if (range.DurationMs > 10 * 60_000)
        {
            Message = "一度に再認識できるのは 10 分までです。選ぶ区間を減らしてください。";
            return;
        }
        try
        {
            var job = _transcription.EnqueueRange(_directory, rows[0].Source, range, _document.Revision, ReRecognizeModel);
            _pendingRangeJob = job.JobId;
            Message = $"{UserMessages.SourceName(rows[0].Source)} {TranscriptRendering.Time(range.StartMs, false)}–{TranscriptRendering.Time(range.EndMs, false)} の再認識を順番待ちに入れました" +
                $"（{UserMessages.ModelLabel(ReRecognizeModel)}）。終わると新しい Revision で表示します。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"再認識を始められませんでした（{ex.Message}）。";
        }
    }

    private bool CanAdopt() => _document is not null && !IsCurrent;

    /// <summary>Makes the shown revision the one in use: later edits, re-recognition and summaries build on it.</summary>
    [RelayCommand(CanExecute = nameof(CanAdopt))]
    private void Adopt()
    {
        if (_store is null || _document is null || _directory is null) return;
        var revision = _document.Revision;
        try
        {
            _store.Adopt(revision, _time.GetUtcNow());
            Reload(revision);
            Message = $"Rev {revision} を使用中にしました。修正と区間の再認識は、この Revision から続けます（ほかの Revision は残っています）。";
            _ = _transcription.RevisionSavedAsync(_directory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Rev {revision} を使用中にできませんでした（{ex.Message}）。";
        }
    }

    private bool CanPlaySelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    private void PlaySelected() => Play(Selected);

    [RelayCommand]
    private void Play(SegmentRowViewModel? row)
    {
        if (_directory is null || row is null) return;
        try
        {
            var (files, offset) = LibraryViewModel.PlayableFrom(_directory, row.Source, row.Segment.StartMs);
            if (files.Count == 0)
            {
                Message = $"{row.SourceText}の再生できる音声がありません。";
                return;
            }
            var device = _player.Play(files, offset, TimeSpan.FromMilliseconds(row.Segment.DurationMs + 300));
            NowPlaying = $"再生中: {row.SourceText} {row.RangeText} → 出力先: {device}";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or FormatException)
        {
            Message = $"再生できませんでした（{ex.Message}）。";
        }
    }

    [RelayCommand]
    private void StopPlayback() => _player.StopPlayback();

    private void OnJobChanged(SttJobChange change)
    {
        if (_directory is null || !string.Equals(change.SessionDirectory, _directory, StringComparison.OrdinalIgnoreCase)) return;
        var job = change.Job;
        if (job.JobId == _pendingRangeJob)
        {
            if (job.State == ProcessingState.Transcribed)
            {
                _pendingRangeJob = null;
                Reload(null);
                Message = $"再認識が終わり、Rev {job.ResultRevision} として保存しました。";
            }
            else if (job.State is ProcessingState.Failed or ProcessingState.Cancelled)
            {
                _pendingRangeJob = null;
                Message = UserMessages.SttError(job.Error);
            }
        }
        else if (job.Kind == SttJobKind.Full && job.State == ProcessingState.Transcribed && _store is not null)
        {
            var current = _store.CurrentRevision();
            if (current == job.ResultRevision)
            {
                Reload(null);
                Message = $"新しい原文 Rev {job.ResultRevision} ができました。";
            }
            else
            {
                // Edits (or a choice) are in use: list the new raw revision without switching away from them.
                Reload(_document?.Revision);
                Message = $"新しい原文 Rev {job.ResultRevision} ができました。使用中の Rev {current}（修正または選んだ版）はそのままです。" +
                    "新しい原文を使うときは、Revision で選んで「この Revision を使う」を押してください。";
            }
        }
    }
}
