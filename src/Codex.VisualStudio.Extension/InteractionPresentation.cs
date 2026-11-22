using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Net.Mail;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Extension;

[DataContract]
public sealed class InteractionCardViewModel
{
    public InteractionCardViewModel(
        string kind,
        string requestId,
        ApprovalViewModel? approval = null,
        UserInputViewModel? userInput = null,
        PermissionSelectionViewModel? permission = null,
        McpElicitationViewModel? elicitation = null,
        string? message = null,
        Func<Task>? dismiss = null)
    {
        Kind = kind;
        RequestId = requestId;
        Approval = approval;
        UserInput = userInput;
        Permission = permission;
        Elicitation = elicitation;
        Message = message;
        CanDismiss = dismiss is not null;
        DismissCommand = new AsyncCommand(dismiss ?? (static () => Task.CompletedTask), () => CanDismiss);
    }

    // Information-only cards have no server request to resolve, so the user removes them explicitly.
    [DataMember]
    public bool CanDismiss { get; }

    [DataMember]
    public AsyncCommand DismissCommand { get; }

    [DataMember]
    public string Kind { get; }

    public string RequestId { get; }

    [DataMember]
    public ApprovalViewModel? Approval { get; }

    [DataMember]
    public UserInputViewModel? UserInput { get; }

    [DataMember]
    public PermissionSelectionViewModel? Permission { get; }

    [DataMember]
    public McpElicitationViewModel? Elicitation { get; }

    [DataMember]
    public string? Message { get; }

    [DataMember]
    public bool IsApproval => Approval is not null;

    [DataMember]
    public bool IsQuestion => UserInput is not null;

    [DataMember]
    public bool IsPermission => Permission is not null;

    [DataMember]
    public bool IsElicitation => Elicitation is not null;

    [DataMember]
    public bool IsInformation => Approval is null && UserInput is null && Permission is null && Elicitation is null;

    public void MarkResolved()
    {
        Approval?.MarkResolved();
        UserInput?.MarkResolved();
        Permission?.MarkResolved();
        Elicitation?.MarkResolved();
    }
}

[DataContract]
public sealed class ApprovalChoiceViewModel
{
    private readonly string choiceId;
    private readonly Func<string, Task> select;
    private readonly AsyncCommand selectCommand;

    public ApprovalChoiceViewModel(ApprovalChoice choice, SafeMarkdownService markdown, Func<string, Task> select, Func<bool> canSelect)
    {
        choiceId = choice.ChoiceId;
        this.select = select;
        Label = markdown.ToSafeText(choice.Label).Trim();
        Description = markdown.ToSafeText(choice.Description).Trim();
        selectCommand = new AsyncCommand(SelectAsync, canSelect);
    }

    [DataMember]
    public string Label { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public AsyncCommand SelectCommand => selectCommand;

    public void NotifyCanExecuteChanged() => selectCommand.RaiseCanExecuteChanged();

    private Task SelectAsync() => select(choiceId);
}

[DataContract]
public sealed class PermissionSelectionViewModel : ObservableObject
{
    private readonly Func<string, IReadOnlyList<string>, PermissionScope, Task> resolver;
    private readonly string requestId;

    public PermissionSelectionViewModel(
        PermissionRequest request,
        Func<string, IReadOnlyList<string>, PermissionScope, Task> resolver,
        SafeMarkdownService markdown)
    {
        this.requestId = request.RequestId;
        this.resolver = resolver;
        ScopeGroupName = "permission-" + Guid.NewGuid().ToString("N");
        Title = "Permission request";
        Reason = markdown.ToSafeText(request.Reason ?? string.Empty).Trim();
        Options = new ObservableCollection<PermissionGrantOptionViewModel>(
            request.RequestedPermissions.Select(option => new PermissionGrantOptionViewModel(option, markdown)));
        SubmitCommand = new AsyncCommand(() => ResolveAsync(false), () => CanResolve);
        DeclineCommand = new AsyncCommand(() => ResolveAsync(true), () => CanResolve);
    }

    [DataMember]
    public string Title { get; }

    [DataMember]
    public string Reason { get; }

    [DataMember]
    public string ScopeGroupName { get; }

    [DataMember]
    public ObservableCollection<PermissionGrantOptionViewModel> Options { get; }

    [DataMember]
    public PermissionScope Scope
    {
        get => field;
        set => SetProperty(ref field, value);
    } = PermissionScope.Turn;

    [DataMember]
    public bool IsTurnScope
    {
        get => Scope == PermissionScope.Turn;
        set
        {
            if (value)
            {
                Scope = PermissionScope.Turn;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSessionScope));
            }
        }
    }

    [DataMember]
    public bool IsSessionScope
    {
        get => Scope == PermissionScope.Session;
        set
        {
            if (value)
            {
                Scope = PermissionScope.Session;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsTurnScope));
            }
        }
    }

    [DataMember]
    public string? ValidationText
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool IsResolved
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                SubmitCommand.RaiseCanExecuteChanged();
                DeclineCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public AsyncCommand SubmitCommand { get; }

    [DataMember]
    public AsyncCommand DeclineCommand { get; }

    public bool CanResolve => !IsResolved;

    public void MarkResolved() => IsResolved = true;

    private async Task ResolveAsync(bool decline)
    {
        if (!CanResolve)
        {
            return;
        }

        IsResolved = true;
        string[] selected = decline
            ? []
            : Options.Where(option => option.IsSelected).Select(option => option.PermissionId).ToArray();
        await resolver(requestId, selected, Scope).ConfigureAwait(false);
    }
}

