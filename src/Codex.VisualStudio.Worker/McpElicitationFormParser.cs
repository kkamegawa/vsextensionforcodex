using System.Globalization;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using Codex.VisualStudio.Contracts;

namespace Codex.VisualStudio.Worker;

internal enum McpElicitationParseStatus
{
    Supported,
    Unsupported,
    Invalid,
}

internal sealed record McpElicitationParseRefusal(
    McpElicitationParseStatus Status,
    UnsupportedInteractionKind? UnsupportedKind,
    string SafeReason);

internal sealed class McpElicitationForm
{
    private readonly IReadOnlyDictionary<string, McpFormField> fields;

    internal McpElicitationForm(McpElicitationRequest request, IReadOnlyDictionary<string, McpFormField> fields)
    {
        Request = request;
        this.fields = fields;
    }

    public McpElicitationRequest Request { get; }

    public bool TryValidateValues(
        IReadOnlyList<McpElicitationValue> values,
        out JsonElement content,
        out string safeError)
    {
        content = JsonSerializer.SerializeToElement(new { });
        safeError = string.Empty;
        if (values.Count > McpElicitationFormParser.MaximumFields)
        {
            safeError = "The response contains too many fields.";
            return false;
        }

        var supplied = new Dictionary<string, McpElicitationValue>(StringComparer.Ordinal);
        foreach (McpElicitationValue value in values)
        {
            if (value is null
                || string.IsNullOrEmpty(value.FieldName)
                || !fields.ContainsKey(value.FieldName)
                || !supplied.TryAdd(value.FieldName, value))
            {
                safeError = "The response contains an unknown or duplicate field.";
                return false;
            }
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach ((string fieldName, McpFormField field) in fields)
        {
            if (!supplied.TryGetValue(fieldName, out McpElicitationValue? value))
            {
                if (field.Definition.Required)
                {
                    safeError = "A required field is missing.";
                    return false;
                }

                continue;
            }

            if (!TryReadValue(field, value, out object? parsed))
            {
                safeError = "A field value does not match the requested schema.";
                return false;
            }

            result.Add(fieldName, parsed);
        }

        content = JsonSerializer.SerializeToElement(result);
        if (Encoding.UTF8.GetByteCount(content.GetRawText()) > McpElicitationFormParser.MaximumPayloadBytes)
        {
            content = JsonSerializer.SerializeToElement(new { });
            safeError = "The response exceeds the supported size limit.";
            return false;
        }

        return true;
    }

    internal static bool TryReadValue(McpFormField field, McpElicitationValue value, out object? parsed)
    {
        parsed = null;
        bool hasString = value.StringValue is not null;
        bool hasInteger = value.IntegerValue.HasValue;
        bool hasNumber = value.NumberValue.HasValue;
        bool hasBoolean = value.BooleanValue.HasValue;
        bool hasChoices = value.ChoiceIds.Count != 0;
        bool hasUnexpectedChoices = field.Definition.Type is not (McpElicitationFieldType.SingleSelect or McpElicitationFieldType.MultiSelect)
            && value.ChoiceIds.Count > 0;

        if (hasUnexpectedChoices)
        {
            return false;
        }

        switch (field.Definition.Type)
        {
            case McpElicitationFieldType.Text:
                if (!hasString || hasInteger || hasNumber || hasBoolean || hasChoices
                    || !McpElicitationFormParser.ValidateString(field, value.StringValue!))
                {
                    return false;
                }

                parsed = value.StringValue;
                return true;
            case McpElicitationFieldType.WholeNumber:
                if (!hasInteger || hasString || hasNumber || hasBoolean || hasChoices
                    || !field.IsIntegerInRange(value.IntegerValue!.Value))
                {
                    return false;
                }

                parsed = value.IntegerValue.Value;
                return true;
            case McpElicitationFieldType.Number:
                if (!hasNumber || hasString || hasInteger || hasBoolean || hasChoices
                    || !double.IsFinite(value.NumberValue!.Value)
                    || !field.IsNumberInRange(value.NumberValue.Value))
                {
                    return false;
                }

                parsed = value.NumberValue.Value;
                return true;
            case McpElicitationFieldType.Boolean:
                if (!hasBoolean || hasString || hasInteger || hasNumber || hasChoices)
                {
                    return false;
                }

                parsed = value.BooleanValue!.Value;
                return true;
            case McpElicitationFieldType.SingleSelect:
            case McpElicitationFieldType.MultiSelect:
                if (hasString || hasInteger || hasNumber || hasBoolean
                    || (field.Definition.Type == McpElicitationFieldType.SingleSelect && value.ChoiceIds.Count != 1)
                    || (ulong)value.ChoiceIds.Count < (field.Definition.MinimumSelections ?? 0)
                    || (field.Definition.MaximumSelections.HasValue
                        && (ulong)value.ChoiceIds.Count > field.Definition.MaximumSelections.Value))
                {
                    return false;
                }

                var mapped = new List<string>(value.ChoiceIds.Count);
                var used = new HashSet<string>(StringComparer.Ordinal);
                foreach (string choiceId in value.ChoiceIds)
                {
                    if (string.IsNullOrEmpty(choiceId)
                        || !used.Add(choiceId)
                        || !field.ChoiceValues.TryGetValue(choiceId, out string? choiceValue))
                    {
                        return false;
                    }

                    mapped.Add(choiceValue);
                }

                parsed = field.Definition.Type == McpElicitationFieldType.SingleSelect
                    ? mapped[0]
                    : mapped;
                return true;
            default:
                return false;
        }
    }
}

internal static class McpElicitationFormParser
{
    internal const int MaximumFields = 64;
    internal const int MaximumChoicesPerField = 256;
    internal const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumDisplayTextLength = 16 * 1024;

