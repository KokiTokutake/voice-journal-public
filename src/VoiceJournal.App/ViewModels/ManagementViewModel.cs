using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VoiceJournal.App.Services;
using VoiceJournal.Core.Analysis;
using VoiceJournal.Core.Management;
using VoiceJournal.Core.Records;
using VoiceJournal.Infrastructure.Storage;

namespace VoiceJournal.App.ViewModels;

/// <summary>A session that has a record, in the 「管理」 page's list.</summary>
public sealed record SessionChoice(ReviewableSession Session)
{
    public string Label => $"{Session.StartedAtUtc.ToLocalTime():yyyy/MM/dd HH:mm}  {Session.Title ?? "（無題）"}（候補 {Session.Candidates.ToString(CultureInfo.InvariantCulture)} 件）";
}

/// <summary>An existing item offered as the target of an update or a mention, or as a parent issue.</summary>
public sealed record ItemChoice(ManagementItem Item, string? Note)
{
    public string Label => Note is null ? $"{Item.DisplayId} {Item.Title}" : $"{Item.DisplayId} {Item.Title}（{Note}）";
}

/// <summary>One choice of the radio group (新規・更新・関連・却下・保留).</summary>
public sealed partial class ChoiceOption : ObservableObject
{
    private readonly Action<ChoiceOption> _picked;

    internal ChoiceOption(ReviewChoice choice, string label, bool isChecked, bool isEnabled, Action<ChoiceOption> picked)
    {
        Choice = choice;
        Label = label;
        _isChecked = isChecked;
        IsEnabled = isEnabled;
        _picked = picked;
    }

    public ReviewChoice Choice { get; }

    public string Label { get; }

    public bool IsEnabled { get; }

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value) || !value) return;
            _picked(this);
        }
    }

    internal void Uncheck()
    {
        _isChecked = false;
        OnPropertyChanged(nameof(IsChecked));
    }
}

/// <summary>A candidate card (UI-08): what was said, the existing items it may be, and the user's choice.</summary>
public sealed partial class CandidateCardViewModel : ObservableObject
{
    private readonly Action<CandidateCardViewModel> _changed;
    private readonly IReadOnlyDictionary<Guid, ManagementItem> _items;

    internal CandidateCardViewModel(CandidateReview review, DuplicateReport duplicates, IReadOnlyList<ManagementItem> items,
        IReadOnlyList<string> evidence, Action<CandidateCardViewModel> changed)
    {
        Review = review;
        Duplicates = duplicates;
        Evidence = evidence;
        _changed = changed;
        _items = items.ToDictionary(i => i.Id);
        var c = review.Candidate;
        var notes = duplicates.Matches.ToDictionary(m => m.Item.Id, m => MatchNote(m));
        Targets = [.. items.Where(i => i.Kind == c.Kind)
            .OrderByDescending(i => notes.ContainsKey(i.Id)).ThenByDescending(i => duplicates.Matches.FirstOrDefault(m => m.Item.Id == i.Id)?.Score ?? 0)
            .Select(i => new ItemChoice(i, notes.GetValueOrDefault(i.Id)))];
        ParentIssues = [.. items.Where(i => i.Kind == CandidateKind.Issue).Select(i => new ItemChoice(i, null))];
        _selectedTarget = review.TargetItemId is { } t ? Targets.FirstOrDefault(x => x.Item.Id == t) : null;
        _selectedParent = review.ParentIssueId is { } p ? ParentIssues.FirstOrDefault(x => x.Item.Id == p) : null;
        var hasTargets = Targets.Count > 0;
        Choices =
        [
            new(ReviewChoice.Create, "新規", review.Choice == ReviewChoice.Create, true, Pick),
            new(ReviewChoice.Update, "既存を更新", review.Choice == ReviewChoice.Update, hasTargets, Pick),
            new(ReviewChoice.Mention, "関連だけ", review.Choice == ReviewChoice.Mention, hasTargets, Pick),
            new(ReviewChoice.Reject, "却下", review.Choice == ReviewChoice.Reject, true, Pick),
            new(ReviewChoice.Defer, "保留", review.Choice == ReviewChoice.Defer, true, Pick),
        ];
    }

    public CandidateReview Review { get; private set; }

    public DuplicateReport Duplicates { get; }