[DataContract]
public sealed class PermissionGrantOptionViewModel : ObservableObject
{
    public PermissionGrantOptionViewModel(PermissionGrantOption option, SafeMarkdownService markdown)
    {
        PermissionId = option.PermissionId;
        Label = markdown.ToSafeText(option.Label).Trim();
        Description = markdown.ToSafeText(option.Description).Trim();
    }

    public string PermissionId { get; }

    [DataMember]
    public string Label { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public bool IsSelected
    {
        get => field;
        set => SetProperty(ref field, value);
    }
}

[DataContract]
public sealed class McpElicitationViewModel : ObservableObject
{
    private readonly Func<string, McpElicitationAction, IReadOnlyList<McpElicitationValue>, Task> resolver;
    private readonly string requestId;

    public McpElicitationViewModel(
        McpElicitationRequest request,
        Func<string, McpElicitationAction, IReadOnlyList<McpElicitationValue>, Task> resolver,
        SafeMarkdownService markdown)
    {
        requestId = request.RequestId;
        this.resolver = resolver;
        ServerName = markdown.ToSafeText(request.DisplayServerName.Length == 0 ? request.ServerName : request.DisplayServerName).Trim();
        Message = markdown.ToSafeText(request.Message).Trim();
        OriginDisplay = markdown.ToSafeText(request.OriginDisplay ?? string.Empty).Trim();
        OpenAuthorizationActionId = request.OpenAuthorizationActionId;
        IsUrlRequest = request.Kind == McpElicitationKind.Url;
        Fields = new ObservableCollection<McpElicitationFieldViewModel>(
            request.Fields.Select(field => new McpElicitationFieldViewModel(field, markdown)));
        SubmitCommand = new AsyncCommand(() => ResolveAsync(McpElicitationAction.Accept), () => CanAccept);
        DeclineCommand = new AsyncCommand(() => ResolveAsync(McpElicitationAction.Decline), () => CanResolve);
        CancelCommand = new AsyncCommand(() => ResolveAsync(McpElicitationAction.Cancel), () => CanResolve);
        OpenAuthorizationCommand = new AsyncCommand(OpenAuthorizationAsync, () => CanOpenAuthorization);
    }

    [DataMember]
    public string ServerName { get; }

    [DataMember]
    public string Message { get; }

    [DataMember]
    public string OriginDisplay { get; }

    [DataMember]
    public bool IsUrlRequest { get; }

    [DataMember]
    public bool IsFormRequest => !IsUrlRequest;

    [DataMember]
    public ObservableCollection<McpElicitationFieldViewModel> Fields { get; }