    public static bool TryParse(
        JsonElement parameters,
        string requestId,
        Func<string> newOpaqueId,
        Func<string, ProtectedAuthorizationUrlInfo?> protectAuthorizationUrl,
        out McpElicitationForm? form,
        out McpElicitationParseRefusal refusal)
        => TryParse(parameters, requestId, newOpaqueId, out form, out refusal, protectAuthorizationUrl);

    public static bool TryParse(
        JsonElement parameters,
        string requestId,
        Func<string> newOpaqueId,
        out McpElicitationForm? form,
        out McpElicitationParseRefusal refusal,
        Func<string, ProtectedAuthorizationUrlInfo?>? protectAuthorizationUrl = null)
    {
        form = null;
        refusal = new McpElicitationParseRefusal(McpElicitationParseStatus.Invalid, null, "The request is invalid.");
        if (parameters.ValueKind != JsonValueKind.Object
            || Encoding.UTF8.GetByteCount(parameters.GetRawText()) > MaximumPayloadBytes)
        {
            return false;
        }

        string? mode = GetString(parameters, "mode");
        if (string.Equals(mode, "openai/userVerification", StringComparison.Ordinal))
        {
            refusal = new McpElicitationParseRefusal(
                McpElicitationParseStatus.Unsupported,
                UnsupportedInteractionKind.UserVerification,
                "Native user verification is not supported by this client.");
            return false;
        }

        if (string.Equals(mode, "openai/form", StringComparison.Ordinal)
            || string.Equals(mode, "openaiForm", StringComparison.Ordinal))
        {
            refusal = Unsupported("This MCP form schema is not supported.");
            return false;
        }

        if (string.Equals(mode, "url", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(requestId)
                || !TryGetString(parameters, "serverName", out string urlServerName)
                || !TryGetString(parameters, "elicitationId", out string elicitationId)
                || !TryGetString(parameters, "message", out string urlMessage)
                || !TryGetString(parameters, "url", out string authorizationUrl)
                || urlServerName.Length > MaximumDisplayTextLength
                || elicitationId.Length > MaximumDisplayTextLength
                || urlMessage.Length > MaximumDisplayTextLength
                || protectAuthorizationUrl is null)
            {
                return false;
            }

            // Store the raw URL only after validating every required field. The callback applies
            // the protected browser URL policy and returns only opaque action/origin data.
            ProtectedAuthorizationUrlInfo? protectedUrl = protectAuthorizationUrl(authorizationUrl);
            if (protectedUrl is null)
            {
                return false;
            }

            string? urlThreadId = GetString(parameters, "threadId");
            string? urlTurnId = GetString(parameters, "turnId");
            form = new McpElicitationForm(
                new McpElicitationRequest
                {
                    RequestId = requestId,
                    ServerName = urlServerName,
                    ThreadId = urlThreadId,
                    TurnId = urlTurnId,
                    Kind = McpElicitationKind.Url,
                    Message = urlMessage,
                    OriginDisplay = protectedUrl.OriginDisplay,
                    OpenAuthorizationActionId = protectedUrl.ActionId,
                },
                new Dictionary<string, McpFormField>(StringComparer.Ordinal));
            refusal = new McpElicitationParseRefusal(McpElicitationParseStatus.Supported, null, string.Empty);
            return true;
        }

        if (!string.Equals(mode, "form", StringComparison.Ordinal))
        {
            refusal = Unsupported("This MCP elicitation mode is not supported.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(requestId)
            || !TryGetString(parameters, "serverName", out string serverName)
            || !parameters.TryGetProperty("requestedSchema", out JsonElement schema)
            || !TryReadObjectSchema(schema, newOpaqueId, out Dictionary<string, McpFormField>? parsedFields))
        {
            return false;
        }

        string? threadId = GetString(parameters, "threadId");
        string? turnId = GetString(parameters, "turnId");
        string? message = GetString(parameters, "message");
        if (serverName.Length > MaximumDisplayTextLength || (message?.Length ?? 0) > MaximumDisplayTextLength)
        {
            return false;
        }

        form = new McpElicitationForm(
            new McpElicitationRequest
            {
                RequestId = requestId,
                ServerName = serverName,
                ThreadId = threadId,
                TurnId = turnId,
                Kind = McpElicitationKind.Form,
                Message = message ?? string.Empty,
                Fields = parsedFields!.Values.Select(field => field.Definition).ToArray(),
            },
            parsedFields!);
        refusal = new McpElicitationParseRefusal(McpElicitationParseStatus.Supported, null, string.Empty);
        return true;
    }

    private static bool TryReadObjectSchema(
        JsonElement schema,
        Func<string> newOpaqueId,
        out Dictionary<string, McpFormField>? fields)
    {
        fields = null;
        if (schema.ValueKind != JsonValueKind.Object
            || !OnlyProperties(schema, "$schema", "type", "properties", "required")
            || GetString(schema, "type") != "object"
            || !schema.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var requiredNames = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out JsonElement required)
            && required.ValueKind != JsonValueKind.Null)
        {
            if (required.ValueKind != JsonValueKind.Array || required.GetArrayLength() > MaximumFields)
            {
                return false;
            }

            foreach (JsonElement requiredName in required.EnumerateArray())
            {
                if (requiredName.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(requiredName.GetString())
                    || !requiredNames.Add(requiredName.GetString()!))
                {
                    return false;
                }
            }
        }

        if (properties.EnumerateObject().Count() > MaximumFields)
        {
            return false;
        }

        var parsed = new Dictionary<string, McpFormField>(StringComparer.Ordinal);
        foreach (JsonProperty property in properties.EnumerateObject())
        {
            if (string.IsNullOrEmpty(property.Name)
                || property.Name.Length > MaximumDisplayTextLength
                || parsed.ContainsKey(property.Name)
                || !TryReadField(property.Name, property.Value, requiredNames.Contains(property.Name), newOpaqueId, out McpFormField? field))
            {
                return false;
            }

            parsed.Add(property.Name, field!);
        }

        if (requiredNames.Any(name => !parsed.ContainsKey(name)))
        {
            return false;
        }

        fields = parsed;
        return true;
    }

