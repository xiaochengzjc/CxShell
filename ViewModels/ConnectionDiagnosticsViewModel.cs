using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CxShell.Models;
using CxShell.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CxShell.ViewModels;

public sealed partial class ConnectionDiagnosticStepViewModel : ObservableObject
{
    private readonly int _index;
    private string _name;

    [ObservableProperty] private ConnectionDiagnosticStepStatus _status;
    [ObservableProperty] private string? _detail;
    [ObservableProperty] private long? _elapsedMilliseconds;

    public ConnectionDiagnosticStepViewModel(int index, string name)
    {
        _index = index;
        _name = name;
        _status = ConnectionDiagnosticStepStatus.Pending;
    }

    public string DisplayName => $"{_index + 1}. {_name}";

    public string StatusDisplay => Status switch
    {
        ConnectionDiagnosticStepStatus.Running => LocalizationService.Shared.Text("Diagnostics.Running"),
        ConnectionDiagnosticStepStatus.Success => ElapsedMilliseconds is { } milliseconds
            ? $"✓ {milliseconds}ms"
            : "✓",
        ConnectionDiagnosticStepStatus.Warning => "!",
        ConnectionDiagnosticStepStatus.Failed => "✗",
        ConnectionDiagnosticStepStatus.Skipped => "-",
        _ => "..."
    };

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public void Apply(ConnectionDiagnosticStepUpdate update)
    {
        _name = update.Name;
        Status = update.Status;
        Detail = update.Detail;
        ElapsedMilliseconds = update.ElapsedMilliseconds;
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(HasDetail));
    }

    partial void OnStatusChanged(ConnectionDiagnosticStepStatus value)
    {
        OnPropertyChanged(nameof(StatusDisplay));
    }

    partial void OnDetailChanged(string? value)
    {
        OnPropertyChanged(nameof(HasDetail));
    }

    partial void OnElapsedMillisecondsChanged(long? value)
    {
        OnPropertyChanged(nameof(StatusDisplay));
    }
}

public partial class ConnectionDiagnosticsViewModel : ObservableObject
{
    private readonly SessionInfo _session;
    private readonly string? _password;
    private readonly ConnectionDiagnosticsService _service = new();
    private ConnectionDiagnosticReport? _lastReport;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _issueTitle;
    [ObservableProperty] private string? _issueDescription;
    [ObservableProperty] private bool _allPassed;

    public ObservableCollection<ConnectionDiagnosticStepViewModel> Steps { get; } = new();
    public ObservableCollection<string> Suggestions { get; } = new();

    public string TitleText => Text("Diagnostics.Title");
    public string StepsText => Text("Diagnostics.Steps");
    public string TargetText => string.Format(
        Text("Diagnostics.Target"),
        string.IsNullOrWhiteSpace(_session.Name) ? _session.Host : _session.Name,
        _session.Username,
        _session.Host,
        _session.Port);
    public string RunningText => Text("Diagnostics.RunningPanel");
    public string AllPassedText => Text("Diagnostics.AllPassed");
    public string IssueLabelText => Text("Diagnostics.IssueLabel");
    public string SuggestionsText => Text("Diagnostics.Suggestions");
    public string RerunText => Text("Diagnostics.Rerun");
    public string ExportText => Text("Diagnostics.Export");
    public string CloseText => Text("Diagnostics.Close");
    public string UnknownText => Text("Diagnostics.NotRun");
    public bool HasIssue => !string.IsNullOrWhiteSpace(IssueTitle);
    public bool HasSuggestions => Suggestions.Count > 0;
    public bool CanExport => _lastReport != null;

    public ConnectionDiagnosticsViewModel(SessionInfo session, string? password)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _password = password;
        Steps.Add(new ConnectionDiagnosticStepViewModel(0, Text("Diagnostics.StepDns")));
        Steps.Add(new ConnectionDiagnosticStepViewModel(1, Text("Diagnostics.StepTcp")));
        Steps.Add(new ConnectionDiagnosticStepViewModel(2, Text("Diagnostics.StepBanner")));
        Steps.Add(new ConnectionDiagnosticStepViewModel(3, Text("Diagnostics.StepAuthentication")));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        IssueTitle = null;
        IssueDescription = null;
        AllPassed = false;
        Suggestions.Clear();
        OnPropertyChanged(nameof(HasSuggestions));
        _lastReport = null;
        OnPropertyChanged(nameof(HasIssue));
        OnPropertyChanged(nameof(CanExport));

        try
        {
            var progress = new Progress<ConnectionDiagnosticStepUpdate>(update =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (update.Index >= 0 && update.Index < Steps.Count)
                        Steps[update.Index].Apply(update);
                });
            });
            var report = await _service.DiagnoseAsync(_session, _password, progress, cancellationToken);
            _lastReport = report;
            IssueTitle = report.IssueTitle;
            IssueDescription = report.IssueDescription;
            foreach (var suggestion in report.Suggestions)
                Suggestions.Add(suggestion);

            AllPassed = report.Success;
            OnPropertyChanged(nameof(HasIssue));
            OnPropertyChanged(nameof(HasSuggestions));
            OnPropertyChanged(nameof(CanExport));
        }
        catch (OperationCanceledException)
        {
            IssueTitle = Text("Diagnostics.Cancelled");
            IssueDescription = Text("Diagnostics.CancelledDescription");
            OnPropertyChanged(nameof(HasIssue));
        }
        catch (Exception ex)
        {
            IssueTitle = Text("Diagnostics.Failed");
            IssueDescription = ex.Message;
            OnPropertyChanged(nameof(HasIssue));
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRun() => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        RunCommand.NotifyCanExecuteChanged();
    }

    public string BuildReportText()
    {
        var builder = new StringBuilder();
        builder.AppendLine(Text("Diagnostics.ReportTitle"));
        builder.AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        builder.AppendLine(TargetText);
        builder.AppendLine(new string('-', 56));
        foreach (var step in Steps)
        {
            builder.AppendLine($"{step.DisplayName}: {step.StatusDisplay}");
            if (step.HasDetail)
                builder.AppendLine($"  {step.Detail}");
        }

        if (HasIssue)
        {
            builder.AppendLine(new string('-', 56));
            builder.AppendLine($"{IssueLabelText}: {IssueTitle}");
            builder.AppendLine(IssueDescription);
        }

        if (Suggestions.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine(SuggestionsText);
            foreach (var suggestion in Suggestions)
                builder.AppendLine($"- {suggestion}");
        }

        return builder.ToString();
    }

    private static string Text(string key) => LocalizationService.Shared.Text(key);
}
