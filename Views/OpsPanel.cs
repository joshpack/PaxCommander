using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxCommander.Core;
using PaxCommander.Core.Remote;
using PaxCommander.Ops;

namespace PaxCommander.Views;

/// <summary>
/// The transfer queue along the bottom: every running copy/move/delete/zip with progress, speed, time left and
/// pause/cancel. It polls the jobs (they're updated from worker threads) and hides itself when idle.
/// </summary>
public sealed class OpsPanel : UserControl
{
    private sealed class Card
    {
        public required IJobView Job;
        public required Border Root;
        public required TextBlock Title, Detail;
        public required ProgressBar Bar;
        public required Button Pause, Cancel;
        public DateTime? FinishedSeen;
    }

    private readonly JobQueue _queue;
    private readonly List<RemoteJob> _remote = [];
    private readonly Dictionary<Guid, IJobView> _views = [];
    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly Dictionary<Guid, Card> _cards = [];
    private readonly DispatcherTimer _timer;
    private readonly TextBlock _summary = new() { Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };

    public OpsPanel(JobQueue queue)
    {
        _queue = queue;
        var clear = new Button { Content = "Clear finished", Padding = new Thickness(10, 2), MinHeight = 0, Height = 24, FontSize = 12 };
        clear.Classes.Add("icon");
        clear.Width = double.NaN;
        clear.Click += (_, _) => ClearFinished();
        var head = new DockPanel { Margin = new Thickness(4, 0, 0, 6) };
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        head.Children.Add(_summary);
        Content = new Border
        {
            Background = Skin.Panel,
            BorderBrush = Skin.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            Child = new DockPanel
            {
                Children =
                {
                    head,
                    new ScrollViewer { Content = _list, MaxHeight = 210 },
                },
            },
        };
        DockPanel.SetDock(head, Dock.Top);
        IsVisible = false;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
    }

    /// <summary>Call after queueing a job so the panel appears immediately.</summary>
    public void Poke()
    {
        Tick();
        _timer.Start();
    }

    private void Tick()
    {
        var jobs = AllJobs();
        foreach (var job in jobs)
        {
            if (!_cards.TryGetValue(job.Id, out var card))
            {
                if (job.State is JobState.Completed or JobState.Cancelled && job.FinishedUtc < DateTime.UtcNow.AddSeconds(-10)) continue;
                card = Create(job);
                _cards[job.Id] = card;
                _list.Children.Insert(0, card.Root);
            }
            Update(card);
        }

        // Successful jobs fade out after a few seconds; failures stay until cleared so they can't be missed.
        foreach (var card in _cards.Values.ToList())
        {
            if (card.Job.State == JobState.Completed && card.Job.ProblemCount == 0 && card.FinishedSeen is { } seen && DateTime.UtcNow - seen > TimeSpan.FromSeconds(5))
                Remove(card);
        }

        var running = jobs.Count(j => j.State is JobState.Queued or JobState.Scanning or JobState.Running or JobState.Paused);
        _summary.Text = running > 0 ? $"{running} OPERATION{(running == 1 ? "" : "S")} RUNNING" : "OPERATIONS";
        IsVisible = _cards.Count > 0;
        if (_cards.Count == 0) _timer.Stop();
    }