    [DataMember]
    public string? ValidationText
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    [DataMember]
    public bool IsResolved
    {
        get => field;
        private set
        {
            if (SetProperty(ref field, value))
            {
                SubmitCommand.RaiseCanExecuteChanged();
                DeclineCommand.RaiseCanExecuteChanged();
                CancelCommand.RaiseCanExecuteChanged();
                OpenAuthorizationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    [DataMember]
    public AsyncCommand SubmitCommand { get; }

    [DataMember]
    public AsyncCommand DeclineCommand { get; }

    [DataMember]
    public AsyncCommand CancelCommand { get; }

    [DataMember]
    public AsyncCommand OpenAuthorizationCommand { get; }

    public bool CanResolve => !IsResolved;

    public bool CanAccept => CanResolve;

    public bool CanOpenAuthorization => CanResolve && !string.IsNullOrWhiteSpace(OpenAuthorizationActionId);

    private string? OpenAuthorizationActionId { get; }

    private Func<string, Task>? OpenAuthorization { get; set; }

    public void ConfigureOpenAuthorization(Func<string, Task> open)
    {
        OpenAuthorization = open;
        OpenAuthorizationCommand.RaiseCanExecuteChanged();
    }

    public void MarkResolved() => IsResolved = true;

    private async Task ResolveAsync(McpElicitationAction action)
    {
        if (!CanResolve)
        {
            return;
        }

        var values = new List<McpElicitationValue>();
        if (action == McpElicitationAction.Accept)
        {
            foreach (McpElicitationFieldViewModel field in Fields)
            {
                if (!field.TryCreateValue(out McpElicitationValue? value, out string? error))
                {
                    ValidationText = error;
                    return;
                }

                if (value is not null)
                {
                    values.Add(value);
                }
            }

            ValidationText = null;
        }

        IsResolved = true;
        await resolver(requestId, action, values).ConfigureAwait(false);
    }

    private Task OpenAuthorizationAsync()
        => OpenAuthorization is not null && OpenAuthorizationActionId is not null
            ? OpenAuthorization(OpenAuthorizationActionId)
            : Task.CompletedTask;
}

[DataContract]
public sealed partial class McpElicitationFieldViewModel : ObservableObject
{
    private readonly string fieldName;
    private readonly McpElicitationFieldType type;
    private readonly McpElicitationFormat format;
    private readonly bool required;
    private readonly uint? minLength;
    private readonly uint? maxLength;
    private readonly double? minimumNumber;
    private readonly double? maximumNumber;
    private readonly long? minimumInteger;
    private readonly long? maximumInteger;
    private readonly ulong? minimumSelections;
    private readonly ulong? maximumSelections;
    private bool isPresent;

    public McpElicitationFieldViewModel(McpElicitationField field, SafeMarkdownService markdown)
    {
        fieldName = field.Name;
        type = field.Type;
        format = field.Format;
        required = field.Required;
        isPresent = field.Required || field.DefaultValue is not null;
        minLength = field.MinLength;
        maxLength = field.MaxLength;
        minimumNumber = field.MinimumNumber;
        maximumNumber = field.MaximumNumber;
        minimumInteger = field.MinimumInteger;
        maximumInteger = field.MaximumInteger;
        minimumSelections = field.MinimumSelections;
        maximumSelections = field.MaximumSelections;
        Title = markdown.ToSafeText(field.Title).Trim();
        Description = markdown.ToSafeText(field.Description ?? string.Empty).Trim();
        Type = field.Type;
        IsBoolean = field.Type == McpElicitationFieldType.Boolean;
        IsSingleSelect = field.Type == McpElicitationFieldType.SingleSelect;
        IsMultiSelect = field.Type == McpElicitationFieldType.MultiSelect;
        Choices = new ObservableCollection<McpElicitationChoiceViewModel>(
            field.Choices.Select(choice => new McpElicitationChoiceViewModel(choice, markdown, OnChoiceSelected)));
        if (field.DefaultValue is { } defaultValue)
        {
            InputText = defaultValue.StringValue is { } defaultString
                ? SafeMarkdownService.ToSafeLiteralText(defaultString)
                : defaultValue.IntegerValue?.ToString(CultureInfo.InvariantCulture)
                ?? defaultValue.NumberValue?.ToString("R", CultureInfo.InvariantCulture)
                ?? string.Empty;
            if (type == McpElicitationFieldType.Boolean)
            {
                BooleanValue = defaultValue.BooleanValue;
            }
            foreach (string selectedId in defaultValue.ChoiceIds)
            {
                McpElicitationChoiceViewModel? choice = Choices.FirstOrDefault(item => item.ChoiceId == selectedId);
                if (choice is not null)
                {
                    choice.IsSelected = true;
                }
            }
        }
    }

    [DataMember]
    public string Title { get; }

    [DataMember]
    public string Description { get; }

    [DataMember]
    public McpElicitationFieldType Type { get; }

    [DataMember]
    public bool IsBoolean { get; }

    [DataMember]
    public bool IsTextInput => Type is McpElicitationFieldType.Text or McpElicitationFieldType.Number or McpElicitationFieldType.WholeNumber;

    [DataMember]
    public bool IsSingleSelect { get; }

    [DataMember]
    public bool IsMultiSelect { get; }

    [DataMember]
    public ObservableCollection<McpElicitationChoiceViewModel> Choices { get; }

    [DataMember]
    public string InputText
    {
        get => field;
        set
        {
            isPresent = true;
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ValidationText));
            }
        }
    } = string.Empty;

    [DataMember]
    public bool? BooleanValue
    {
        get => field;
        set
        {
            isPresent = value.HasValue;
            SetProperty(ref field, value);
        }
    }

    [DataMember]
    public string? ValidationText
    {
        get => field;
        private set => SetProperty(ref field, value);
    }