    private static bool TryReadField(
        string name,
        JsonElement schema,
        bool required,
        Func<string> newOpaqueId,
        out McpFormField? field)
    {
        field = null;
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string? type = GetString(schema, "type");
        McpElicitationFieldType fieldType;
        McpElicitationFormat format = McpElicitationFormat.None;
        uint? minLength = null;
        uint? maxLength = null;
        double? minimum = null;
        double? maximum = null;
        decimal? exactMinimum = null;
        decimal? exactMaximum = null;
        ulong? minSelections = null;
        ulong? maxSelections = null;
        var choices = new List<McpElicitationChoice>();
        var choiceValues = new Dictionary<string, string>(StringComparer.Ordinal);
        McpElicitationValue? defaultValue = null;

        if (type == "string")
        {
            bool hasChoiceSchema = schema.TryGetProperty("enum", out _) || schema.TryGetProperty("oneOf", out _);
            bool hasFormat = schema.TryGetProperty("format", out JsonElement formatElement) && formatElement.ValueKind != JsonValueKind.Null;
            if (hasChoiceSchema)
            {
                fieldType = McpElicitationFieldType.SingleSelect;
                if (!OnlyProperties(schema, "type", "title", "description", "default", "enum", "enumNames", "oneOf"))
                {
                    return false;
                }

                if (!ReadSingleChoices(schema, newOpaqueId, choices, choiceValues, out string? defaultChoice))
                {
                    return false;
                }

                if (defaultChoice is not null)
                {
                    defaultValue = new McpElicitationValue { FieldName = name, ChoiceIds = [defaultChoice] };
                }
            }
            else
            {
                fieldType = McpElicitationFieldType.Text;
                if (!OnlyProperties(schema, "type", "title", "description", "default", "format", "minLength", "maxLength"))
                {
                    return false;
                }

                if (schema.TryGetProperty("minLength", out JsonElement minLengthElement)
                    && !ReadNullableUInt32(minLengthElement, out minLength))
                {
                    return false;
                }

                if (schema.TryGetProperty("maxLength", out JsonElement maxLengthElement)
                    && !ReadNullableUInt32(maxLengthElement, out maxLength))
                {
                    return false;
                }

                if (minLength.HasValue && maxLength.HasValue && minLength.Value > maxLength.Value)
                {
                    return false;
                }

                if (hasFormat && !TryReadFormat(formatElement, out format))
                {
                    return false;
                }

                if (schema.TryGetProperty("default", out JsonElement defaultElement)
                    && defaultElement.ValueKind != JsonValueKind.Null)
                {
                    if (defaultElement.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    defaultValue = new McpElicitationValue { FieldName = name, StringValue = defaultElement.GetString() };
                }
            }
        }
        else if (type is "number" or "integer")
        {
            fieldType = type == "integer" ? McpElicitationFieldType.WholeNumber : McpElicitationFieldType.Number;
            if (!OnlyProperties(schema, "type", "title", "description", "default", "minimum", "maximum"))
            {
                return false;
            }

            if (schema.TryGetProperty("minimum", out JsonElement minimumElement)
                && minimumElement.ValueKind != JsonValueKind.Null
                && !ReadFiniteDouble(minimumElement, out minimum))
            {
                return false;
            }

            if (schema.TryGetProperty("maximum", out JsonElement maximumElement)
                && maximumElement.ValueKind != JsonValueKind.Null
                && !ReadFiniteDouble(maximumElement, out maximum))
            {
                return false;
            }

            if (minimum.HasValue && maximum.HasValue && minimum.Value > maximum.Value)
            {
                return false;
            }

            if (type == "integer")
            {
                if (minimum.HasValue && !TryReadExactBound(schema.GetProperty("minimum"), true, out exactMinimum)
                    || maximum.HasValue && !TryReadExactBound(schema.GetProperty("maximum"), false, out exactMaximum))
                {
                    return false;
                }

                if (exactMinimum.HasValue && exactMaximum.HasValue && exactMinimum.Value > exactMaximum.Value)
                {
                    return false;
                }
            }

            if (schema.TryGetProperty("default", out JsonElement defaultElement)
                && defaultElement.ValueKind != JsonValueKind.Null)
            {
                if (fieldType == McpElicitationFieldType.WholeNumber)
                {
                    if (defaultElement.ValueKind != JsonValueKind.Number || !defaultElement.TryGetInt64(out long integerDefault))
                    {
                        return false;
                    }

                    defaultValue = new McpElicitationValue { FieldName = name, IntegerValue = integerDefault };
                }
                else
                {
                    if (!ReadFiniteDouble(defaultElement, out double? numberDefault))
                    {
                        return false;
                    }

                    defaultValue = new McpElicitationValue { FieldName = name, NumberValue = numberDefault };
                }
            }
        }
        else if (type == "boolean")
        {
            fieldType = McpElicitationFieldType.Boolean;
            if (!OnlyProperties(schema, "type", "title", "description", "default"))
            {
                return false;
            }

            if (schema.TryGetProperty("default", out JsonElement defaultElement)
                && defaultElement.ValueKind != JsonValueKind.Null)
            {
                if (defaultElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    return false;
                }

                defaultValue = new McpElicitationValue { FieldName = name, BooleanValue = defaultElement.GetBoolean() };
            }
        }
        else if (type == "array")
        {
            fieldType = McpElicitationFieldType.MultiSelect;
            if (!OnlyProperties(schema, "type", "title", "description", "default", "items", "minItems", "maxItems")
                || !schema.TryGetProperty("items", out JsonElement items)
                || !ReadMultiChoices(items, newOpaqueId, choices, choiceValues))
            {
                return false;
            }

            if (schema.TryGetProperty("minItems", out JsonElement minItemsElement)
                && !ReadNullableUInt64(minItemsElement, out minSelections))
            {
                return false;
            }

            if (schema.TryGetProperty("maxItems", out JsonElement maxItemsElement)
                && !ReadNullableUInt64(maxItemsElement, out maxSelections))
            {
                return false;
            }

            if ((minSelections.HasValue && minSelections.Value > (ulong)choices.Count)
                || (minSelections.HasValue && maxSelections.HasValue && minSelections.Value > maxSelections.Value))
            {
                return false;
            }

            if (schema.TryGetProperty("default", out JsonElement defaultElement)
                && defaultElement.ValueKind != JsonValueKind.Null)
            {
                if (defaultElement.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                var defaultIds = new List<string>();
                var seenDefaults = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement defaultChoice in defaultElement.EnumerateArray())
                {
                    if (defaultChoice.ValueKind != JsonValueKind.String)
                    {
                        return false;
                    }

                    string? defaultId = choiceValues.SingleOrDefault(
                        pair => string.Equals(pair.Value, defaultChoice.GetString(), StringComparison.Ordinal)).Key;
                    if (defaultId is null || !seenDefaults.Add(defaultId))
                    {
                        return false;
                    }

                    defaultIds.Add(defaultId);
                }

                if ((ulong)defaultIds.Count < (minSelections ?? 0)
                    || (maxSelections.HasValue && (ulong)defaultIds.Count > maxSelections.Value))
                {
                    return false;
                }

                defaultValue = new McpElicitationValue { FieldName = name, ChoiceIds = defaultIds };
            }
        }
        else
        {
            return false;
        }

        string title = GetString(schema, "title") ?? name;
        string? description = GetString(schema, "description");
        if (title.Length > MaximumDisplayTextLength || (description?.Length ?? 0) > MaximumDisplayTextLength)
        {
            return false;
        }

        var definition = new McpElicitationField
        {
            Name = name,
            Title = title,
            Description = description,
            Required = required,
            Type = fieldType,
            Format = format,
            MinLength = minLength,
            MaxLength = maxLength,
            MinimumNumber = minimum,
            MaximumNumber = maximum,
            MinimumInteger = exactMinimum.HasValue ? (long?)decimal.Ceiling(exactMinimum.Value) : null,
            MaximumInteger = exactMaximum.HasValue ? (long?)decimal.Floor(exactMaximum.Value) : null,
            MinimumSelections = minSelections,
            MaximumSelections = maxSelections,
            Choices = choices,
            DefaultValue = defaultValue,
        };
        var parsedField = new McpFormField(definition, choiceValues, exactMinimum, exactMaximum);
        if (defaultValue is not null && !McpElicitationForm.TryReadValue(parsedField, defaultValue, out _))
        {
            return false;
        }

        field = parsedField;
        return true;
    }

    private static bool ReadSingleChoices(
        JsonElement schema,
        Func<string> newOpaqueId,
        List<McpElicitationChoice> choices,
        Dictionary<string, string> values,
        out string? defaultChoiceId)
    {
        defaultChoiceId = null;
        List<(string Value, string Label)> options;
        if (schema.TryGetProperty("oneOf", out JsonElement oneOf))
        {
            if (oneOf.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            options = new List<(string, string)>();
            foreach (JsonElement option in oneOf.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object
                    || !OnlyProperties(option, "const", "title")
                    || !TryGetString(option, "const", out string choiceValue)
                    || !TryGetString(option, "title", out string choiceLabel))
                {
                    return false;
                }

                options.Add((choiceValue, choiceLabel));
            }
        }
        else if (schema.TryGetProperty("enum", out JsonElement enumArray))
        {
            if (enumArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            options = new List<(string, string)>();
            string[] labels = [];
            if (schema.TryGetProperty("enumNames", out JsonElement enumNames)
                && enumNames.ValueKind != JsonValueKind.Null)
            {
                if (enumNames.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }

                labels = enumNames.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : string.Empty).ToArray();
                if (labels.Any(string.IsNullOrEmpty) || labels.Length != enumArray.GetArrayLength())
                {
                    return false;
                }
            }

            int index = 0;
            foreach (JsonElement item in enumArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string value = item.GetString()!;
                options.Add((value, labels.Length == 0 ? value : labels[index]));
                index++;
            }
        }
        else
        {
            return false;
        }

        if (!AddChoices(options, newOpaqueId, choices, values))
        {
            return false;
        }

        if (schema.TryGetProperty("default", out JsonElement defaultElement)
            && defaultElement.ValueKind != JsonValueKind.Null)
        {
            if (defaultElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string defaultValue = defaultElement.GetString()!;
            defaultChoiceId = values.SingleOrDefault(pair => string.Equals(pair.Value, defaultValue, StringComparison.Ordinal)).Key;
            if (defaultChoiceId is null)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReadMultiChoices(
        JsonElement items,
        Func<string> newOpaqueId,
        List<McpElicitationChoice> choices,
        Dictionary<string, string> values)
    {
        if (items.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        List<(string Value, string Label)> options = [];
        if (items.TryGetProperty("anyOf", out JsonElement anyOf))
        {
            if (!OnlyProperties(items, "anyOf") || anyOf.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement option in anyOf.EnumerateArray())
            {
                if (option.ValueKind != JsonValueKind.Object
                    || !OnlyProperties(option, "const", "title")
                    || !TryGetString(option, "const", out string choiceValue)
                    || !TryGetString(option, "title", out string choiceLabel))
                {
                    return false;
                }

                options.Add((choiceValue, choiceLabel));
            }
        }
        else if (items.TryGetProperty("enum", out JsonElement enumArray))
        {
            if (!OnlyProperties(items, "type", "enum")
                || GetString(items, "type") != "string"
                || enumArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (JsonElement item in enumArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                string value = item.GetString()!;
                options.Add((value, value));
            }
        }
        else
        {
            return false;
        }

        if (!AddChoices(options, newOpaqueId, choices, values))
        {
            return false;
        }

        return true;
    }

    private static bool AddChoices(
        List<(string Value, string Label)> options,
        Func<string> newOpaqueId,
        List<McpElicitationChoice> choices,
        Dictionary<string, string> values)
    {
        if (options.Count is 0 or > MaximumChoicesPerField)
        {
            return false;
        }

        var seenValues = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string value, string label) in options)
        {
            if (value.Length > MaximumDisplayTextLength
                || label.Length > MaximumDisplayTextLength
                || !seenValues.Add(value))
            {
                return false;
            }

            string choiceId = newOpaqueId();
            if (string.IsNullOrWhiteSpace(choiceId) || !values.TryAdd(choiceId, value))
            {
                return false;
            }

            choices.Add(new McpElicitationChoice { ChoiceId = choiceId, Label = label });
        }

        return true;
    }

    internal static bool ValidateString(McpFormField field, string value)
    {
        if (value.Length > MaximumPayloadBytes || !IsWellFormedUnicode(value))
        {
            return false;
        }

        uint runeLength = 0;
        foreach (System.Text.Rune _ in value.EnumerateRunes())
        {
            runeLength++;
        }
        McpElicitationField definition = field.Definition;
        if ((definition.MinLength.HasValue && runeLength < definition.MinLength.Value)
            || (definition.MaxLength.HasValue && runeLength > definition.MaxLength.Value))
        {
            return false;
        }

        return definition.Format switch
        {
            McpElicitationFormat.None => true,
            McpElicitationFormat.Email => IsEmail(value),
            McpElicitationFormat.Uri => Uri.TryCreate(value, UriKind.Absolute, out _),
            McpElicitationFormat.Date => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            McpElicitationFormat.DateTime => IsRfc3339DateTime(value),
            _ => false,
        };
    }

    private static bool IsEmail(string value)
    {
        if (value.IndexOfAny(['\r', '\n', '<', '>']) >= 0
            || !MailAddress.TryCreate(value, out MailAddress? address))
        {
            return false;
        }

        return string.Equals(address.Address, value, StringComparison.Ordinal);
    }

    private static bool IsRfc3339DateTime(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                value,
                "^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:[Zz]|[+-][0-9]{2}:[0-9]{2})$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            return false;
        }

        int separator = value.IndexOf('T');
        if (separator != 10
            || !DateOnly.TryParseExact(value[..separator], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return false;
        }

        return true;
    }

    private static bool IsWellFormedUnicode(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (char.IsHighSurrogate(current))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                {
                    return false;
                }

                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadFormat(JsonElement element, out McpElicitationFormat format)
    {
        format = GetString(element) switch
        {
            "email" => McpElicitationFormat.Email,
            "uri" => McpElicitationFormat.Uri,
            "date" => McpElicitationFormat.Date,
            "date-time" => McpElicitationFormat.DateTime,
            _ => McpElicitationFormat.None,
        };
        return format != McpElicitationFormat.None;
    }

    private static bool ReadNullableUInt32(JsonElement element, out uint? value)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out uint number))
        {
            value = number;
            return true;
        }

        value = null;
        return false;
    }

    private static bool ReadNullableUInt64(JsonElement element, out ulong? value)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out ulong number))
        {
            value = number;
            return true;
        }

