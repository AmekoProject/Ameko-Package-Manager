// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssCS;
using AssCS.History;
using AssCS.Overrides.Blocks;
using AssCS.Utilities;
using Avalonia;
using Avalonia.Controls;
using Holo;
using Holo.Configuration;
using Holo.Providers;
using Holo.Scripting;
using Holo.Scripting.Models;

/// <summary>
/// Script for manipulating line breaks
/// </summary>
public class LineBreaker : HoloScript
{
    private static readonly PackageInfo _info = new()
    {
        DisplayName = "Line Breaker",
        QualifiedName = "9volt.lineBreaker",
        Exports =
        [
            new MethodInfo
            {
                DisplayName = "Add",
                QualifiedName = "add",
                Submenu = "Line Breaker",
            },
            new MethodInfo
            {
                DisplayName = "Remove",
                QualifiedName = "remove",
                Submenu = "Line Breaker",
            },
            new MethodInfo
            {
                DisplayName = "Move Left",
                QualifiedName = "left",
                Submenu = "Line Breaker",
            },
            new MethodInfo
            {
                DisplayName = "Move Right",
                QualifiedName = "right",
                Submenu = "Line Breaker",
            },
            new MethodInfo
            {
                DisplayName = "Configure...",
                QualifiedName = "config",
                Submenu = "Line Breaker",
            },
        ],
        LogDisplay = LogDisplay.Ephemeral,
        Submenu = "Line Breaker",
        Headless = true,
    };

    private readonly IProjectProvider _projectProvider;
    private readonly IConfiguration _configuration;
    private readonly IMessageService _messageService;
    private readonly IScriptConfigurationService _scriptConfig;
    private readonly IWindowService _windowService;

    /// <inheritdoc />
    public LineBreaker()
        : base(_info)
    {
        _projectProvider = ScriptServiceLocator.Get<IProjectProvider>();
        _configuration = ScriptServiceLocator.Get<IConfiguration>();
        _messageService = ScriptServiceLocator.Get<IMessageService>();
        _scriptConfig = ScriptServiceLocator.Get<IScriptConfigurationService>();
        _windowService = ScriptServiceLocator.Get<IWindowService>();
    }

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(
        string? methodName,
        ScriptArgs? args = null
    )
    {
        if (string.IsNullOrEmpty(methodName))
            return ExecutionResult.Failure;

        // Get current event
        var workspace = _projectProvider.Current.WorkingSpace;
        var @event = _projectProvider.Current.WorkingSpace?.SelectionManager.ActiveEvent;
        if (workspace is null || @event is null)
        {
            _messageService.Enqueue("No event selected", TimeSpan.FromSeconds(3));
            return ExecutionResult.Success;
        }

        // Get space handling setting
        if (!_scriptConfig.TryGet<SpaceHandling>(this, "SpaceHandling", out var handling))
        {
            handling = SpaceHandling.Before;
            _scriptConfig.Set(this, "SpaceHandling", handling);
        }

        if (methodName == "config")
            return await ShowConfigWindow(handling);

        return methodName switch
        {
            "add" => Add(workspace, @event, handling),
            "remove" => Remove(workspace, @event, handling),
            "left" => MoveLeft(workspace, @event, handling),
            "right" => MoveRight(workspace, @event, handling),
            _ => ExecutionResult.Success,
        };
    }