    public bool TryCreateValue(out McpElicitationValue? value, out string? error)
    {
        value = null;
        error = null;
        string text = InputText;
        bool hasChoices = type is McpElicitationFieldType.SingleSelect or McpElicitationFieldType.MultiSelect;
        string[] selectedIds = Choices.Where(choice => choice.IsSelected).Select(choice => choice.ChoiceId).ToArray();
        if (hasChoices)
        {
            bool includeSelection = isPresent || selectedIds.Length > 0;
            if (includeSelection
                && (minimumSelections is ulong min && (ulong)selectedIds.Length < min
                    || maximumSelections is ulong max && (ulong)selectedIds.Length > max))
            {
                return Fail("The selected value count for " + Title + " is outside the allowed range.", out error);
            }

            if (type == McpElicitationFieldType.SingleSelect
                && (selectedIds.Length > 1 || required && includeSelection && selectedIds.Length != 1))
            {
                return Fail("Select one value for " + Title + ".", out error);
            }

            if (includeSelection)
            {
                value = new McpElicitationValue { FieldName = fieldName, ChoiceIds = selectedIds };
            }

            return true;
        }

        if (type == McpElicitationFieldType.Boolean)
        {
            if (BooleanValue is bool selectedBoolean)
            {
                value = new McpElicitationValue { FieldName = fieldName, BooleanValue = selectedBoolean };
            }
            else if (required)
            {
                return Fail("Choose a value for " + Title + ".", out error);
            }

            return true;
        }

        if (!isPresent)
        {
            return true;
        }

        if (type == McpElicitationFieldType.Text)
        {
            if (Encoding.UTF8.GetByteCount(InputText) > 1024 * 1024 || !IsWellFormedUnicode(InputText))
            {
                return Fail("The text for " + Title + " is invalid or exceeds the supported size limit.", out error);
            }

            int runeCount = InputText.EnumerateRunes().Count();
            if (minLength is uint textMin && (uint)runeCount < textMin
                || maxLength is uint textMax && (uint)runeCount > textMax)
            {
                return Fail("The text length for " + Title + " is outside the allowed range.", out error);
            }

            if (!MatchesFormat(InputText))
            {
                return Fail("Enter a valid " + FormatLabel() + " for " + Title + ".", out error);
            }

            value = new McpElicitationValue { FieldName = fieldName, StringValue = InputText };
            return true;
        }

        string numericText = text.Trim();
        if (numericText.Length == 0)
        {
            return Fail("Enter a valid number for " + Title + ".", out error);
        }

        if (type == McpElicitationFieldType.WholeNumber)
        {
            if (!long.TryParse(numericText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            {
                return Fail("Enter a whole number for " + Title + ".", out error);
            }

            if (minimumNumber is double fractionMin && integer < fractionMin
                || maximumNumber is double fractionMax && integer > fractionMax
                || minimumInteger is long integerMin && integer < integerMin
                || maximumInteger is long integerMax && integer > integerMax)
            {
                return Fail("The number for " + Title + " is outside the allowed range.", out error);
            }

            value = new McpElicitationValue { FieldName = fieldName, IntegerValue = integer };
            return true;
        }

        if (!double.TryParse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
        {
            return Fail("Enter a finite number for " + Title + ".", out error);
        }

        if (minimumNumber is double numberMin && number < numberMin || maximumNumber is double numberMax && number > numberMax)
        {
            return Fail("The number for " + Title + " is outside the allowed range.", out error);
        }

        value = new McpElicitationValue { FieldName = fieldName, NumberValue = number };
        return true;
    }

    private void OnChoiceSelected(McpElicitationChoiceViewModel selected)
    {
        isPresent = true;
        if (type != McpElicitationFieldType.SingleSelect)
        {
            return;
        }

        foreach (McpElicitationChoiceViewModel choice in Choices)
        {
            if (!ReferenceEquals(choice, selected))
            {
                choice.SetSelectedSilently(false);
            }
        }
    }

    private bool MatchesFormat(string text)
    {
        try
        {
            return format switch
            {
                McpElicitationFormat.Email => IsEmail(text),
                McpElicitationFormat.Uri => Uri.TryCreate(text, UriKind.Absolute, out _),
                McpElicitationFormat.Date => DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                McpElicitationFormat.DateTime => IsRfc3339DateTime(text),
                _ => true,
            };
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsEmail(string text)
        => text.IndexOfAny(['\r', '\n', '<', '>']) < 0
            && MailAddress.TryCreate(text, out MailAddress? address)
            && string.Equals(address.Address, text, StringComparison.Ordinal);

    private static bool IsRfc3339DateTime(string text)
        => Rfc3339DateTimeRegex().IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsWellFormedUnicode(string text)
    {
        for (int index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]))
            {
                if (index + 1 >= text.Length || !char.IsLowSurrogate(text[++index]))
                {
                    return false;
                }
            }
            else if (char.IsLowSurrogate(text[index]))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Rfc3339DateTimeRegex();

    private string FormatLabel() => format switch
    {
        McpElicitationFormat.Email => "email address",
        McpElicitationFormat.Uri => "URL",
        McpElicitationFormat.Date => "date",
        McpElicitationFormat.DateTime => "date and time",
        _ => "value",
    };

    private bool Fail(string message, out string? error)
    {
        ValidationText = message;
        error = message;
        return false;
    }
}

[DataContract]
public sealed class McpElicitationChoiceViewModel : ObservableObject
{
    private readonly Action<McpElicitationChoiceViewModel> onSelected;
    private bool isSelected;

    public McpElicitationChoiceViewModel(McpElicitationChoice choice, SafeMarkdownService markdown, Action<McpElicitationChoiceViewModel> onSelected)
    {
        this.onSelected = onSelected;
        ChoiceId = choice.ChoiceId;
        Label = markdown.ToSafeText(choice.Label).Trim();
    }

    public string ChoiceId { get; }

    [DataMember]
    public string Label { get; }

    [DataMember]
    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value) && value)
            {
                onSelected(this);
            }
        }
    }

    internal void SetSelectedSilently(bool value) => SetProperty(ref isSelected, value, nameof(IsSelected));
}

[DataContract]
public sealed class InteractionAuthStatusPresentationViewModel : ObservableObject
{
    private readonly SafeMarkdownService markdown;
    private readonly Func<McpServerAuthStatus, Task> startMcpLogin;
    private readonly Func<string, Task> dismissMcpLogin;
    private readonly Func<string, Task> openAuthorization;