    public IReadOnlyList<string> Evidence { get; }

    public IReadOnlyList<ChoiceOption> Choices { get; }

    public IReadOnlyList<ItemChoice> Targets { get; }

    public IReadOnlyList<ItemChoice> ParentIssues { get; }

    public string KindText => Review.Candidate.Kind == CandidateKind.Issue ? "課題" : "タスク";

    public string ActionText => Review.Candidate.Action switch
    {
        CandidateAction.Update => "AI の提案: 既存の更新",
        CandidateAction.Mention => "AI の提案: 既存への言及",
        _ => "AI の提案: 新規",
    };

    public bool IsTask => Review.Candidate.Kind == CandidateKind.Task;

    /// <summary>The parent issue box, for a task when there are issues to choose from.</summary>
    public bool ShowParent => IsTask && ParentIssues.Count > 0;

    public string Title
    {
        get => Review.Fields.Title;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (text.Length == 0 || text == Review.Fields.Title) { OnPropertyChanged(); return; }
            Update(Review with { EditedFields = Review.Fields with { Title = text } });
        }
    }

    public string DetailText
    {
        get
        {
            var f = Review.Fields;
            var parts = new List<string>
            {
                "担当: " + (f.Assignee ?? "未設定"),
                "期限: " + (f.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? (Review.Candidate.DueExpression is { } e ? $"未確定（「{e}」）" : "未設定")),
                "状態: " + (f.Status is { } s ? ItemStatuses.Label(s) : "未設定"),
            };
            if (Review.Candidate.Certainty != Certainty.Stated) parts.Add(UserMessages.CertaintyName(Review.Candidate.Certainty));
            return string.Join("　", parts);
        }
    }

    public string? Description => Review.Fields.Description.Length > 0 ? Review.Fields.Description : null;

    public string? ConcernText => Review.Concerns.Count > 0 ? "要確認: " + string.Join("・", Review.Concerns.Select(UserMessages.ConcernName)) : null;

    public string? DuplicateText => Duplicates switch
    {
        { Explicit: ExplicitTargetState.NotFound } => "AI が示した既存の項目が見つかりません。自動では対応付けません",
        { Explicit: ExplicitTargetState.WrongKind } => "AI が示した既存の項目は種別が違います。自動では対応付けません",
        { Ambiguous: true } => "タイトルが短すぎて照合できません。既存の項目を手で選んでください",
        { Matches.Count: > 0 } m => "似た既存の項目: " + string.Join("、", m.Matches.Select(x => $"{x.Item.DisplayId}（{MatchNote(x)}）")),
        _ => null,
    };

    public bool NeedsTarget => Review.Choice is ReviewChoice.Update or ReviewChoice.Mention;

    private ItemChoice? _selectedTarget;

    public ItemChoice? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!SetProperty(ref _selectedTarget, value)) return;
            var fields = value is null ? null : DefaultFieldsToChange(value.Item);
            Update(Review with { TargetItemId = value?.Item.Id, TargetRevision = value?.Item.Revision, FieldsToChange = fields, ConfirmedIndividually = false });
        }
    }

    private ItemChoice? _selectedParent;

    public ItemChoice? SelectedParent
    {
        get => _selectedParent;
        set
        {
            if (!SetProperty(ref _selectedParent, value)) return;
            Update(Review with { ParentIssueId = value?.Item.Id });
        }
    }

    public IReadOnlyList<IndividualCheck> Checks => CandidateRules.Checks(Review, Target);

    public bool NeedsConfirmation => Review.Choice is ReviewChoice.Create or ReviewChoice.Update or ReviewChoice.Mention && Checks.Count > 0 && !Checks.Contains(IndividualCheck.Conflict);

    public string ConfirmationLabel => "個別に確認しました（" + string.Join("・", Checks.Select(CandidateRules.Label)) + "）";

    public bool ConfirmedIndividually
    {
        get => Review.ConfirmedIndividually;
        set
        {
            if (value == Review.ConfirmedIndividually) return;
            Update(Review with { ConfirmedIndividually = value });
        }
    }

    /// <summary>Why the card will not enter the plan (null when it will).</summary>
    public string? StateText => Review.Choice is null ? "未確認"
        : Review.Choice is ReviewChoice.Reject ? "却下（計画に入れません）"
        : Review.Choice is ReviewChoice.Defer ? "保留（計画に入れません）"
        : CandidateRules.IsReadyForPlan(Review, Target, out var reason) ? "承認（計画に入れます）" : reason;

    public bool IsReady => CandidateRules.IsReadyForPlan(Review, Target, out _);

    private ManagementItem? Target => Review.TargetItemId is { } id ? _items.GetValueOrDefault(id) : null;

    /// <summary>「すべて選択」: plain new candidates only (13.2).</summary>
    internal bool SelectIfPlain()
    {
        if (!CandidateRules.IsSelectableByAll(Review)) return false;
        Choices[0].IsChecked = true;
        return true;
    }

    private void Pick(ChoiceOption option)
    {
        foreach (var o in Choices.Where(o => o != option)) o.Uncheck();
        var review = Review with { Choice = option.Choice, ConfirmedIndividually = false };
        if (option.Choice is ReviewChoice.Update or ReviewChoice.Mention && review.TargetItemId is null && Targets.Count > 0 && Targets[0] is { } first)
        {
            _selectedTarget = first;
            OnPropertyChanged(nameof(SelectedTarget));
            review = review with { TargetItemId = first.Item.Id, TargetRevision = first.Item.Revision, FieldsToChange = DefaultFieldsToChange(first.Item) };
        }
        Update(review);
    }

    // An update changes only the fields the candidate names; when it names none, the fields that differ and are set.
    private List<ItemField> DefaultFieldsToChange(ManagementItem target)
    {
        if (Review.Candidate.ChangedFields.Count > 0) return [.. Review.Candidate.ChangedFields];
        var f = Review.Fields;
        var list = new List<ItemField>();
        if (f.Description.Length > 0 && f.Description != target.Description) list.Add(ItemField.Description);
        if (f.Status is { } s && s != target.Status) list.Add(ItemField.Status);
        if (f.Assignee is not null && f.Assignee != target.Assignee) list.Add(ItemField.Assignee);
        if (f.DueDate is not null && f.DueDate != target.DueDate) list.Add(ItemField.DueDate);
        if (f.Priority is not null && f.Priority != target.Priority) list.Add(ItemField.Priority);
        return list;
    }

    private void Update(CandidateReview review)
    {
        Review = review;
        OnPropertyChanged(string.Empty);
        _changed(this);
    }

    private static string MatchNote(DuplicateMatch m)
    {
        var parts = new List<string> { m.ExactTitle ? "同じタイトル" : m.Strong ? $"強く類似 {m.Score:0.00}" : $"類似 {m.Score:0.00}" };
        parts.AddRange(m.Differences.Select(UserMessages.DifferenceName));
        return string.Join("・", parts);
    }
}