    /// <summary>
    /// Adds the line break to the middle of the event
    /// </summary>
    private ExecutionResult Add(Workspace workspace, Event @event, SpaceHandling handling)
    {
        // Check if the event already has a line break
        if (Regex.Matches(@event.Text, @"\\[Nn]").Count > 0)
        {
            _messageService.Enqueue("Event already has a break", TimeSpan.FromSeconds(3));
            return ExecutionResult.Success;
        }

        // Find midpoint
        var spaceIndexes = GetSpaceIndexes(@event);

        // Nowhere to put the newline
        if (spaceIndexes.Count == 0)
        {
            _messageService.Enqueue(
                "No location found to place line break",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        var middleSpaceIdx = spaceIndexes[spaceIndexes.Count / 2];

        // Get break type
        var lineBreak =
            _projectProvider.Current.UseSoftLinebreaks ?? _configuration.UseSoftLinebreaks
                ? @"\n"
                : @"\N";

        // Insert break
        switch (handling)
        {
            case SpaceHandling.Before:
                @event.Text = @event.Text.Insert(middleSpaceIdx, lineBreak);
                break;
            case SpaceHandling.After:
                @event.Text = @event.Text.Insert(middleSpaceIdx + 1, lineBreak);
                break;
            case SpaceHandling.Replace:
                @event.Text = ReplaceAt(@event.Text, middleSpaceIdx, lineBreak);
                break;
        }

        // Commit change
        workspace.Commit(@event, ChangeType.ModifyEventText);
        return ExecutionResult.Success;
    }

    /// <summary>
    /// Removes the linebreak from the event
    /// </summary>
    private ExecutionResult Remove(Workspace workspace, Event @event, SpaceHandling handling)
    {
        // Check if the event has a line break
        if (Regex.Matches(@event.Text, @"\\[Nn]").Count != 1)
        {
            _messageService.Enqueue(
                "Event does not contain a singular break",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        // Remove line breaks
        switch (handling)
        {
            case SpaceHandling.Replace:
                @event.Text = @event.Text.ReplaceMany([@"\N", @"\n"], " ");
                break;
            default:
                @event.Text = @event.Text.ReplaceMany([@"\N", @"\n"], string.Empty);
                break;
        }

        // Commit change
        workspace.Commit(@event, ChangeType.ModifyEventText);
        return ExecutionResult.Success;
    }

    /// <summary>
    /// Moves the linebreak left one space, if applicable
    /// </summary>
    private ExecutionResult MoveLeft(Workspace workspace, Event @event, SpaceHandling handling)
    {
        // Check if the event has a line break
        if (Regex.Matches(@event.Text, @"\\[Nn]").Count != 1)
        {
            _messageService.Enqueue(
                "Event does not contain a singular break",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        // Find the current linebreak
        var spaceIndexes = GetSpaceIndexes(@event);
        var currentBreakIdx = @event.Text.IndexOf(
            @"\N",
            StringComparison.InvariantCultureIgnoreCase
        );

        // The required index changes based on the mode
        if (handling == SpaceHandling.After)
            currentBreakIdx -= 1;

        var newSpaceIdx = spaceIndexes.LastOrDefault(p => p < currentBreakIdx, -1);

        // Nowhere to put the newline
        if (spaceIndexes.Count == 0 || newSpaceIdx == -1)
        {
            _messageService.Enqueue(
                "No location found to move line break to",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        // Get break type
        var lineBreak =
            _projectProvider.Current.UseSoftLinebreaks ?? _configuration.UseSoftLinebreaks
                ? @"\n"
                : @"\N";

        // Remove existing break and insert new one
        switch (handling)
        {
            case SpaceHandling.Before:
                @event.Text = @event
                    .Text.ReplaceMany([@"\N", @"\n"], string.Empty)
                    .Insert(newSpaceIdx, lineBreak);
                break;
            case SpaceHandling.After:
                @event.Text = @event
                    .Text.ReplaceMany([@"\N", @"\n"], string.Empty)
                    .Insert(newSpaceIdx + 1, lineBreak);
                break;
            case SpaceHandling.Replace:
                @event.Text = @event.Text.ReplaceMany([@"\N", @"\n"], " ");
                @event.Text = ReplaceAt(@event.Text, newSpaceIdx, lineBreak);
                break;
        }

        // Commit change
        workspace.Commit(@event, ChangeType.ModifyEventText);
        return ExecutionResult.Success;
    }

    /// <summary>
    /// Moves the linebreak right one space, if applicable
    /// </summary>
    private ExecutionResult MoveRight(Workspace workspace, Event @event, SpaceHandling handling)
    {
        // Check if the event has a line break
        if (Regex.Matches(@event.Text, @"\\[Nn]").Count != 1)
        {
            _messageService.Enqueue(
                "Event does not contain a singular break",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        // Find the current linebreak
        var spaceIndexes = GetSpaceIndexes(@event);
        var currentBreakIdx = @event.Text.IndexOf(
            @"\N",
            StringComparison.InvariantCultureIgnoreCase
        );

        // The required index changes based on the mode
        if (handling != SpaceHandling.Replace)
            currentBreakIdx += 3;
        else
            currentBreakIdx -= 2;

        var newSpaceIdx = spaceIndexes.FirstOrDefault(p => p > currentBreakIdx, -1);

        // Nowhere to put the newline
        if (spaceIndexes.Count == 0 || newSpaceIdx == -1)
        {
            _messageService.Enqueue(
                "No location found to move line break to",
                TimeSpan.FromSeconds(3)
            );
            return ExecutionResult.Success;
        }

        // Get break type
        var lineBreak =
            _projectProvider.Current.UseSoftLinebreaks ?? _configuration.UseSoftLinebreaks
                ? @"\n"
                : @"\N";

        // Remove existing break and insert new one
        switch (handling)
        {
            case SpaceHandling.Before:
                @event.Text = @event
                    .Text.ReplaceMany([@"\N", @"\n"], string.Empty)
                    .Insert(newSpaceIdx - 2, lineBreak);
                break;
            case SpaceHandling.After:
                @event.Text = @event
                    .Text.ReplaceMany([@"\N", @"\n"], string.Empty)
                    .Insert(newSpaceIdx - 1, lineBreak);
                break;
            case SpaceHandling.Replace:
                @event.Text = @event.Text.ReplaceMany([@"\N", @"\n"], " ");
                @event.Text = ReplaceAt(@event.Text, newSpaceIdx - 1, lineBreak);
                break;
        }

        // Commit change
        workspace.Commit(@event, ChangeType.ModifyEventText);
        return ExecutionResult.Success;
    }

    /// <summary>
    /// Get a list of space indexes in the event
    /// </summary>
    /// <param name="event">Event to parse</param>
    /// <returns>List of indexes containing a space</returns>
    private static List<int> GetSpaceIndexes(Event @event)
    {
        var result = new List<int>();
        var cumulativeIndex = 0;
        foreach (var block in @event.ParseTags())
        {
            if (block.Type != BlockType.Plain)
            {
                cumulativeIndex += block.Text.Length;
                continue;
            }

            for (var i = 0; i < block.Text.Length; i++)
            {
                if (block.Text[i] == ' ')
                    result.Add(cumulativeIndex + i);
            }
            cumulativeIndex += block.Text.Length;
        }
        return result;
    }

    /// <summary>
    /// Display the configuration window
    /// </summary>
    private async Task<ExecutionResult> ShowConfigWindow(SpaceHandling handling)
    {
        var win = new Window { Title = "Line Breaker Configuration" };

        var label = new Label { Content = "How should spaces be handled?" };
        var beforeRb = new RadioButton
        {
            Content = "Place linebreak before space",
            GroupName = "spaceHandlingField",
            IsChecked = handling == SpaceHandling.Before,
        };
        var afterRb = new RadioButton
        {
            Content = "Place linebreak after space",
            GroupName = "spaceHandlingField",
            IsChecked = handling == SpaceHandling.After,
        };
        var replaceRb = new RadioButton
        {
            Content = "Replace space with linebreak",
            GroupName = "spaceHandlingField",
            IsChecked = handling == SpaceHandling.Replace,
        };

        var saveButton = new Button { Content = "Save" };
        saveButton.Click += (_, _) =>
        {
            var requestedHandling = handling;
            if (beforeRb.IsChecked ?? false)
                requestedHandling = SpaceHandling.Before;
            else if (afterRb.IsChecked ?? false)
                requestedHandling = SpaceHandling.After;
            else if (replaceRb.IsChecked ?? false)
                requestedHandling = SpaceHandling.Replace;

            _scriptConfig.Set(this, "SpaceHandling", requestedHandling);
            win.Close();
        };

        var panel = new StackPanel { Margin = new Thickness(5), Spacing = 5 };
        panel.Children.AddRange([label, beforeRb, afterRb, replaceRb, saveButton]);
        win.Content = panel;
        await _windowService.ShowDialogAsync(win);

        return ExecutionResult.Success;
    }

    /// <summary>
    /// Replace the character at the index with the given string
    /// </summary>
    private static string ReplaceAt(string str, int index, string newThing)
    {
        ArgumentNullException.ThrowIfNull(str);

        if (index < 0 || index >= str.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        return str[..index] + newThing + str[(index + 1)..];
    }

    /// <summary>
    /// Enum for controlling how to handle spaces
    /// </summary>
    public enum SpaceHandling
    {
        Before = 0,
        After = 1,
        Replace = 2,
    }
}