    public InteractionAuthStatusPresentationViewModel(
        InteractionAuthStatus status,
        SafeMarkdownService markdown,
        Func<McpServerAuthStatus, Task> startMcpLogin,
        Func<string, Task> dismissMcpLogin,
        Func<string, Task> openAuthorization)
    {
        this.markdown = markdown;
        this.startMcpLogin = startMcpLogin;
        this.dismissMcpLogin = dismissMcpLogin;
        this.openAuthorization = openAuthorization;
        IsLocal = status.IsLocal;
        IsSupported = status.IsSupported;
        State = status.State;
        StatusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        OriginDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        HasOpenAuthorizationAction = !string.IsNullOrWhiteSpace(status.OpenAuthorizationActionId);
        openAuthorizationActionId = status.OpenAuthorizationActionId;
        McpServers = new ObservableCollection<McpServerAuthPresentationViewModel>();
        Update(status);
    }

    private string? openAuthorizationActionId;

    [DataMember]
    public bool IsLocal { get => field; private set => SetProperty(ref field, value); }

    [DataMember]
    public bool IsSupported { get => field; private set => SetProperty(ref field, value); }

    [DataMember]
    public InteractionAuthState State { get => field; private set => SetProperty(ref field, value); }

    [DataMember]
    public string StatusText { get => field; private set => SetProperty(ref field, value); } = string.Empty;

    [DataMember]
    public string OriginDisplay { get => field; private set => SetProperty(ref field, value); } = string.Empty;

    [DataMember]
    public bool HasOpenAuthorizationAction { get => field; private set => SetProperty(ref field, value); }

    // Lets the compact (collapsed) presentation signal that MCP actions are waiting in the details.
    [DataMember]
    public bool HasPendingMcpAuthAction { get => field; private set => SetProperty(ref field, value); }

    [DataMember]
    public ObservableCollection<McpServerAuthPresentationViewModel> McpServers { get; }

    internal void RemoveMcpServer(McpServerAuthPresentationViewModel server)
    {
        if (McpServers.Remove(server))
        {
            server.PropertyChanged -= OnMcpServerPropertyChanged;
            RefreshPendingMcpAuthAction();
        }
    }

    public void Update(InteractionAuthStatus status)
    {
        IsLocal = status.IsLocal;
        IsSupported = status.IsSupported;
        State = status.State;
        StatusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        OriginDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        openAuthorizationActionId = status.OpenAuthorizationActionId;
        HasOpenAuthorizationAction = !string.IsNullOrWhiteSpace(openAuthorizationActionId);

        var retainedForMissingThread = new HashSet<McpServerAuthPresentationViewModel>();
        foreach (McpServerAuthStatus server in status.McpServers)
        {
            McpServerAuthPresentationViewModel? existing = McpServers.FirstOrDefault(item => item.MatchesServer(server));
            string? expectedThreadId = null;
            McpServerAuthStatus updateStatus = server;
            if (existing is null
                && status.McpEnvironmentIdentityEnabled
                && !server.HasRegistrationIdentity
                && !server.IsRegistrationAmbiguous)
            {
                McpServerAuthPresentationViewModel[] registrationMatches = McpServers
                    .Where(item => item.MatchesServerNameAndThread(server)
                        && !status.McpServers.Any(item.MatchesServer))
                    .ToArray();
                if (registrationMatches.Length == 1 && registrationMatches[0].HasRegistrationIdentity)
                {
                    existing = registrationMatches[0];
                    expectedThreadId = existing.ThreadId;
                    retainedForMissingThread.Add(existing);
                    updateStatus = new McpServerAuthStatus
                    {
                        ServerName = server.ServerName,
                        DisplayServerName = server.DisplayServerName,
                        HasRegistrationIdentity = existing.HasRegistrationIdentity,
                        EnvironmentId = existing.EnvironmentId,
                        DisplayEnvironmentId = existing.DisplayEnvironmentId,
                        ThreadId = existing.ThreadId,
                        IsRegistrationAmbiguous = existing.IsRegistrationAmbiguous,
                        State = server.State,
                        Message = server.Message,
                        OriginDisplay = server.OriginDisplay,
                        OpenAuthorizationActionId = server.OpenAuthorizationActionId,
                    };
                }
                else if (registrationMatches.Length > 1)
                {
                    foreach (McpServerAuthPresentationViewModel registrationMatch in registrationMatches)
                    {
                        retainedForMissingThread.Add(registrationMatch);
                        if (!registrationMatch.HasRegistrationIdentity)
                        {
                            registrationMatch.SetRegistrationAmbiguous(true);
                        }
                    }

                    continue;
                }
            }

            bool hasThreadScopedSnapshotForServer = server.ThreadId is null
                && status.McpServers.Any(candidate => candidate.ThreadId is not null
                    && string.Equals(candidate.ServerName, server.ServerName, StringComparison.Ordinal)
                    && (!server.HasRegistrationIdentity
                        || (candidate.HasRegistrationIdentity
                            && string.Equals(candidate.EnvironmentId, server.EnvironmentId, StringComparison.Ordinal))));
            if (existing is null && server.ThreadId is null && !hasThreadScopedSnapshotForServer)
            {
                McpServerAuthPresentationViewModel[] missingThreadMatches = McpServers
                    .Where(item => item.MatchesServerIgnoringMissingThread(server)
                        && !status.McpServers.Any(item.MatchesServer))
                    .ToArray();
                if (missingThreadMatches.Length == 1)
                {
                    existing = missingThreadMatches[0];
                    expectedThreadId = existing.ThreadId;
                    retainedForMissingThread.Add(existing);
                }
                else if (missingThreadMatches.Length > 1)
                {
                    foreach (McpServerAuthPresentationViewModel match in missingThreadMatches)
                    {
                        retainedForMissingThread.Add(match);
                        if (status.McpEnvironmentIdentityEnabled)
                        {
                            match.SetRegistrationAmbiguous(true);
                        }
                    }

                    continue;
                }
            }

            if (existing is null)
            {
                if (status.McpEnvironmentIdentityEnabled && !server.HasRegistrationIdentity)
                {
                    server.IsRegistrationAmbiguous = true;
                }

                var added = new McpServerAuthPresentationViewModel(server, markdown, startMcpLogin, dismissMcpLogin, openAuthorization);
                added.PropertyChanged += OnMcpServerPropertyChanged;
                McpServers.Add(added);
            }
            else
            {
                if (status.McpEnvironmentIdentityEnabled
                    && !updateStatus.HasRegistrationIdentity
                    && existing.IsRegistrationAmbiguous)
                {
                    updateStatus.IsRegistrationAmbiguous = true;
                }

                existing.Update(updateStatus, markdown, expectedThreadId);
            }
        }

        for (int index = McpServers.Count - 1; index >= 0; index--)
        {
            if (!retainedForMissingThread.Contains(McpServers[index])
                && !status.McpServers.Any(McpServers[index].MatchesServer))
            {
                McpServers[index].PropertyChanged -= OnMcpServerPropertyChanged;
                McpServers.RemoveAt(index);
            }
        }

        foreach (McpServerAuthPresentationViewModel server in McpServers)
        {
            int sameNameCount = status.McpServers.Count(candidate =>
                string.Equals(candidate.ServerName, server.RawServerName, StringComparison.Ordinal)
                && string.Equals(candidate.ThreadId, server.ThreadId, StringComparison.Ordinal));
            server.SetRegistrationAmbiguous(server.IsRegistrationAmbiguous || (!server.HasRegistrationIdentity && sameNameCount > 1));
        }

        RefreshPendingMcpAuthAction();
    }

