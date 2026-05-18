using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuHelpCardBuilderTests
{
    private readonly FeishuHelpCardBuilder _builder = new();

    [Fact]
    public void BuildCommandListCard_UsesCategoryButtonCallback()
    {
        var cardJson = _builder.BuildCommandListCard(CreateCategories());
        var actionValue = GetActionValue(JsonDocument.Parse(cardJson).RootElement.GetProperty("body").GetProperty("elements"), "show_category");

        Assert.Equal(JsonValueKind.Object, actionValue.ValueKind);
        Assert.Equal("show_category", actionValue.GetProperty("action").GetString());
        Assert.Equal("general", actionValue.GetProperty("category_id").GetString());
    }

    [Fact]
    public void BuildFilteredCard_UsesCommandButtonCallback()
    {
        var cardJson = _builder.BuildFilteredCard(CreateCategories(), "help");
        var actionValue = GetActionValue(JsonDocument.Parse(cardJson).RootElement.GetProperty("body").GetProperty("elements"), "select_command");

        Assert.Equal(JsonValueKind.Object, actionValue.ValueKind);
        Assert.Equal("select_command", actionValue.GetProperty("action").GetString());
        Assert.Equal("feishuhelp", actionValue.GetProperty("command_id").GetString());
    }

    [Fact]
    public void BuildCommandListCardV2_UsesCategoryButtonCallback()
    {
        var card = _builder.BuildCommandListCardV2(CreateCategories(), showRefreshButton: false);
        using var bodyDoc = JsonDocument.Parse(JsonSerializer.Serialize(card.Body!.Elements));
        var actionValue = GetActionValue(bodyDoc.RootElement, "show_category");

        Assert.Equal(JsonValueKind.Object, actionValue.ValueKind);
        Assert.Equal("show_category", actionValue.GetProperty("action").GetString());
        Assert.Equal("general", actionValue.GetProperty("category_id").GetString());
        Assert.False(ContainsProperty(bodyDoc.RootElement, "overflow"));
        Assert.False(ContainsProperty(bodyDoc.RootElement, "extra"));
    }

    [Fact]
    public void BuildCommandListCard_IncludesReplyTtsToggle_WhenEnabled()
    {
        var cardJson = _builder.BuildCommandListCard(CreateCategories(), replyTtsEnabled: true);
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.True(ContainsStringValue(elements, "语音回复：开"));
        Assert.True(ContainsAction(elements, "toggle_reply_tts"));
    }

    [Fact]
    public void BuildCommandListCardV2_IncludesReplyTtsToggle_WhenDisabled()
    {
        var card = _builder.BuildCommandListCardV2(CreateCategories(), replyTtsEnabled: false);
        using var bodyDoc = JsonDocument.Parse(JsonSerializer.Serialize(card.Body!.Elements));

        Assert.True(ContainsStringValue(bodyDoc.RootElement, "语音回复：关"));
        Assert.True(ContainsAction(bodyDoc.RootElement, "toggle_reply_tts"));
    }

    [Fact]
    public void BuildCategoryCommandsCardV2_UsesCommandButtonCallback()
    {
        var category = CreateCategories()[0];
        var card = _builder.BuildCategoryCommandsCardV2(category);
        using var bodyDoc = JsonDocument.Parse(JsonSerializer.Serialize(card.Body!.Elements));
        var actionValue = GetActionValue(bodyDoc.RootElement, "select_command");

        Assert.Equal(JsonValueKind.Object, actionValue.ValueKind);
        Assert.Equal("select_command", actionValue.GetProperty("action").GetString());
        Assert.Equal("feishuhelp", actionValue.GetProperty("command_id").GetString());
    }

    [Fact]
    public void BuildFilteredCardV2_UsesCommandButtonCallback()
    {
        var card = _builder.BuildFilteredCardV2(CreateCategories(), "help");
        using var bodyDoc = JsonDocument.Parse(JsonSerializer.Serialize(card.Body!.Elements));
        var actionValue = GetActionValue(bodyDoc.RootElement, "select_command");

        Assert.Equal(JsonValueKind.Object, actionValue.ValueKind);
        Assert.Equal("select_command", actionValue.GetProperty("action").GetString());
        Assert.Equal("feishuhelp", actionValue.GetProperty("command_id").GetString());
        Assert.False(ContainsProperty(bodyDoc.RootElement, "overflow"));
        Assert.False(ContainsProperty(bodyDoc.RootElement, "extra"));
    }

    [Fact]
    public void BuildCommandListCard_AppendsSuperpowersQuickActionsFooter()
    {
        var cardJson = _builder.BuildCommandListCard(CreateCategories());
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.True(ContainsStringValue(elements, SuperpowersQuickActionDefaults.InstructionText));

        var quickInput = GetInputByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName);
        Assert.Equal(
            SuperpowersQuickActionDefaults.QuickInputPlaceholder,
            quickInput.GetProperty("placeholder").GetProperty("content").GetString());
        Assert.Equal(1, CountInputsByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName));

        var quickInputAction = quickInput
            .GetProperty("behaviors")[0]
            .GetProperty("value");
        Assert.Equal(
            FeishuHelpCardAction.SubmitSuperpowersQuickInputAction,
            quickInputAction.GetProperty("action").GetString());
        Assert.False(ContainsAction(elements, FeishuHelpCardAction.ExecuteSuperpowersPlanAction));
        Assert.False(ContainsAction(elements, FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction));
    }

    [Fact]
    public void BuildFilteredCard_AppendsSuperpowersQuickActionsFooter()
    {
        var cardJson = _builder.BuildFilteredCard(CreateCategories(), "help");
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.True(ContainsStringValue(elements, SuperpowersQuickActionDefaults.InstructionText));
        Assert.Equal(1, CountInputsByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName));

        var quickInput = GetInputByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName);
        Assert.Equal(
            FeishuHelpCardAction.SubmitSuperpowersQuickInputAction,
            quickInput.GetProperty("behaviors")[0].GetProperty("value").GetProperty("action").GetString());
        Assert.False(ContainsAction(elements, FeishuHelpCardAction.ExecuteSuperpowersPlanAction));
        Assert.False(ContainsAction(elements, FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction));
    }

    [Fact]
    public void BuildCategoryCommandsCardV2_AppendsSuperpowersQuickActionsFooter()
    {
        var category = CreateCategories()[0];
        var card = _builder.BuildCategoryCommandsCardV2(category);
        using var bodyDoc = JsonDocument.Parse(JsonSerializer.Serialize(card.Body!.Elements));

        Assert.True(ContainsStringValue(bodyDoc.RootElement, SuperpowersQuickActionDefaults.InstructionText));
        Assert.Equal(
            SuperpowersQuickActionDefaults.QuickInputFieldName,
            GetInputByName(bodyDoc.RootElement, SuperpowersQuickActionDefaults.QuickInputFieldName)
                .GetProperty("name")
                .GetString());

        Assert.False(ContainsAction(bodyDoc.RootElement, FeishuHelpCardAction.ExecuteSuperpowersPlanAction));
        Assert.False(ContainsAction(bodyDoc.RootElement, FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction));
    }

    [Fact]
    public void BuildCommandListCard_AppendsGoalQuickActionFooter_BelowSuperpowers()
    {
        var cardJson = _builder.BuildCommandListCard(CreateCategories());
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.True(ContainsStringValue(elements, GoalQuickActionDefaults.InstructionText));
        Assert.Equal(1, CountInputsByName(elements, GoalQuickActionDefaults.QuickInputFieldName));

        var goalInput = GetInputByName(elements, GoalQuickActionDefaults.QuickInputFieldName);
        Assert.Equal(
            GoalQuickActionDefaults.QuickInputPlaceholder,
            goalInput.GetProperty("placeholder").GetProperty("content").GetString());
        Assert.Equal(
            FeishuHelpCardAction.SubmitGoalQuickInputAction,
            goalInput.GetProperty("behaviors")[0].GetProperty("value").GetProperty("action").GetString());
        Assert.True(ContainsAction(elements, "status_goal"));
        Assert.True(ContainsStringValue(elements, "/goal pause"));
        Assert.True(ContainsStringValue(elements, "/goal clear"));
        Assert.True(ContainsStringValue(elements, "/goal resume"));
        Assert.True(ContainsAction(elements, "status_goal"));
        Assert.True(ContainsAction(elements, "pause_goal"));
        Assert.True(ContainsAction(elements, "clear_goal"));
        Assert.True(ContainsAction(elements, "resume_goal"));
        Assert.Equal(
            2,
            CountColumnSetsContainingActions(
                elements,
                FeishuHelpCardAction.StatusGoalAction,
                FeishuHelpCardAction.PauseGoalAction,
                FeishuHelpCardAction.ClearGoalAction,
                FeishuHelpCardAction.ResumeGoalAction));
        Assert.False(ContainsStringValue(elements, GoalQuickActionDefaults.TemporaryExitButtonText));

        Assert.True(
            GetInputIndexByName(elements, GoalQuickActionDefaults.QuickInputFieldName)
            > GetInputIndexByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName));
    }

    [Fact]
    public void BuildCommandListCard_HidesGoalQuickActionButtons_WhenDisabled()
    {
        var cardJson = _builder.BuildCommandListCard(CreateCategories(), showGoalQuickActionButtons: false);
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal(1, CountInputsByName(elements, GoalQuickActionDefaults.QuickInputFieldName));
        Assert.False(ContainsStringValue(elements, GoalQuickActionDefaults.StatusButtonText));
        Assert.False(ContainsStringValue(elements, GoalQuickActionDefaults.PauseButtonText));
        Assert.False(ContainsStringValue(elements, GoalQuickActionDefaults.ClearButtonText));
        Assert.False(ContainsStringValue(elements, GoalQuickActionDefaults.ResumeButtonText));
        Assert.False(ContainsAction(elements, "status_goal"));
        Assert.False(ContainsAction(elements, "pause_goal"));
        Assert.False(ContainsAction(elements, "clear_goal"));
        Assert.False(ContainsAction(elements, "resume_goal"));
    }

    [Fact]
    public void BuildCommandListCard_HidesSuperpowersQuickActions_WhenDisabled()
    {
        var cardJson = _builder.BuildCommandListCard(
            CreateCategories(),
            showSuperpowersQuickActions: false);
        using var document = JsonDocument.Parse(cardJson);
        var elements = document.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal(0, CountInputsByName(elements, SuperpowersQuickActionDefaults.QuickInputFieldName));
        Assert.False(ContainsStringValue(elements, SuperpowersQuickActionDefaults.InstructionText));
        Assert.Equal(1, CountInputsByName(elements, GoalQuickActionDefaults.QuickInputFieldName));
        Assert.True(ContainsStringValue(elements, GoalQuickActionDefaults.InstructionText));
    }

    private static JsonElement GetActionValue(JsonElement elements, string action)
    {
        if (TryGetActionValue(elements, action, out var actionValue))
        {
            return actionValue;
        }

        throw new Xunit.Sdk.XunitException($"No button callback found for action '{action}' in card payload.");
    }

    private static bool ContainsAction(JsonElement elements, string action)
    {
        return TryGetActionValue(elements, action, out _);
    }

    private static bool TryGetActionValue(JsonElement element, string action, out JsonElement actionValue)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (TryGetButtonActionValue(element, action, out actionValue))
            {
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryGetActionValue(property.Value, action, out actionValue))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryGetActionValue(item, action, out actionValue))
                {
                    return true;
                }
            }
        }

        actionValue = default;
        return false;
    }

    private static int CountColumnSetsContainingActions(JsonElement element, params string[] actions)
    {
        var actionSet = actions.ToHashSet(StringComparer.Ordinal);
        var count = 0;
        CountColumnSetsContainingActionsCore(element, actionSet, ref count);
        return count;
    }

    private static void CountColumnSetsContainingActionsCore(JsonElement element, HashSet<string> actions, ref int count)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tag", out var tagElement)
                && string.Equals(tagElement.GetString(), "column_set", StringComparison.Ordinal)
                && actions.Any(action => TryGetActionValue(element, action, out _)))
            {
                count++;
            }

            foreach (var property in element.EnumerateObject())
            {
                CountColumnSetsContainingActionsCore(property.Value, actions, ref count);
            }

            return;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            CountColumnSetsContainingActionsCore(item, actions, ref count);
        }
    }

    private static bool TryGetButtonActionValue(JsonElement element, string action, out JsonElement actionValue)
    {
        if (element.TryGetProperty("tag", out var tag) && tag.GetString() == "button")
        {
            if (element.TryGetProperty("behaviors", out var behaviors))
            {
                foreach (var behavior in behaviors.EnumerateArray())
                {
                    if (!behavior.TryGetProperty("value", out var value))
                    {
                        continue;
                    }

                    if (TryMatchActionValue(value, action, out actionValue))
                    {
                        return true;
                    }
                }
            }

            if (element.TryGetProperty("value", out var directValue)
                && TryMatchActionValue(directValue, action, out actionValue))
            {
                return true;
            }
        }

        actionValue = default;
        return false;
    }

    private static bool TryMatchActionValue(JsonElement value, string action, out JsonElement actionValue)
    {
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("action", out var actionProp) &&
            actionProp.GetString() == action)
        {
            actionValue = value;
            return true;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(value.GetString()!);
                if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                    doc.RootElement.TryGetProperty("action", out var nestedActionProp) &&
                    nestedActionProp.GetString() == action)
                {
                    actionValue = doc.RootElement.Clone();
                    return true;
                }
            }
            catch (JsonException)
            {
            }
        }

        actionValue = default;
        return false;
    }

    private static bool ContainsProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName) || ContainsProperty(property.Value, propertyName))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsProperty(item, propertyName))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsStringValue(JsonElement element, string expected)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), expected, StringComparison.Ordinal),
            JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsStringValue(property.Value, expected)),
            JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsStringValue(item, expected)),
            _ => false
        };
    }

    private static JsonElement GetInputByName(JsonElement element, string inputName)
    {
        if (TryGetInputByName(element, inputName, out var input))
        {
            return input;
        }

        throw new Xunit.Sdk.XunitException($"No input found for name '{inputName}' in card payload.");
    }

    private static bool TryGetInputByName(JsonElement element, string inputName, out JsonElement input)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tag", out var tag)
                && tag.GetString() == "input"
                && element.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), inputName, StringComparison.Ordinal))
            {
                input = element;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryGetInputByName(property.Value, inputName, out input))
                {
                    return true;
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryGetInputByName(item, inputName, out input))
                {
                    return true;
                }
            }
        }

        input = default;
        return false;
    }

    private static int CountInputsByName(JsonElement element, string inputName)
    {
        var count = 0;
        CountInputsByName(element, inputName, ref count);
        return count;
    }

    private static void CountInputsByName(JsonElement element, string inputName, ref int count)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("tag", out var tag)
                && tag.GetString() == "input"
                && element.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), inputName, StringComparison.Ordinal))
            {
                count++;
            }

            foreach (var property in element.EnumerateObject())
            {
                CountInputsByName(property.Value, inputName, ref count);
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CountInputsByName(item, inputName, ref count);
            }
        }
    }

    private static int GetInputIndexByName(JsonElement elements, string inputName)
    {
        if (elements.ValueKind != JsonValueKind.Array)
        {
            throw new Xunit.Sdk.XunitException("Card body elements must be an array.");
        }

        var index = 0;
        foreach (var item in elements.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("tag", out var tag)
                && tag.GetString() == "input"
                && item.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), inputName, StringComparison.Ordinal))
            {
                return index;
            }

            index++;
        }

        throw new Xunit.Sdk.XunitException($"No input found for name '{inputName}' in card payload.");
    }

    private static List<FeishuCommandCategory> CreateCategories()
    {
        return
        [
            new FeishuCommandCategory
            {
                Id = "general",
                Name = "General",
                Commands =
                [
                    new FeishuCommand
                    {
                        Id = "feishuhelp",
                        Name = "/feishuhelp",
                        Description = "Show help for Feishu commands",
                        Usage = "/feishuhelp",
                        ExecuteText = "/feishuhelp",
                        Category = "General",
                        ToolId = "claude-code"
                    }
                ]
            }
        ];
    }
}