/// <summary>A file of the plan in the final check (UI-09).</summary>
public sealed record PlannedFileRow(string Path, string Operation, string Diff)
{
    public string Header => $"{Path}（{(Operation == "create" ? "新規作成" : "更新")}）";
}

/// <summary>An item of the management list (UI-10).</summary>
public sealed record ItemRow(ManagementItem Item)
{
    public string DisplayId => Item.DisplayId;

    public string Title => Item.Title;

    public string Status => ItemStatuses.Label(Item.Status);

    public string Assignee => Item.Assignee ?? "未設定";

    public string Due => Item.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "未設定";

    public string Project => Item.Project;
}

/// <summary>
/// 「管理」 page (UI-08 candidate cards, UI-09 final check, UI-10 list). Phase E: decisions are saved as they are made,
/// and an approved selection becomes a change plan saved for Phase F. Nothing is written to the management files yet.
/// </summary>
public sealed partial class ManagementViewModel : ObservableObject
{
    private readonly ManagementStore _store;
    private readonly IDialogService _dialogs;
    private readonly TimeProvider _time;
    private ChangePlan? _plan;

    public ManagementViewModel(ManagementStore store, IDialogService dialogs, TimeProvider time)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    public ObservableCollection<SessionChoice> Sessions { get; } = [];

    public ObservableCollection<CandidateCardViewModel> Cards { get; } = [];

    public ObservableCollection<ItemRow> Items { get; } = [];