    internal void UpdateGatewayStatus(InteractionAuthStatus status)
    {
        IsLocal = status.IsLocal;
        IsSupported = status.IsSupported;
        State = status.State;
        StatusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        OriginDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        openAuthorizationActionId = status.OpenAuthorizationActionId;
        HasOpenAuthorizationAction = !string.IsNullOrWhiteSpace(openAuthorizationActionId);
        RefreshPendingMcpAuthAction();
    }

    private void OnMcpServerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(McpServerAuthPresentationViewModel.CanStartLogin)
            or nameof(McpServerAuthPresentationViewModel.CanDismiss)
            or nameof(McpServerAuthPresentationViewModel.CanOpenAuthorization))
        {
            RefreshPendingMcpAuthAction();
        }
    }

    private void RefreshPendingMcpAuthAction()
        => HasPendingMcpAuthAction = McpServers.Any(server => server.HasPendingAction);

    public Task OpenAuthorizationAsync(Func<string, Task> open)
        => openAuthorizationActionId is { Length: > 0 } actionId ? open(actionId) : Task.CompletedTask;

}

[DataContract]
public sealed class McpServerAuthPresentationViewModel : ObservableObject
{
    private string serverName;
    private string safeDisplayServerName = string.Empty;
    private string? safeDisplayEnvironmentId;
    private readonly Func<McpServerAuthStatus, Task> startMcpLogin;
    private readonly Func<string, Task> dismissMcpLogin;
    private readonly Func<string, Task> openAuthorization;
    private string? operationId;
    private string? authorizationActionId;
    private InteractionAuthState state;
    private string displayServerName = string.Empty;
    private string statusText = string.Empty;
    private string originDisplay = string.Empty;
    private bool registrationAmbiguous;

    public McpServerAuthPresentationViewModel(
        McpServerAuthStatus status,
        SafeMarkdownService markdown,
        Func<McpServerAuthStatus, Task> startMcpLogin,
        Func<string, Task> dismissMcpLogin,
        Func<string, Task> openAuthorization)
    {
        this.startMcpLogin = startMcpLogin;
        this.dismissMcpLogin = dismissMcpLogin;
        this.openAuthorization = openAuthorization;
        serverName = status.ServerName;
        safeDisplayServerName = markdown.ToSafeText(status.DisplayServerName.Length == 0 ? status.ServerName : status.DisplayServerName).Trim();
        UpdateRegistrationIdentity(status.HasRegistrationIdentity, status.EnvironmentId, status.DisplayEnvironmentId, status.ThreadId, markdown);
        registrationAmbiguous = status.IsRegistrationAmbiguous;
        state = status.State;
        statusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        originDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        authorizationActionId = status.OpenAuthorizationActionId;
        LoginCommand = new AsyncCommand(() => this.startMcpLogin(CreateLoginRequestSnapshot()), () => CanStartLogin);
        DismissCommand = new AsyncCommand(DismissAsync, () => CanDismiss);
        OpenAuthorizationCommand = new AsyncCommand(OpenAuthorizationAsync, () => CanOpenAuthorization);
    }

    [DataMember]
    public string ServerName { get => displayServerName; private set => SetProperty(ref displayServerName, value); }

    internal string RawServerName => serverName;