        value = null;
        return false;
    }

    private static bool ReadFiniteDouble(JsonElement element, out double? value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number) && double.IsFinite(number))
        {
            value = number;
            return true;
        }

        value = null;
        return false;
    }

    private static bool TryReadExactBound(JsonElement element, bool lowerBound, out decimal? bound)
    {
        bound = null;
        if (element.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        if (element.TryGetDecimal(out decimal decimalValue))
        {
            bound = decimalValue;
            decimal integerBound = lowerBound ? decimal.Ceiling(decimalValue) : decimal.Floor(decimalValue);
            return integerBound >= long.MinValue && integerBound <= long.MaxValue;
        }

        if (!element.TryGetDouble(out double number) || !double.IsFinite(number))
        {
            return false;
        }

        // Bounds beyond the signed integer domain are either impossible or redundant.
        if (lowerBound && number > long.MaxValue || !lowerBound && number < long.MinValue)
        {
            return false;
        }

        if (lowerBound && number < long.MinValue || !lowerBound && number > long.MaxValue)
        {
            return true;
        }

        if (!decimal.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue))
        {
            return false;
        }

        bound = decimalValue;
        decimal rounded = lowerBound ? decimal.Ceiling(decimalValue) : decimal.Floor(decimalValue);
        return rounded >= long.MinValue && rounded <= long.MaxValue;
    }

    private static bool OnlyProperties(JsonElement element, params string[] allowed)
    {
        var names = new HashSet<string>(allowed, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            && (value = property.GetString() ?? string.Empty) is not null;
    }

    private static string? GetString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out JsonElement property)
            ? GetString(property)
            : null;

    private static string? GetString(JsonElement element)
        => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

    private static McpElicitationParseRefusal Unsupported(string reason)
        => new(McpElicitationParseStatus.Unsupported, UnsupportedInteractionKind.McpElicitationSchema, reason);

}

internal sealed record McpFormField(
    McpElicitationField Definition,
    IReadOnlyDictionary<string, string> ChoiceValues,
    decimal? ExactMinimum,
    decimal? ExactMaximum)
{
    public bool IsNumberInRange(double value)
        => (!Definition.MinimumNumber.HasValue || value >= Definition.MinimumNumber.Value)
            && (!Definition.MaximumNumber.HasValue || value <= Definition.MaximumNumber.Value);

    public bool IsIntegerInRange(long value)
        => (!ExactMinimum.HasValue || (decimal)value >= ExactMinimum.Value)
            && (!ExactMaximum.HasValue || (decimal)value <= ExactMaximum.Value);
}