    private Card Create(IJobView job)
    {
        var title = new TextBlock { FontWeight = FontWeight.SemiBold, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis };
        var detail = new TextBlock { FontSize = 11.5, Foreground = Skin.Muted, TextTrimming = TextTrimming.CharacterEllipsis };
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 5, 0, 0) };
        var pause = new Button { Content = Glyphs.Icon("pause", 13) };
        pause.Classes.Add("icon");
        pause.Click += (_, _) =>
        {
            if (job.State == JobState.Paused) job.Resume();
            else job.Pause();
            Tick();
        };
        var cancel = new Button { Content = Glyphs.Icon("close", 13) };
        cancel.Classes.Add("icon");
        var glyph = job.Glyph;
        var icon = Glyphs.Icon(glyph, 16, Skin.Info);
        icon.Margin = new Thickness(2, 0, 10, 0);
        icon.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        grid.Children.Add(icon);
        var mid = new StackPanel { Children = { title, detail, bar } };
        Grid.SetColumn(mid, 1);
        grid.Children.Add(mid);
        Grid.SetColumn(pause, 2);
        grid.Children.Add(pause);
        Grid.SetColumn(cancel, 3);
        grid.Children.Add(cancel);
        var root = new Border
        {
            Background = Skin.Popup,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 7),
            Child = grid,
        };
        var card = new Card { Job = job, Root = root, Title = title, Detail = detail, Bar = bar, Pause = pause, Cancel = cancel };
        cancel.Click += (_, _) =>
        {
            if (job.State is JobState.Completed or JobState.Failed or JobState.Cancelled) Remove(card);
            else job.Cancel();
        };
        ToolTip.SetTip(cancel, "Cancel");
        return card;
    }

    private void Update(Card c)
    {
        var j = c.Job;
        c.Title.Text = j.Title;
        var done = j.State is JobState.Completed or JobState.Failed or JobState.Cancelled;
        if (done && c.FinishedSeen is null) c.FinishedSeen = DateTime.UtcNow;
        c.Pause.IsVisible = !done && j.CanPause;
        ((GlyphIcon)c.Pause.Content!).Glyph = j.State == JobState.Paused ? "play" : "pause";
        ToolTip.SetTip(c.Pause, j.State == JobState.Paused ? "Resume" : "Pause");
        ToolTip.SetTip(c.Cancel, done ? "Dismiss" : "Cancel");

        double fraction = j.TotalBytes > 0 ? (double)j.DoneBytes / j.TotalBytes
            : j.TotalItems > 0 ? (double)j.DoneItems / j.TotalItems : 0;
        c.Bar.IsIndeterminate = j.State is JobState.Scanning or JobState.Queued || (j.State == JobState.Running && j.TotalBytes == 0 && j.TotalItems == 0);
        c.Bar.Value = done && j.State == JobState.Completed ? 1 : fraction;

        c.Detail.Foreground = j.State == JobState.Failed || j.ProblemCount > 0 ? Skin.Warn : Skin.Muted;
        c.Detail.Text = j.State switch
        {
            JobState.Queued => "Waiting for other operations…",
            JobState.Scanning => "Counting… " + (j.CurrentItem is { } s ? Path.GetFileName(s) : ""),
            JobState.Paused => $"Paused · {Progress(j)}",
            JobState.Running => $"{Progress(j)}" + (j.BytesPerSecond > 0 ? $" · {Formatting.Speed(j.BytesPerSecond)}" : "") +
                                (j.Remaining is { } r ? $" · {Formatting.Duration(r)} left" : "") +
                                (j.CurrentItem is { } cur ? $" · {Path.GetFileName(cur)}" : ""),
            JobState.Completed when j.ProblemCount > 0 => $"Done, but {Formatting.Count(j.ProblemCount, "item")} skipped: {j.FirstProblem}",
            JobState.Completed => "Done",
            JobState.Cancelled => "Cancelled",
            JobState.Failed => "Failed: " + (j.ErrorMessage ?? "unknown error"),
            _ => "",
        };
    }

    private static string Progress(IJobView j) =>
        j.TotalBytes > 0
            ? $"{Formatting.Size(j.DoneBytes)} of {Formatting.Size(j.TotalBytes)}"
            : j.TotalItems > 0 ? $"{j.DoneItems:N0} of {j.TotalItems:N0} items" : "";

    private void Remove(Card card)
    {
        _cards.Remove(card.Job.Id);
        _list.Children.Remove(card.Root);
        if (_cards.Count == 0) IsVisible = false;
    }

    private void ClearFinished()
    {
        foreach (var c in _cards.Values.Where(c => c.Job.State is JobState.Completed or JobState.Failed or JobState.Cancelled).ToList())
            Remove(c);
        _queue.ClearFinished();
        _remote.RemoveAll(r => r.FinishedUtc is not null);
    }

    /// <summary>Shows a server transfer alongside local jobs.</summary>
    public void Add(RemoteJob job)
    {
        lock (_remote) _remote.Add(job);
        Poke();
    }

    private List<IJobView> AllJobs()
    {
        var list = new List<IJobView>();
        foreach (var j in _queue.Jobs)
        {
            if (!_views.TryGetValue(j.Id, out var view)) _views[j.Id] = view = new FileJobView(j);
            list.Add(view);
        }
        lock (_remote) list.AddRange(_remote);
        return list;
    }

    private sealed class FileJobView(FileJob job) : IJobView
    {
        public Guid Id => job.Id;
        public string Glyph => job.Kind switch
        {
            JobKind.Copy => "copy",
            JobKind.Move => "swap",
            JobKind.Delete or JobKind.Recycle => "trash",
            JobKind.Compress or JobKind.Extract => "archive",
            _ => "zap",
        };
        public string Title => job.Title;
        public JobState State => job.State;
        public DateTime? FinishedUtc => job.FinishedUtc;
        public int ProblemCount => job.Problems.Count;
        public string? FirstProblem => job.Problems.Count > 0 ? job.Problems[0].Reason : null;
        public long TotalBytes => job.TotalBytes;
        public long DoneBytes => job.DoneBytes;
        public int TotalItems => job.TotalItems;
        public int DoneItems => job.DoneItems;
        public string? CurrentItem => job.CurrentItem;
        public double BytesPerSecond => job.BytesPerSecond;
        public TimeSpan? Remaining => job.Remaining;
        public string? ErrorMessage => job.ErrorMessage;
        public bool CanPause => job.Kind != JobKind.Recycle;
        public void Pause() => job.Pause();
        public void Resume() => job.Resume();
        public void Cancel() => job.Cancel();
    }
}