    [DataMember]
    public bool HasRegistrationIdentity { get; private set; }

    public string? EnvironmentId { get; private set; }

    internal string? DisplayEnvironmentId => safeDisplayEnvironmentId;

    public string? ThreadId { get; private set; }

    [DataMember]
    public InteractionAuthState State { get => state; private set => SetProperty(ref state, value); }

    [DataMember]
    public string StatusText { get => statusText; private set => SetProperty(ref statusText, value); }

    [DataMember]
    public string OriginDisplay { get => originDisplay; private set => SetProperty(ref originDisplay, value); }

    [DataMember]
    public bool CanStartLogin => !registrationAmbiguous
        && (State is InteractionAuthState.ReauthenticationRequired or InteractionAuthState.Unavailable or InteractionAuthState.Failed);

    [DataMember]
    public bool CanDismiss => operationId is not null;

    [DataMember]
    public bool CanOpenAuthorization => authorizationActionId is not null;

    [DataMember]
    public bool IsRegistrationAmbiguous => registrationAmbiguous;

    internal bool HasPendingAction => CanStartLogin || CanDismiss || CanOpenAuthorization;

    private McpServerAuthStatus CreateLoginRequestSnapshot()
        => new()
        {
            ServerName = serverName,
            DisplayServerName = safeDisplayServerName,
            IsRegistrationAmbiguous = registrationAmbiguous,
            HasRegistrationIdentity = HasRegistrationIdentity,
            EnvironmentId = EnvironmentId,
            DisplayEnvironmentId = safeDisplayEnvironmentId,
            ThreadId = ThreadId,
            State = State,
            Message = StatusText,
            OriginDisplay = OriginDisplay,
            OpenAuthorizationActionId = authorizationActionId,
        };

    [DataMember]
    public AsyncCommand LoginCommand { get; }

    [DataMember]
    public AsyncCommand DismissCommand { get; }

    [DataMember]
    public AsyncCommand OpenAuthorizationCommand { get; }

    public void Update(McpOAuthLoginStatus status, SafeMarkdownService markdown)
    {
        operationId = status.State == InteractionAuthState.LoginPending ? status.OperationId : null;
        serverName = status.ServerName;
        safeDisplayServerName = markdown.ToSafeText(status.DisplayServerName.Length == 0 ? status.ServerName : status.DisplayServerName).Trim();
        UpdateRegistrationIdentity(status.HasRegistrationIdentity, status.EnvironmentId, status.DisplayEnvironmentId, status.ThreadId ?? ThreadId, markdown);
        State = status.State;
        StatusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        OriginDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        authorizationActionId = status.OpenAuthorizationActionId;
        OnPropertyChanged(nameof(CanDismiss));
        OnPropertyChanged(nameof(CanOpenAuthorization));
        OnPropertyChanged(nameof(CanStartLogin));
        LoginCommand.RaiseCanExecuteChanged();
        DismissCommand.RaiseCanExecuteChanged();
        OpenAuthorizationCommand.RaiseCanExecuteChanged();
    }

    public void Update(McpServerAuthStatus status, SafeMarkdownService markdown)
        => Update(status, markdown, expectedThreadId: null);

    internal void Update(McpServerAuthStatus status, SafeMarkdownService markdown, string? expectedThreadId)
    {
        if (status.State != InteractionAuthState.LoginPending)
        {
            operationId = null;
        }
        serverName = status.ServerName;
        safeDisplayServerName = markdown.ToSafeText(status.DisplayServerName.Length == 0 ? status.ServerName : status.DisplayServerName).Trim();
        UpdateRegistrationIdentity(status.HasRegistrationIdentity, status.EnvironmentId, status.DisplayEnvironmentId, status.ThreadId ?? expectedThreadId, markdown);
        SetRegistrationAmbiguous(status.IsRegistrationAmbiguous);
        State = status.State;
        StatusText = markdown.ToSafeText(status.Message ?? status.State.ToString()).Trim();
        OriginDisplay = markdown.ToSafeText(status.OriginDisplay ?? string.Empty).Trim();
        authorizationActionId = status.OpenAuthorizationActionId;
        OnPropertyChanged(nameof(CanDismiss));
        OnPropertyChanged(nameof(CanOpenAuthorization));
        OnPropertyChanged(nameof(CanStartLogin));
        LoginCommand.RaiseCanExecuteChanged();
        DismissCommand.RaiseCanExecuteChanged();
        OpenAuthorizationCommand.RaiseCanExecuteChanged();
    }

    public bool MatchesServer(string value)
        => string.Equals(serverName, value, StringComparison.Ordinal);

    public bool MatchesServer(McpServerAuthStatus value)
        => MatchesServer(value, expectedThreadId: null);

    public bool MatchesServer(McpServerAuthStatus value, string? expectedThreadId)
        => string.Equals(serverName, value.ServerName, StringComparison.Ordinal)
            && HasRegistrationIdentity == value.HasRegistrationIdentity
            && (!HasRegistrationIdentity || string.Equals(EnvironmentId, value.EnvironmentId, StringComparison.Ordinal))
            && (string.Equals(ThreadId, value.ThreadId, StringComparison.Ordinal)
                || (value.ThreadId is null && expectedThreadId is not null && string.Equals(ThreadId, expectedThreadId, StringComparison.Ordinal)));

