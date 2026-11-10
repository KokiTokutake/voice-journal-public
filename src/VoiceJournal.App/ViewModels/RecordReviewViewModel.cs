using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Playback;
using VoiceJournal.Core.Records;
using VoiceJournal.Core.Transcription;

namespace VoiceJournal.App.ViewModels;

/// <summary>A link from a statement or candidate to the transcript segment it rests on.</summary>
public sealed record EvidenceLink(Guid SegmentId, string Label, string Quote, bool Found)
{
    public string ToolTip => Found ? $"根拠: 「{Quote}」（クリックで原文の区間へ移動して再生）" : $"根拠の区間がこの文字起こしにありません: 「{Quote}」";
}

/// <summary>One statement of the record. Setting <see cref="Text"/> saves a new revision through the owner.</summary>
public sealed partial class StatementRowViewModel : ObservableObject
{
    private readonly Func<StatementRowViewModel, string, bool>? _onEdited;

    internal StatementRowViewModel(RecordStatement statement, IReadOnlyList<EvidenceLink> evidence, Func<StatementRowViewModel, string, bool>? onEdited, string? compare)
    {
        Statement = statement;
        Evidence = evidence;
        _onEdited = onEdited;
        CompareText = compare;
    }

    public RecordStatement Statement { get; private set; }

    public Guid Id => Statement.Id;

    public IReadOnlyList<EvidenceLink> Evidence { get; }

    public bool IsEditable => _onEdited is not null;

    public bool IsReadOnly => _onEdited is null;