    public ObservableCollection<PlannedFileRow> PlannedFiles { get; } = [];

    [ObservableProperty]
    public partial SessionChoice? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan))]
    public partial string? PlanSummary { get; set; }

    [ObservableProperty]
    public partial string? PlanNotes { get; set; }

    public bool HasPlan => PlanSummary is not null;

    public bool HasCards => Cards.Count > 0;

    public string ItemsTitle => Items.Count == 0
        ? "管理一覧（まだ項目はありません。変更計画の反映は Phase F で実装します）"
        : $"管理一覧（{Items.Count.ToString(CultureInfo.InvariantCulture)} 件）";

    public string ReadyText => $"承認 {Cards.Count(c => c.IsReady).ToString(CultureInfo.InvariantCulture)} 件／候補 {Cards.Count.ToString(CultureInfo.InvariantCulture)} 件";

    /// <summary>Loads the sessions and the item list; keeps the selected session when it is still there.</summary>
    [RelayCommand]
    public void Refresh()
    {
        var selected = SelectedSession?.Session.SessionId;
        Sessions.Clear();
        foreach (var s in _store.SessionsWithRecords()) Sessions.Add(new SessionChoice(s));
        Items.Clear();
        foreach (var i in _store.Items()) Items.Add(new ItemRow(i));
        OnPropertyChanged(nameof(ItemsTitle));
        var again = Sessions.FirstOrDefault(s => s.Session.SessionId == selected) ?? Sessions.FirstOrDefault();
        if (again != SelectedSession) SelectedSession = again;
        else LoadCards();
        if (Sessions.Count == 0) Message = "記録のあるセッションがまだありません。ライブラリでセッションを選び、「記録を開く」から記録を作ると、ここで課題・タスクの候補を確かめられます。";
    }

    /// <summary>From the record page: show this session's candidates.</summary>
    public void Open(Guid sessionId)
    {
        Refresh();
        if (Sessions.FirstOrDefault(s => s.Session.SessionId == sessionId) is { } s) SelectedSession = s;
    }

    partial void OnSelectedSessionChanged(SessionChoice? value) => LoadCards();

    private void LoadCards()
    {
        Cards.Clear();
        ClearPlan();
        Message = null;
        if (SelectedSession is not { } choice)
        {
            OnPropertyChanged(nameof(HasCards));
            OnPropertyChanged(nameof(ReadyText));
            return;
        }
        var records = new RecordStore(choice.Session.Directory);
        var status = records.Status(null);
        var revision = status.ApprovedRevision ?? status.LatestRevision;
        if (revision is null)
        {
            Message = "このセッションには記録がありません。";
            return;
        }
        var record = records.Load(revision.Value);
        var items = _store.Items();
        var stored = _store.Reviews(choice.Session.SessionId);
        var transcripts = new Core.Transcription.TranscriptStore(choice.Session.Directory);
        var segments = transcripts.Revisions().Contains(record.TranscriptRevision) ? transcripts.Load(record.TranscriptRevision) : null;
        foreach (var review in ReviewSource.FromRecord(record, choice.Session.Project))
        {
            var r = stored.TryGetValue(review.Key, out var s) ? review with
            {
                Choice = s.Choice, TargetItemId = s.TargetItemId, TargetRevision = s.TargetRevision, EditedFields = s.EditedFields,
                FieldsToChange = s.FieldsToChange, ParentIssueId = s.ParentIssueId, ConfirmedIndividually = s.Confirmed,
            } : review;
            var evidence = review.Candidate.Evidence.Select(e =>
            {
                var seg = segments?.Find(e.SegmentId);
                return seg is null ? $"「{e.Quote}」" : $"{TimeSpan.FromMilliseconds(seg.StartMs):hh\\:mm\\:ss} 「{e.Quote}」";
            }).ToList();
            Cards.Add(new CandidateCardViewModel(r, DuplicateFinder.Find(r.Candidate, r.Project, items), items, evidence, OnCardChanged));
        }
        if (revision != status.LatestRevision) Message = $"承認済みの記録（Revision {revision}）の候補を表示しています。";
        else if (status.ApprovedRevision is null) Message = "記録はまだ承認されていません。候補の承認は記録の承認と別に行えます（AT-11）。";
        if (Cards.Count == 0) Message = "この記録には課題・タスクの候補がありません。";
        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(ReadyText));
    }

    private void OnCardChanged(CandidateCardViewModel card)
    {
        _store.SaveReview(card.Review);
        ClearPlan();
        OnPropertyChanged(nameof(ReadyText));
    }

    /// <summary>「通常の新規候補をすべて選択」: completion, reopening, date or assignee changes and concerns stay unchecked (13.2).</summary>
    [RelayCommand]
    private void SelectPlain()
    {
        var n = Cards.Count(c => c.SelectIfPlain());
        Message = n > 0 ? $"通常の新規候補 {n} 件を「新規」にしました。要確認の候補は 1 件ずつ選んでください。" : "「すべて選択」の対象（要確認の目印のない新規候補）はありません。";
    }

    /// <summary>UI-09: builds the plan from the approved cards and shows the diff per file.</summary>
    [RelayCommand]
    private void BuildPlan()
    {
        ClearPlan();
        var approved = Cards.Where(c => c.Review.Choice is ReviewChoice.Create or ReviewChoice.Update or ReviewChoice.Mention).Select(c => c.Review).ToList();
        if (approved.Count == 0)
        {
            Message = "新規・更新・関連を選んだ候補がありません。";
            return;
        }
        var (issue, task) = _store.NextNumbers();
        var (keys, prints) = _store.Applied();
        var (pending, pendingPrints) = _store.Pending();
        var plan = ChangePlanner.Plan(new PlanningInput
        {
            WorkspaceId = _store.WorkspaceGuid,
            Items = _store.Items(),
            Reviews = approved,
            Applied = keys,
            Pending = pending,
            PendingFingerprints = pendingPrints,
            AppliedFingerprints = prints,
            Files = _store.Files(),
            NextIssueNumber = issue,
            NextTaskNumber = task,
            NowUtc = _time.GetUtcNow(),
        }, Guid.CreateVersion7);
        foreach (var f in plan.Files) PlannedFiles.Add(new PlannedFileRow(f.RelativePath, f.Operation, TextDiff.Unified(f.BeforeText, f.AfterText, f.RelativePath)));
        var notes = new List<string>();
        notes.AddRange(plan.Skipped.Select(s => $"計画に入れない: {s.Title} — {s.Reason}"));
        notes.AddRange(plan.Proposals.Select(p => $"提案として保存: {p.Target.DisplayId} {p.Target.Title} — {p.Reason}"));
        PlanNotes = notes.Count > 0 ? string.Join("\n", notes) : null;
        if (plan.IsEmpty)
        {
            PlanSummary = null;
            Message = "計画に入れられる候補がありません。" + (notes.Count > 0 ? "理由は下に表示しています。" : string.Empty);
            PlanNotes = notes.Count > 0 ? string.Join("\n", notes) : null;
            return;
        }
        _plan = plan;
        var created = plan.Changes.Count(c => c.Action == ReviewChoice.Create);
        var updated = plan.Changes.Count(c => c.Action == ReviewChoice.Update);
        var mentioned = plan.Changes.Count(c => c.Action == ReviewChoice.Mention);
        PlanSummary = $"変更計画: 新規 {created} 件・更新 {updated} 件・関連 {mentioned} 件・提案 {plan.Proposals.Count} 件、ファイル {plan.Files.Count} 個。" +
            "管理ファイルへの反映は Phase F で実装します。今は計画を保存して内容を確かめられます。";
    }

    /// <summary>Saves the plan for Phase F. Pressing it twice returns the same batch (AT-12).</summary>
    [RelayCommand]
    private void SavePlan()
    {
        if (_plan is null) return;
        try
        {
            var saved = _store.SavePlan(_plan);
            Refresh();
            Message = saved.Reused
                ? "同じ内容の変更計画はすでに保存されています（二重には保存しません）。"
                : $"変更計画を保存しました（{saved.Directory}）。管理ファイルはまだ変わっていません。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DatabaseBusyException or Microsoft.Data.Sqlite.SqliteException)
        {
            _dialogs.ShowError("変更計画を保存できません", ex.Message);
        }
    }

    private void ClearPlan()
    {
        _plan = null;
        PlannedFiles.Clear();
        PlanSummary = null;
        PlanNotes = null;
    }
}