    internal bool MatchesServerIgnoringMissingThread(McpServerAuthStatus value)
        => value.ThreadId is null
            && string.Equals(serverName, value.ServerName, StringComparison.Ordinal)
            && HasRegistrationIdentity == value.HasRegistrationIdentity
            && (!HasRegistrationIdentity || string.Equals(EnvironmentId, value.EnvironmentId, StringComparison.Ordinal));

    internal bool MatchesServerNameAndThread(McpServerAuthStatus value)
        => string.Equals(serverName, value.ServerName, StringComparison.Ordinal)
            && (value.ThreadId is null || string.Equals(ThreadId, value.ThreadId, StringComparison.Ordinal));

    internal bool MatchesServerForHydration(string expectedServerName, string? expectedThreadId)
        => !HasRegistrationIdentity
            && string.Equals(serverName, expectedServerName, StringComparison.Ordinal)
            && (string.Equals(ThreadId, expectedThreadId, StringComparison.Ordinal)
                || (ThreadId is null && expectedThreadId is not null));

    internal void ApplyHydratedRegistrationIdentity(McpServerAuthStatus status, SafeMarkdownService markdown)
    {
        UpdateRegistrationIdentity(status.HasRegistrationIdentity, status.EnvironmentId, status.DisplayEnvironmentId, status.ThreadId, markdown);
        SetRegistrationAmbiguous(status.IsRegistrationAmbiguous);
        if (status.IsRegistrationAmbiguous && !CanDismiss && !CanOpenAuthorization)
        {
            StatusText = markdown.ToSafeText(status.Message ?? "Registration identity could not be resolved. Sign-in is disabled.").Trim();
        }
    }

    internal void MergeHydratedRegistrationIdentityFrom(
        McpServerAuthPresentationViewModel source,
        McpServerAuthStatus status,
        SafeMarkdownService markdown)
    {
        UpdateRegistrationIdentity(status.HasRegistrationIdentity, status.EnvironmentId, status.DisplayEnvironmentId, status.ThreadId, markdown);
        SetRegistrationAmbiguous(status.IsRegistrationAmbiguous);

        if (source.operationId is not null)
        {
            operationId = source.operationId;
            State = source.State;
            StatusText = source.StatusText;
        }

        if (source.authorizationActionId is not null)
        {
            authorizationActionId = source.authorizationActionId;
        }

        OnPropertyChanged(nameof(CanDismiss));
        OnPropertyChanged(nameof(CanOpenAuthorization));
        OnPropertyChanged(nameof(CanStartLogin));
        LoginCommand.RaiseCanExecuteChanged();
        DismissCommand.RaiseCanExecuteChanged();
        OpenAuthorizationCommand.RaiseCanExecuteChanged();
    }

    internal void SetHydrationAmbiguous(string message, SafeMarkdownService markdown)
    {
        SetRegistrationAmbiguous(true);
        if (!CanDismiss && !CanOpenAuthorization)
        {
            StatusText = markdown.ToSafeText(message).Trim();
        }
    }

    internal void SetRegistrationAmbiguous(bool value)
    {
        if (registrationAmbiguous == value)
        {
            return;
        }

        registrationAmbiguous = value;
        OnPropertyChanged(nameof(IsRegistrationAmbiguous));
        OnPropertyChanged(nameof(CanStartLogin));
        LoginCommand.RaiseCanExecuteChanged();
    }

    private void UpdateRegistrationIdentity(bool hasIdentity, string? environmentId, string? displayEnvironmentId, string? threadId, SafeMarkdownService markdown)
    {
        if (HasRegistrationIdentity != hasIdentity)
        {
            HasRegistrationIdentity = hasIdentity;
            OnPropertyChanged(nameof(HasRegistrationIdentity));
        }

        safeDisplayEnvironmentId = displayEnvironmentId is null ? null : markdown.ToSafeText(displayEnvironmentId).Trim();
        if (!string.Equals(EnvironmentId, environmentId, StringComparison.Ordinal))
        {
            EnvironmentId = environmentId;
            OnPropertyChanged(nameof(EnvironmentId));
        }

        if (!string.Equals(ThreadId, threadId, StringComparison.Ordinal))
        {
            ThreadId = threadId;
            OnPropertyChanged(nameof(ThreadId));
        }

        string safeName = safeDisplayServerName;
        string displayName = !hasIdentity
            ? safeName
            : environmentId is null
                ? $"{safeName} (thread-wide)"
                : $"{safeName} (environment {safeDisplayEnvironmentId ?? "[hidden]"})";
        ServerName = displayName;
    }

    private async Task DismissAsync()
    {
        if (operationId is not { Length: > 0 } id)
        {
            return;
        }

        await dismissMcpLogin(id).ConfigureAwait(false);
        if (string.Equals(operationId, id, StringComparison.Ordinal))
        {
            operationId = null;
            OnPropertyChanged(nameof(CanDismiss));
            DismissCommand.RaiseCanExecuteChanged();
        }
    }

    private Task OpenAuthorizationAsync()
        => authorizationActionId is { Length: > 0 } id ? openAuthorization(id) : Task.CompletedTask;
}