    /// <summary>Two-way bound (on lost focus). An empty text or a failed save brings the old text back.</summary>
    public string Text
    {
        get => Statement.Text;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == Statement.Text || _onEdited is null) return;
            if (text.Length == 0 || !_onEdited(this, text)) OnPropertyChanged();
        }
    }

    /// <summary>"AI" / "修正" / "手入力", plus the certainty when the model was not sure.</summary>
    public string OriginText => string.Join("・", new[] { UserMessages.OriginName(Statement.Origin), UserMessages.CertaintyName(Statement.Certainty) }.Where(t => t.Length > 0));

    public bool IsTentative => Statement.Certainty != Certainty.Stated;

    /// <summary>A user-changed statement keeps the evidence of the AI text it started from.</summary>
    public string? EvidenceNote => Statement.Origin == TextOrigin.UserEdited && Evidence.Count > 0 ? "根拠は AI の生成時のもの" : null;

    public bool HasNoEvidence => Evidence.Count == 0 && Statement.Origin == TextOrigin.Ai;

    /// <summary>When an older revision is shown: how this statement differs in the latest one.</summary>
    public string? CompareText { get; }

    internal void Update(RecordStatement statement)
    {
        Statement = statement;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>The summary (要約) or one section of the record.</summary>
public sealed partial class SectionRowViewModel : ObservableObject
{
    private readonly Func<SectionRowViewModel, string, bool>? _onHeadingEdited;

    internal SectionRowViewModel(Guid? id, string heading, bool isSummary, Func<SectionRowViewModel, string, bool>? onHeadingEdited, string? compare)
    {
        Id = id;
        _heading = heading;
        IsSummary = isSummary;
        _onHeadingEdited = onHeadingEdited;
        CompareText = compare;
    }

    /// <summary>Null for the summary.</summary>
    public Guid? Id { get; }

    public bool IsSummary { get; }

    public bool IsEditable { get; internal init; }

    /// <summary>Heading can be changed, moved and deleted (sections only, latest revision only).</summary>
    public bool IsSectionEditable => IsEditable && !IsSummary;

    public bool IsHeadingReadOnly => !IsSectionEditable;

    private string _heading;

    public string Heading
    {
        get => _heading;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == _heading || _onHeadingEdited is null || IsSummary) return;
            if (text.Length == 0 || !_onHeadingEdited(this, text))
            {
                OnPropertyChanged();
                return;
            }
            _heading = text;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<StatementRowViewModel> Statements { get; } = [];

    /// <summary>The text of a statement to add at the end.</summary>
    [ObservableProperty]
    public partial string NewStatementText { get; set; } = string.Empty;

    public string? CompareText { get; }
}

/// <summary>An issue or task candidate (read-only in Phase D: 未反映).</summary>
public sealed class CandidateRowViewModel
{
    internal CandidateRowViewModel(RecordCandidate candidate, IReadOnlyList<EvidenceLink> evidence, string? state = null)
    {
        State = state ?? "未反映";
        var c = candidate.Candidate;
        KindText = UserMessages.CandidateKindName(c.Kind);
        Title = c.Fields.Title;
        Description = c.Fields.Description;
        var details = new List<string>();
        if (c.Fields.Status is { } status) details.Add("状態: " + UserMessages.ItemStatusName(status));
        if (c.Fields.Priority is { } priority) details.Add("優先度: " + UserMessages.PriorityName(priority));
        details.Add("担当: " + (c.Fields.Assignee ?? "未定"));
        details.Add("期限: " + (c.Fields.DueDate?.ToString("yyyy/MM/dd (ddd)", CultureInfo.GetCultureInfo("ja-JP")) ?? (c.DueExpression is { Length: > 0 } expr ? $"未確定（「{expr}」）" : "なし")));
        if (UserMessages.CertaintyName(c.Certainty) is { Length: > 0 } certainty) details.Add(certainty);
        DetailText = string.Join("　", details);
        ConcernText = candidate.Flags.Count == 0 ? null : "要確認: " + string.Join("、", candidate.Flags.Select(UserMessages.ConcernName));
        Evidence = evidence;
        Rationale = c.Rationale;
    }

    public string KindText { get; }

    public string Title { get; }

    public string Description { get; }

    public string DetailText { get; }

    public string? ConcernText { get; }

    public string Rationale { get; }

    /// <summary>未反映, 反映待ち or 反映済み（TSK-000001） (detail spec 10.2, 14.2-8: the record links to what was applied).</summary>
    public string State { get; }

    public IReadOnlyList<EvidenceLink> Evidence { get; }
}

/// <summary>
/// UI-07 record review (detail spec 18.2, 13.1, 13.3): the transcript the record was built on (left) and the
/// record (right) with its summary, sections, evidence and candidates. Statements and headings are edited in place,
/// sections added, moved and deleted, and the record approved or deferred. Every change is a new revision; only the
/// latest revision can be changed, older ones are shown read-only with how they differ from the latest.
/// </summary>
public sealed partial class RecordReviewViewModel : ObservableObject
{
    private readonly IRecordService _records;
    private readonly IAudioPlayer _player;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private string? _directory;
    private RecordStore? _store;
    private RecordDocument? _document;
    private TranscriptDocument? _transcript;
    private Guid? _activeJob;
    private bool _switching;
    private int? _latestTranscriptRevision;
    private (string Directory, int Source, int Current)? _staleFor;

    /// <summary>Set by the window: makes a lookup of where candidates stand on the 「管理」 page (null: not applied).</summary>
    public Func<Func<Core.Management.CandidateKey, string?>>? CandidateStates { get; set; }

    /// <summary>Set by the app: renders a record with the applied candidates marked (14.2-8). Without it, all are 未反映.</summary>
    public Func<string, RecordDocument, bool, string>? Renderer { get; set; }

    private string Render(string directory, RecordDocument record, bool approved) =>
        Renderer?.Invoke(directory, record, approved) ?? RecordMarkdown.RenderFor(directory, record, approved);

    public RecordReviewViewModel(IRecordService records, IAudioPlayer player, IUiDispatcher ui, TimeProvider? time = null)
    {
        _records = records ?? throw new ArgumentNullException(nameof(records));
        _player = player ?? throw new ArgumentNullException(nameof(player));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _time = time ?? TimeProvider.System;
        _records.JobChanged += (_, change) => _ui.Post(() => OnJobChanged(change));
        _records.Progress += (_, p) => _ui.Post(() => OnProgress(p));
        _player.Ended += (_, _) => _ui.Post(() => NowPlaying = null);
        _header = new RecordHeaderRow(this);
        _footer = new RecordFooterRow(this);
        Rows.Add(_header);
        Rows.Add(_footer);
        Blocks.CollectionChanged += OnBlocksChanged;
    }

    private readonly RecordHeaderRow _header;
    private readonly RecordFooterRow _footer;

    /// <summary>
    /// What the record pane lists, in one virtualized list (NFR-02): the title, the summary and sections, then the
    /// footer (add a section, the candidates). Only the rows on screen are built: a 25-minute meeting has about 90
    /// statements, and building them all took 500–700 ms when the record was opened.
    /// </summary>
    public ObservableCollection<object> Rows { get; } = [];

    private void OnBlocksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case System.Collections.Specialized.NotifyCollectionChangedAction.Add when e.NewItems is { } added:
                for (var i = 0; i < added.Count; i++) Rows.Insert(e.NewStartingIndex + 1 + i, added[i]!);
                break;
            case System.Collections.Specialized.NotifyCollectionChangedAction.Remove when e.OldItems is { } removed:
                for (var i = 0; i < removed.Count; i++) Rows.RemoveAt(e.OldStartingIndex + 1);
                break;
            default:
                Rows.Clear();
                Rows.Add(_header);
                foreach (var b in Blocks) Rows.Add(b);
                Rows.Add(_footer);
                break;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession))]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand))]
    public partial string? SessionTitle { get; set; }

    public bool HasSession => SessionTitle is not null;

    public string? SessionDirectory => _directory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoRecord))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(DeferCommand), nameof(AddSectionCommand))]
    public partial bool HasRecord { get; set; }

    public bool HasNoRecord => HasSession && !HasRecord;

    public ObservableCollection<RevisionItem> Revisions { get; } = [];

    [ObservableProperty]
    public partial RevisionItem? SelectedRevision { get; set; }

    /// <summary>The shown revision is the latest (editable).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTitleReadOnly))]
    [NotifyCanExecuteChangedFor(nameof(ApproveCommand), nameof(AddSectionCommand), nameof(AddStatementCommand), nameof(DeleteStatementCommand),
        nameof(DeleteSectionCommand), nameof(MoveSectionUpCommand), nameof(MoveSectionDownCommand), nameof(RegenerateSectionCommand))]
    public partial bool IsLatest { get; set; }

    public bool IsTitleReadOnly => !IsLatest;

    private string _title = string.Empty;

    /// <summary>Two-way bound (on lost focus); a change saves a new revision.</summary>
    public string RecordTitle
    {
        get => _title;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text == _title || !IsLatest) return;
            if (text.Length == 0 || !Apply(next => RecordEditing.EditTitle(_document!, next, text, _time.GetUtcNow()), rebuild: false))
            {
                OnPropertyChanged();
                return;
            }
            _title = text;
            OnPropertyChanged();
        }
    }

    /// <summary>The summary first, then the sections.</summary>
    public ObservableCollection<SectionRowViewModel> Blocks { get; } = [];

    public ObservableCollection<CandidateRowViewModel> Candidates { get; } = [];

    public string CandidatesHeader => Candidates.Count == 0 ? "課題・タスクの候補（なし）" : $"課題・タスクの候補（{Candidates.Count} 件・未反映）";

    /// <summary>Left pane: the transcript revision the record was built on (evidence ids refer to it).</summary>
    public BulkObservableCollection<SegmentRowViewModel> Segments { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlaySelectedCommand))]
    public partial SegmentRowViewModel? SelectedSegment { get; set; }

    [ObservableProperty]
    public partial string? TranscriptCaption { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>The approval no longer matches (newer draft or changed transcript): shown as a caution.</summary>
    [ObservableProperty]
    public partial bool IsOutdated { get; set; }

    [ObservableProperty]
    public partial bool IsDeferred { get; set; }

    /// <summary>
    /// The latest record was built on an older transcript than the one in use (e.g. re-transcribed with a better model):
    /// shown as a caution band with a button to rebuild. Null when they match.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsStaleBand))]
    public partial string? StaleText { get; set; }

    [ObservableProperty]
    public partial string? StaleActionLabel { get; set; }

    /// <summary>Hidden while a generation is queued or running (the job line says what happens).</summary>
    public bool ShowsStaleBand => StaleText is not null && !IsGenerating;

    [ObservableProperty]
    public partial string? WarningsText { get; set; }

    [ObservableProperty]
    public partial string? ProvenanceText { get; set; }

    [ObservableProperty]
    public partial string? JobText { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(RegenerateSectionCommand))]
    [NotifyPropertyChangedFor(nameof(ShowsStaleBand))]
    public partial bool IsGenerating { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    public partial string? NowPlaying { get; set; }

    [ObservableProperty]
    public partial string NewSectionHeading { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewSectionText { get; set; } = string.Empty;

    public string GenerateLabel => HasRecord ? "記録を作り直す(_W)" : "記録を作る(_W)";

    partial void OnHasRecordChanged(bool value) => OnPropertyChanged(nameof(GenerateLabel));

    partial void OnSelectedRevisionChanged(RevisionItem? value)
    {
        if (_switching || value is null || _store is null) return;
        Show(value.Revision);
    }

    /// <summary>Opens the latest record of a session.</summary>
    public void Open(string sessionDirectory, string title)
    {
        ArgumentException.ThrowIfNullOrEmpty(sessionDirectory);
        _directory = sessionDirectory;
        _store = new RecordStore(sessionDirectory);
        _staleFor = null; // the transcripts may have been corrected since the page last showed this session
        SessionTitle = title;
        Message = null;
        JobText = null;
        IsGenerating = false;
        _activeJob = null;
        // A job of this session may already be running or waiting.
        try
        {
            var active = RecordJobStore.List(sessionDirectory).LastOrDefault(j => ProcessingStateMachine.IsActive(j.State));
            if (active is not null)
            {
                _activeJob = active.JobId;
                IsGenerating = true;
                JobText = JobLabel(active.State);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The job list is only for the progress line.
        }
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
            Message = $"記録を読み込めませんでした（{ex.Message}）。";
            return;
        }
        _switching = true;
        Revisions.Clear();
        foreach (var r in revisions.Reverse()) Revisions.Add(new RevisionItem(r, Label(r)));
        _switching = false;
        if (revisions.Count == 0)
        {
            _document = null;
            _latestTranscriptRevision = null;
            StaleText = null;
            HasRecord = false;
            IsLatest = false;
            Blocks.Clear();
            Candidates.Clear();
            OnPropertyChanged(nameof(CandidatesHeader));
            _title = string.Empty;
            OnPropertyChanged(nameof(RecordTitle));
            StatusText = IsGenerating ? null : "まだ記録がありません。「記録を作る」で、推論エンジンを使って要約と課題・タスクの候補を作ります。";
            WarningsText = null;
            ProvenanceText = null;
            LoadTranscript(null);
            return;
        }
        Show(revision ?? revisions[^1]);
    }

    private string Label(int revision)
    {
        try
        {
            var doc = _store!.Load(revision);
            var kind = doc.Kind switch
            {
                RecordRevisionKind.Generated => "生成",
                RecordRevisionKind.SectionRegenerated => "章の再生成",
                _ => "編集",
            };
            var approved = _store.ReadReview().Approved?.Revision == revision ? "　［承認済み］" : string.Empty;
            return $"Rev {revision}  {kind}（{doc.CreatedAtUtc.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture)}）{approved}";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return $"Rev {revision}  （読み込めません）";
        }
    }

    private void Show(int revision)
    {
        try
        {
            var doc = _store!.Load(revision);
            var latestRevision = _store.Revisions()[^1];
            var latest = revision == latestRevision ? doc : _store.Load(latestRevision);
            _document = doc;
            _latestTranscriptRevision = latest.TranscriptRevision;
            HasRecord = true;
            IsLatest = revision == latestRevision;
            _switching = true;
            SelectedRevision = Revisions.FirstOrDefault(r => r.Revision == revision);
            _switching = false;
            LoadTranscript(doc.TranscriptRevision);
            _title = doc.Title;
            OnPropertyChanged(nameof(RecordTitle));
            BuildBlocks(doc, IsLatest ? null : latest);
            Candidates.Clear();
            var analysis = doc.Provenance.AnalysisRevision ?? doc.Revision;
            var states = doc.Candidates.Count > 0 ? CandidateStates?.Invoke() : null; // one lookup for all candidates
            foreach (var c in doc.Candidates)
                Candidates.Add(new CandidateRowViewModel(c, Links(c.Candidate.Evidence), states?.Invoke(new Core.Management.CandidateKey(doc.SessionId, analysis, c.Candidate.CandidateId))));
            OnPropertyChanged(nameof(CandidatesHeader));
            UpdateStatus();
            var p = doc.Provenance;
            ProvenanceText = $"Rev {doc.Revision}: 文字起こし Rev {doc.TranscriptRevision} から" +
                (p.ModelId is null ? string.Empty : $"、{p.ModelId}（プロンプト {p.PromptVersion}）") +
                $"、{doc.CreatedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)} 作成" +
                (IsLatest ? string.Empty : "　※ 最新ではない Revision は表示のみです。最新との違いを各文の下に示します");
            WarningsText = Warnings(doc);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Message = $"記録の Rev {revision} を読み込めませんでした（{ex.Message}）。";
        }
    }

    private void LoadTranscript(int? revision)
    {
        Segments.Clear();
        _transcript = null;
        if (_directory is null) return;
        var store = new TranscriptStore(_directory);
        try
        {
            var inUse = store.CurrentRevision();
            var shown = revision ?? inUse;
            if (shown is null)
            {
                TranscriptCaption = "文字起こしがまだありません。";
                return;
            }
            _transcript = store.Load(shown.Value);
            Segments.ReplaceAll(_transcript.Segments.Select(s => new SegmentRowViewModel(s, null)));
            TranscriptCaption = revision is null || revision == inUse
                ? $"文字起こし Rev {shown}（使用中）"
                : $"記録の元の文字起こし Rev {shown}（使用中は Rev {inUse}。根拠はこの版の区間を指します）";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TranscriptCaption = $"文字起こしを読み込めませんでした（{ex.Message}）。";
        }
    }

    private IReadOnlyList<EvidenceLink> Links(IEnumerable<Evidence> evidence) =>
        [.. evidence.GroupBy(e => e.SegmentId).Select(g =>
        {
            var segment = _transcript?.Find(g.Key);
            var label = segment is null ? "根拠なし" : $"{TranscriptRendering.Time(segment.StartMs, false)} {UserMessages.SourceName(segment.Source)}";
            return new EvidenceLink(g.Key, label, string.Join(" … ", g.Select(e => e.Quote)), segment is not null);
        })];

    private void BuildBlocks(RecordDocument doc, RecordDocument? latest)
    {
        Blocks.Clear();
        var editable = latest is null;
        var latestStatements = latest?.AllStatements.ToDictionary(s => s.Id);
        var latestSections = latest?.Sections.ToDictionary(s => s.Id);

        string? Compare(RecordStatement s) => latestStatements is null ? null
            : !latestStatements.TryGetValue(s.Id, out var now) ? "→ 最新では削除"
            : now.Text != s.Text ? $"→ 最新: {now.Text}"
            : null;

        var summary = new SectionRowViewModel(null, "要約", isSummary: true, null, null) { IsEditable = editable };
        foreach (var s in doc.Summary) summary.Statements.Add(new StatementRowViewModel(s, Links(s.Evidence), editable ? SaveStatement : null, Compare(s)));
        Blocks.Add(summary);
        foreach (var section in doc.Sections)
        {
            var compare = latestSections is null ? null
                : !latestSections.TryGetValue(section.Id, out var now) ? "→ 最新では削除"
                : now.Heading != section.Heading ? $"→ 最新の見出し: {now.Heading}"
                : null;
            var row = new SectionRowViewModel(section.Id, section.Heading, isSummary: false, editable ? SaveHeading : null, compare) { IsEditable = editable };
            foreach (var s in section.Statements) row.Statements.Add(new StatementRowViewModel(s, Links(s.Evidence), editable ? SaveStatement : null, Compare(s)));
            Blocks.Add(row);
        }
    }

    private static string? Warnings(RecordDocument doc)
    {
        var lines = new List<string>();
        if (doc.MissingPieces.Count > 0)
        {
            var spans = string.Join("、", doc.MissingPieces.Take(3).Select(p => $"{TranscriptRendering.Time(p.StartMs, false)}–{TranscriptRendering.Time(p.EndMs, false)}"));
            lines.Add($"部分生成: {doc.MissingPieces.Count} 区間（{spans}{(doc.MissingPieces.Count > 3 ? " など" : string.Empty)}）を整理できませんでした。この範囲は記録に入っていません。");
        }
        if (doc.Unsupported.Count > 0) lines.Add($"根拠確認が必要: 文字起こしと結び付けられなかった {doc.Unsupported.Count} 件を記録から外しました。");
        // Records made before prompt 2026-11-04.1 may carry a copy of the instructions as a "warning".
        lines.AddRange(doc.Warnings.Where(w => !AnalysisPrompts.EchoesInstructions(w)));
        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    private void UpdateStatus()
    {
        if (_store is null || _directory is null) return;
        try
        {
            var inUse = new TranscriptStore(_directory).CurrentRevision();
            var status = _store.Status(inUse);
            var approved = _store.ReadReview().Approved;
            IsDeferred = status.State == RecordReviewState.Deferred;
            IsOutdated = status.State == RecordReviewState.ApprovedOutdated || (approved is not null && status.TranscriptChanged);
            var text = status.State switch
            {
                RecordReviewState.Draft => $"草稿です（未承認）。内容と根拠を確かめて「承認」を押してください。",
                RecordReviewState.Deferred => "保留中です。",
                RecordReviewState.Approved => $"Rev {status.ApprovedRevision} を承認済みです（{approved!.ApprovedAtUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}）。",
                RecordReviewState.ApprovedOutdated => $"Rev {status.ApprovedRevision} を承認済みですが、" +
                    (status.TranscriptChanged ? $"承認後に元の文字起こしが変わりました（承認時 Rev {approved!.TranscriptRevision} → 使用中 Rev {inUse}）。" : string.Empty) +
                    (status.NewerDraft ? $"それより新しい草稿 Rev {status.LatestRevision} があります。" : string.Empty),
                _ => null,
            };
            StatusText = text;
            UpdateStale(inUse);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"承認の状態を読み込めませんでした（{ex.Message}）。";
        }
    }

    /// <summary>
    /// The band for a record built on an older transcript than the one in use. It looks at the latest record revision,
    /// so browsing an older record revision does not raise it when the latest is up to date.
    /// </summary>
    private void UpdateStale(int? inUse)
    {
        if (_directory is null || inUse is not { } current || _latestTranscriptRevision is not { } source || source == current)
        {
            _staleFor = null;
            StaleText = null;
            StaleActionLabel = null;
            return;
        }
        // Called after every edit: read the two transcripts (for the model names) only when the pair changes.
        if (_staleFor == (_directory, source, current)) return;
        _staleFor = (_directory, source, current);
        var store = new TranscriptStore(_directory);
        StaleText = $"この記録は、古い文字起こし（Rev {source}{ModelSuffix(store, source)}）から作りました。いま使っている文字起こしは Rev {current}{ModelSuffix(store, current)} です。" +
            "記録には、古い文字起こしの誤りや抜けが残っていることがあります。";
        StaleActionLabel = $"Rev {current} の文字起こしで作り直す";
    }

    /// <summary>"・medium" when the revision is a recognition result; nothing for a manual correction or when unreadable.</summary>
    private static string ModelSuffix(TranscriptStore store, int revision)
    {
        try
        {
            return store.Load(revision).Provenance?.ModelName is { } model ? "・" + UserMessages.ModelShortName(model) : string.Empty;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Saves the next revision built by <paramref name="edit"/> from the latest. Returns false (and shows why) when
    /// it cannot. <paramref name="rebuild"/> redraws the record (structure changes); text edits update in place so the
    /// focus stays where the user is typing.
    /// </summary>
    private bool Apply(Func<int, RecordDocument> edit, bool rebuild, string? done = null)
    {
        if (_store is null || _document is null || _directory is null || !IsLatest) return false;
        try
        {
            var previous = _document.Revision;
            var next = edit(_store.NextRevision());
            var directory = _directory;
            _store.Save(next, d => Render(directory, d, approved: false));
            _document = next;
            _switching = true;
            var index = Revisions.IndexOf(Revisions.First(r => r.Revision == previous));
            Revisions[index] = new RevisionItem(previous, Label(previous));
            Revisions.Insert(0, new RevisionItem(next.Revision, Label(next.Revision)));
            SelectedRevision = Revisions[0];
            _switching = false;
            if (rebuild) Show(next.Revision);
            else UpdateStatus();
            Message = done ?? $"Rev {next.Revision} として保存しました（承認版の record.md は変わりません）。";
            _ = _records.RecordSavedAsync(directory);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or InvalidDataException)
        {
            // InvalidOperationException: another revision was written meanwhile (a generation finished).
            Message = $"保存できませんでした（{ex.Message}）。記録を開き直してから直してください。";
            return false;
        }
    }

    private bool SaveStatement(StatementRowViewModel row, string text)
    {
        var ok = Apply(next => RecordEditing.EditStatement(_document!, next, row.Id, text, _time.GetUtcNow()), rebuild: false);
        if (ok && _document!.AllStatements.FirstOrDefault(s => s.Id == row.Id) is { } updated) row.Update(updated);
        return ok;
    }

    private bool SaveHeading(SectionRowViewModel row, string heading) =>
        row.Id is { } id && Apply(next => RecordEditing.EditHeading(_document!, next, id, heading, _time.GetUtcNow()), rebuild: false);

    private bool CanEdit() => IsLatest && HasRecord;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteStatement(StatementRowViewModel? row)
    {
        if (row is null) return;
        Apply(next => RecordEditing.DeleteStatement(_document!, next, row.Id, _time.GetUtcNow()), rebuild: true, "文を削除しました（前の Revision には残っています）。");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddStatement(SectionRowViewModel? section)
    {
        if (section is null) return;
        var text = section.NewStatementText.Trim();
        if (text.Length == 0)
        {
            Message = "追加する文を入力してください。";
            return;
        }
        if (Apply(next => RecordEditing.AddStatement(_document!, next, section.Id, null, text, _time.GetUtcNow()), rebuild: true, "文を追加しました（手入力の文には根拠がありません）。"))
            section.NewStatementText = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DeleteSection(SectionRowViewModel? section)
    {
        if (section?.Id is not { } id) return;
        Apply(next => RecordEditing.DeleteSection(_document!, next, id, _time.GetUtcNow()), rebuild: true, $"章「{section.Heading}」を削除しました（前の Revision には残っています）。");
    }

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveSectionUp(SectionRowViewModel? section) => Move(section, -1);

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void MoveSectionDown(SectionRowViewModel? section) => Move(section, 1);

    private void Move(SectionRowViewModel? section, int offset)
    {
        if (section?.Id is not { } id || _document is null) return;
        var index = _document.Sections.ToList().FindIndex(s => s.Id == id);
        if (index + offset < 0 || index + offset >= _document.Sections.Count) return;
        Apply(next => RecordEditing.MoveSection(_document!, next, id, offset, _time.GetUtcNow()), rebuild: true, "章を移動しました。");
    }

    private bool CanAddSection() => IsLatest && HasRecord;

    [RelayCommand(CanExecute = nameof(CanAddSection))]
    private void AddSection()
    {
        var heading = NewSectionHeading.Trim();
        var text = NewSectionText.Trim();
        if (heading.Length == 0 || text.Length == 0)
        {
            Message = "追加する章の見出しと最初の文を入力してください。";
            return;
        }
        if (Apply(next => RecordEditing.AddSection(_document!, next, _document!.Sections.Count, heading, text, _time.GetUtcNow()), rebuild: true, $"章「{heading}」を追加しました。"))
        {
            NewSectionHeading = string.Empty;
            NewSectionText = string.Empty;
        }
    }

    private bool CanApprove() => IsLatest && HasRecord;

    /// <summary>Approves the latest revision: <c>record.md</c> becomes its rendering. Candidates stay 未反映 (10.2).</summary>
    [RelayCommand(CanExecute = nameof(CanApprove))]
    private void Approve()
    {
        if (_store is null || _document is null || _directory is null) return;
        var directory = _directory;
        try
        {
            var result = _store.Approve(_document.Revision, _document.TranscriptRevision, _time.GetUtcNow(), d => Render(directory, d, approved: true));
            Reload(_document.Revision);
            Message = $"Rev {result.Approval.Revision} を承認し、record.md に保存しました（課題・タスクの候補は、「管理」タブで反映するまで管理ファイルには入りません）。" +
                (result.PreservedExternalEdit is { } kept ? $"\nアプリの外で変更された record.md は {Path.GetRelativePath(directory, kept)} に残しました。" : string.Empty);
            _ = _records.RecordSavedAsync(directory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"承認できませんでした（{ex.Message}）。";
        }
    }

    private bool CanDefer() => HasRecord;

    /// <summary>Marks the record as deferred (保留), or takes it back to review.</summary>
    [RelayCommand(CanExecute = nameof(CanDefer))]
    private void Defer()
    {
        if (_store is null || _directory is null) return;
        try
        {
            var deferred = !IsDeferred;
            _store.SetDeferred(deferred, _time.GetUtcNow());
            UpdateStatus();
            Message = deferred ? "保留にしました。" : "保留を解除しました。";
            _ = _records.RecordSavedAsync(_directory);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"保留を変更できませんでした（{ex.Message}）。";
        }
    }

    private bool CanGenerate() => HasSession && !IsGenerating;

    /// <summary>Queues a new generation from the transcript in use (the current record stays as an older revision).</summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void Generate()
    {
        if (_directory is null) return;
        try
        {
            if (new TranscriptStore(_directory).CurrentRevision() is null)
            {
                Message = "文字起こしがまだありません。先に文字起こしをしてください。";
                return;
            }
            var job = _records.Enqueue(_directory);
            _activeJob = job.JobId;
            IsGenerating = true;
            JobText = JobLabel(ProcessingState.Queued);
            Message = HasRecord ? "記録の作り直しを順番待ちに入れました。今の記録は前の Revision として残ります。" : "記録の作成を順番待ちに入れました。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"記録の作成を始められませんでした（{ex.Message}）。";
        }
    }

    private bool CanRegenerateSection() => IsLatest && HasRecord && !IsGenerating;

    /// <summary>Regenerates one section from the segments its statements rest on; the rest of the record stays.</summary>
    [RelayCommand(CanExecute = nameof(CanRegenerateSection))]
    private void RegenerateSection(SectionRowViewModel? section)
    {
        if (_directory is null || section?.Id is not { } id) return;
        if (section.Statements.All(s => s.Evidence.Count == 0))
        {
            Message = $"章「{section.Heading}」には根拠の区間がないため、作り直せません（手入力の章など）。";
            return;
        }
        try
        {
            var job = _records.EnqueueSection(_directory, id);
            _activeJob = job.JobId;
            IsGenerating = true;
            JobText = JobLabel(ProcessingState.Queued);
            Message = $"章「{section.Heading}」の作り直しを順番待ちに入れました。根拠の区間だけを推論エンジンに渡します。今の内容は前の Revision に残ります。";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"章の作り直しを始められませんでした（{ex.Message}）。";
        }
    }

    /// <summary>The user wants to review this session's candidates on the 「管理」 page.</summary>
    public event EventHandler<Guid>? OpenManagementRequested;

    [RelayCommand]
    private void OpenManagement()
    {
        if (_document is { } doc) OpenManagementRequested?.Invoke(this, doc.SessionId);
    }

    [RelayCommand]
    private void ShowEvidence(EvidenceLink? link)
    {
        if (link is null) return;
        var row = Segments.FirstOrDefault(s => s.SegmentId == link.SegmentId);
        if (row is null)
        {
            Message = "根拠の区間が、表示している文字起こしにありません。";
            return;
        }
        SelectedSegment = row;
        Play(row);
    }

    private bool CanPlaySelected() => SelectedSegment is not null;

    [RelayCommand(CanExecute = nameof(CanPlaySelected))]
    private void PlaySelected() => Play(SelectedSegment);

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

    private static string JobLabel(ProcessingState state) => state switch
    {
        ProcessingState.Queued => "記録の作成: 順番待ち（録音中や文字起こし中は待ちます）",
        ProcessingState.Organizing => "記録の作成: 推論エンジンで整理しています…",
        ProcessingState.Validating => "記録の作成: 結果を検証しています…",
        _ => UserMessages.ProcessingStateName(state),
    };

    private void OnProgress(GenerationProgress progress)
    {
        if (_activeJob is null || _records.CurrentJobId != _activeJob) return;
        var step = progress.Step switch
        {
            "extract" => "区間ごとに整理",
            "integrate" => "全体をまとめる",
            "validate" => "検証",
            "done" => "保存",
            _ => progress.Step,
        };
        JobText = progress.Total > 0 ? $"記録の作成: {step}（{progress.Done}/{progress.Total}）" : $"記録の作成: {step}…";
    }

    private void OnJobChanged(RecordJobChange change)
    {
        if (_directory is null || !string.Equals(change.SessionDirectory, _directory, StringComparison.OrdinalIgnoreCase)) return;
        var job = change.Job;
        if (ProcessingStateMachine.IsActive(job.State))
        {
            _activeJob = job.JobId;
            IsGenerating = true;
            JobText = JobLabel(job.State);
            return;
        }
        if (job.JobId != _activeJob) return;
        _activeJob = null;
        IsGenerating = false;
        JobText = null;
        switch (job.State)
        {
            case ProcessingState.ReadyForReview:
                Reload(null);
                Message = job.FailedPieces.Count > 0
                    ? $"記録 Rev {job.RecordRevision} を作りました（部分生成: {job.FailedPieces.Count} 区間を整理できませんでした）。"
                    : $"記録 Rev {job.RecordRevision} を作りました（{job.ElapsedSeconds:0} 秒）。内容と根拠を確かめてください。";
                break;
            case ProcessingState.NotAnalyzable:
                Reload(null);
                Message = "文字起こしに記録にする内容がありませんでした（無音や認識なし）。記録は作っていません。";
                break;
            default:
                Message = UserMessages.RecordError(job.Error, job.ErrorDetail);
                break;
        }
    }
}

/// <summary>The first row of the record pane (the title); <see cref="Owner"/> is the page.</summary>
public sealed record RecordHeaderRow(RecordReviewViewModel Owner);

/// <summary>The last row of the record pane (add a section, the candidates, provenance).</summary>
public sealed record RecordFooterRow(RecordReviewViewModel Owner);
