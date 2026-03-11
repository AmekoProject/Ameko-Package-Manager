// SPDX-License-Identifier: MPL-2.0

using System;
using System.Threading.Tasks;
using Ameko.Views.Controls;
using AssCS;
using AssCS.History;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Holo.Media;
using Holo.Providers;
using Holo.Scripting;
using Holo.Scripting.Models;
using Microsoft.Extensions.Logging;
using TabItem = Avalonia.Controls.TabItem;

public class TimingButler() : HoloScript(_info)
{
    private static readonly PackageInfo _info = new()
    {
        DisplayName = "Timing Butler",
        QualifiedName = "9volt.timingButler",
        Exports =
        [
            new MethodInfo
            {
                DisplayName = "Call Butler",
                QualifiedName = "call",
                Submenu = "Timing Butler",
            },
            new MethodInfo
            {
                DisplayName = "Butler Configuration",
                QualifiedName = "config",
                Submenu = "Timing Butler",
            },
        ],
        LogDisplay = LogDisplay.Ephemeral,
        Submenu = null,
        Headless = true,
    };

    // Services
    // csharpier-ignore-start
    private readonly IProjectProvider _projectProvider = ScriptServiceLocator.Get<IProjectProvider>();
    private readonly IScriptConfigurationService _configService = ScriptServiceLocator.Get<IScriptConfigurationService>();
    private readonly IMessageService _messageService = ScriptServiceLocator.Get<IMessageService>();
    private readonly IWindowService _windowService = ScriptServiceLocator.Get<IWindowService>();
    // csharpier-ignore-end

    /// <inheritdoc />
    public override async Task<ExecutionResult> ExecuteAsync(
        string? methodName,
        ScriptArgs? args = null
    )
    {
        switch (methodName)
        {
            case "call":
                return await CallButler();
            case "config":
                return await ShowConfigDialog();
            default:
                Logger.LogError("Unknown method {MethodName}", methodName);
                return ExecutionResult.Failure;
        }
    }

    /// <summary>
    /// Perform Timing Butler on the active event
    /// </summary>
    private async Task<ExecutionResult> CallButler()
    {
        var config = GetEffectiveConfig();
        var wsp = _projectProvider.Current.WorkingSpace;

        // Checks
        if (wsp is null)
        {
            _messageService.Enqueue("No workspace loaded!", TimeSpan.FromSeconds(3));
            return ExecutionResult.Success;
        }

        var mc = wsp.MediaController;
        if (!mc.IsVideoLoaded)
        {
            _messageService.Enqueue("No video loaded!", TimeSpan.FromSeconds(3));
            return ExecutionResult.Success;
        }

        // Get the active event and the first non-comment before it
        // This is naive, the events aren't guaranteed to be in chronological order
        var active = wsp.SelectionManager.ActiveEvent;
        var previous = active;
        while (true)
        {
            previous = wsp.Document.EventManager.GetBefore(previous.Id);
            if (previous is null || !previous.IsComment)
                break;
        }

        // Do timing
        var changed =
            DoStartTime(active, previous, mc.VideoInfo, config)
            || DoEndTime(active, mc.VideoInfo, config);

        // If changes happened, commit
        if (changed)
            wsp.Commit(active, ChangeType.ModifyEventMeta);

        return ExecutionResult.Success;
    }

    /// <summary>
    /// Show the configuration dialog
    /// </summary>
    private async Task<ExecutionResult> ShowConfigDialog()
    {
        _ = GetEffectiveConfig(); // Make sure the config file exists, etc.
        var window = CreateConfigWindow();
        await _windowService.ShowDialogAsync(window);
        return ExecutionResult.Success;
    }

    /// <summary>
    /// Act on the start time
    /// </summary>
    /// <returns><see langword="true"/> if a modification was made</returns>
    private static bool DoStartTime(
        Event active,
        Event? previous,
        VideoInfo video,
        ButlerConfig config
    )
    {
        var startFrame = video.FrameFromTime(active.Start);
        var nearestKf = FindNearestKeyframeTo(startFrame, video);
        var kfTime = video.TimeFromFrame(nearestKf);

        // Already on a keyframe, do nothing
        if (startFrame == nearestKf)
            return false;

        // Try snapping to the keyframe
        var delta = active.Start.TotalMilliseconds - kfTime.TotalMilliseconds;
        if (delta > 0) // Kf is earlier than the start time
        {
            if (delta < config.SnapStartEarlierThreshold)
                active.Start = kfTime;
        }
        else // Kf is later than the start time
        {
            if (Math.Abs(delta) < config.SnapStartLaterThreshold)
                active.Start = kfTime;
        }
        var snap = active.Start == kfTime; // True if changed
        var chain = false;

        // Try linking with the previous event's end
        if (previous is not null)
        {
            delta = active.Start.TotalMilliseconds - previous.End.TotalMilliseconds;
            if (Math.Abs(delta) < config.ChainThreshold)
            {
                chain = true;
                if (config.ChainGap == 0)
                {
                    active.Start = previous.End;
                }
                else
                {
                    var prevEndFrame = video.FrameFromTime(previous.End);
                    active.Start = video.TimeFromFrame(prevEndFrame + config.ChainGap);
                }
            }
        }

        if (snap || chain)
            return true;

        // Add lead-in
        active.Start -= Time.FromMillis(config.LeadIn);
        return true;
    }

    /// <summary>
    /// Act on the end time
    /// </summary>
    /// <returns><see langword="true"/> if a modification was made</returns>
    private static bool DoEndTime(Event active, VideoInfo video, ButlerConfig config)
    {
        var endFrame = video.FrameFromTime(active.End);
        var nearestKf = FindNearestKeyframeTo(endFrame, video);
        var kfTime = video.TimeFromFrame(nearestKf);

        // Already on a keyframe, do nothing
        if (endFrame == nearestKf)
            return false;

        // Try snapping to the keyframe
        var delta = active.End.TotalMilliseconds - kfTime.TotalMilliseconds;
        // Kf is earlier than the end time
        if (delta > 0)
        {
            if (delta >= config.SnapEndEarlierThreshold)
                return false;

            active.End = kfTime;
            return true;
        }

        // Kf is later than the end time

        // If the keyframe is > 850ms away (and the threshold is > kf), use the event's CPS to decide what to do
        // If CPS <= 15, then add min(LeadOut, kf-500ms)
        // If CPS > 15, snap to kf
        delta = Math.Abs(delta);
        if (delta >= 850 && delta <= config.SnapEndLaterThreshold)
        {
            if (active.Cps <= 15)
            {
                var leadOut = Math.Min(config.LeadOut, delta - 500);
                active.End += Time.FromMillis(leadOut);
            }
            else
            {
                active.End = kfTime;
            }
            return true;
        }

        // Normal end snapping
        if (delta >= config.SnapEndLaterThreshold)
            return false;

        active.End = kfTime;
        return true;
    }

    /// <summary>
    /// Finds the nearest keyframe to a frame using Binary Search
    /// </summary>
    /// <param name="frame">Starting frame</param>
    /// <param name="video">Video information</param>
    /// <returns>Nearest keyframe</returns>
    private static int FindNearestKeyframeTo(int frame, VideoInfo video)
    {
        var keyframes = video.Keyframes;
        var idx = Array.BinarySearch(keyframes, frame);
        if (idx >= 0)
            return idx;

        idx = ~idx;
        if (idx <= 0)
            return keyframes[0];
        if (idx > keyframes.Length)
            return keyframes[^1];

        var before = keyframes[idx - 1];
        var after = keyframes[idx];

        // Return whichever is closer
        return Math.Abs(frame - before) <= Math.Abs(after - frame) ? before : after;
    }

    /// <summary>
    /// Get the effective configuration
    /// </summary>
    /// <remarks>
    /// Prioritizes the loaded project over local config options,
    /// setting sane defaults if both are absent.
    /// </remarks>
    private ButlerConfig GetEffectiveConfig()
    {
        var result = new ButlerConfig
        {
            LeadIn = GetOrCreateConfig("LeadIn", 120),
            LeadOut = GetOrCreateConfig("LeadOut", 400),
            SnapStartEarlierThreshold = GetOrCreateConfig("SnapStartEarlier", 350),
            SnapStartLaterThreshold = GetOrCreateConfig("SnapStartLater", 100),
            SnapEndEarlierThreshold = GetOrCreateConfig("SnapEndEarlier", 300),
            SnapEndLaterThreshold = GetOrCreateConfig("SnapEndLater", 900),
            ChainThreshold = GetOrCreateConfig("Chain", 620),
            ChainGap = GetOrCreateConfig("ChainGap", 0),
        };

        return result;
    }

    /// <summary>
    /// Helper method for getting a config value and setting it if absent
    /// </summary>
    private int GetOrCreateConfig(string key, int defaultValue)
    {
        var project = _projectProvider.Current;

        // Look for the key in the project config
        if (_configService.TryGet(this, project, key, out int value) && value != -1)
            return value;

        // Look for the key in the local config
        if (_configService.TryGet(this, key, out value))
            return value;

        // Key does not exist anywhere, add default value to local config
        _configService.Set(this, key, defaultValue);
        _configService.Set(this, project, key, -1); // Add fall-through value to project
        return defaultValue;
    }

    private Window CreateConfigWindow()
    {
        var project = _projectProvider.Current;
        var tc = new TabControl();
        var window = new Window
        {
            Title = "Timing Butler Configuration",
            SizeToContent = SizeToContent.WidthAndHeight,
            Padding = new Thickness(5),
            Content = tc,
        };
        // Project tab
        // csharpier-ignore-start
        var pPanel = new StackPanel();
        var pTabItem = new TabItem { Header = "Project Config", Content = pPanel };
        var pLeadInBox = new NumberTextBox { Value = GetForProject("LeadIn"), Minimum = -1 };
        var pLeadOutBox = new NumberTextBox { Value = GetForProject("LeadOut"), Minimum = -1 };
        var pSnapStartEarlierBox = new NumberTextBox { Value = GetForProject("SnapStartEarlier"), Minimum = -1, };
        var pSnapStartLaterBox = new NumberTextBox { Value = GetForProject("SnapStartLater"), Minimum = -1 };
        var pSnapEndEarlierBox = new NumberTextBox { Value = GetForProject("SnapEndEarlier"), Minimum = -1 };
        var pSnapEndLaterBox = new NumberTextBox { Value = GetForProject("SnapEndLater"), Minimum = -1 };
        var pChainBox = new NumberTextBox { Value = GetForProject("Chain"), Minimum = -1 };
        var pChainGapBox = new NumberTextBox { Value = GetForProject("ChainGap"), Minimum = -1 };
        var pSaveBtn = new Button { Content = "Save" };

        // Local tab
        var lPanel = new StackPanel();
        var lTabItem = new TabItem { Header = "Personal Config", Content = lPanel };
        var lLeadInBox = new NumberTextBox { Value = GetForLocal("LeadIn"), Minimum = 0 };
        var lLeadOutBox = new NumberTextBox { Value = GetForLocal("LeadOut"), Minimum = 0 };
        var lSnapStartEarlierBox = new NumberTextBox { Value = GetForLocal("SnapStartEarlier"), Minimum = 0 };
        var lSnapStartLaterBox = new NumberTextBox { Value = GetForLocal("SnapStartLater"), Minimum = 0 };
        var lSnapEndEarlierBox = new NumberTextBox { Value = GetForLocal("SnapEndEarlier"), Minimum = 0 };
        var lSnapEndLaterBox = new NumberTextBox { Value = GetForLocal("SnapEndLater"), Minimum = 0 };
        var lChainBox = new NumberTextBox { Value = GetForLocal("Chain"), Minimum = 0 };
        var lChainGapBox = new NumberTextBox { Value = GetForLocal("ChainGap"), Minimum = 0 };
        var lSaveBtn = new Button { Content = "Save" };
        // csharpier-ignore-end

        // Save buttons
        pSaveBtn.Click += (_, _) =>
        {
            SetForProject("LeadIn", pLeadInBox.Value);
            SetForProject("LeadOut", pLeadOutBox.Value);
            SetForProject("SnapStartEarlier", pSnapStartEarlierBox.Value);
            SetForProject("SnapStartLater", pSnapStartLaterBox.Value);
            SetForProject("SnapEndEarlier", pSnapEndEarlierBox.Value);
            SetForProject("SnapEndLater", pSnapEndLaterBox.Value);
            SetForProject("Chain", pChainBox.Value);
            SetForProject("ChainGap", pChainGapBox.Value);
            _messageService.Enqueue("Project config saved!", TimeSpan.FromSeconds(5));
        };

        lSaveBtn.Click += (_, _) =>
        {
            SetForLocal("LeadIn", lLeadInBox.Value);
            SetForLocal("LeadOut", lLeadOutBox.Value);
            SetForLocal("SnapStartEarlier", lSnapStartEarlierBox.Value);
            SetForLocal("SnapStartLater", lSnapStartLaterBox.Value);
            SetForLocal("SnapEndEarlier", lSnapEndEarlierBox.Value);
            SetForLocal("SnapEndLater", lSnapEndLaterBox.Value);
            SetForLocal("Chain", lChainBox.Value);
            SetForLocal("ChainGap", lChainGapBox.Value);
            _messageService.Enqueue("Local config saved!", TimeSpan.FromSeconds(5));
        };
        // Construction
        pPanel.Children.AddRange([
            new Label { Content = "Use -1 to fall back to local configuration." },
            new Label { Content = "Lead In (if no snap) (ms):" },
            pLeadInBox,
            new Label { Content = "Lead Out (if no snap) (ms):" },
            pLeadOutBox,
            new Label { Content = "Snap Start to Earlier Keyframe (ms):" },
            pSnapStartEarlierBox,
            new Label { Content = "Snap Start to Later Keyframe (ms):" },
            pSnapStartLaterBox,
            new Label { Content = "Snap End to Earlier Keyframe (ms):" },
            pSnapEndEarlierBox,
            new Label { Content = "Snap End to Later Keyframe (ms):" },
            pSnapEndLaterBox,
            new Label { Content = "Chain Adjacent Events (ms):" },
            pChainBox,
            new Label { Content = "Chain Gap (frames):" },
            pChainGapBox,
            pSaveBtn,
        ]);
        lPanel.Children.AddRange([
            new Label { Content = "Lead In (if no snap) (ms):" },
            lLeadInBox,
            new Label { Content = "Lead Out (if no snap) (ms):" },
            lLeadOutBox,
            new Label { Content = "Snap Start to Earlier Keyframe (ms):" },
            lSnapStartEarlierBox,
            new Label { Content = "Snap Start to Later Keyframe (ms):" },
            lSnapStartLaterBox,
            new Label { Content = "Snap End to Earlier Keyframe (ms):" },
            lSnapEndEarlierBox,
            new Label { Content = "Snap End to Later Keyframe (ms):" },
            lSnapEndLaterBox,
            new Label { Content = "Chain Adjacent Events (ms):" },
            lChainBox,
            new Label { Content = "Chain Gap (frames):" },
            lChainGapBox,
            lSaveBtn,
        ]);

        tc.Items.Add(pTabItem);
        tc.Items.Add(lTabItem);
        return window;

        // Local helper functions
        int GetForProject(string key) =>
            _configService.TryGet(this, project, key, out int value) ? value : -1;
        int GetForLocal(string key) => _configService.TryGet(this, key, out int value) ? value : 0;
        void SetForProject(string key, decimal value) =>
            _configService.Set(this, project, key, (int)value);
        void SetForLocal(string key, decimal value) => _configService.Set(this, key, (int)value);
    }

    /// <summary>
    /// Configuration model
    /// </summary>
    private class ButlerConfig
    {
        public int LeadIn { get; init; }
        public int LeadOut { get; init; }
        public int SnapStartEarlierThreshold { get; init; }
        public int SnapStartLaterThreshold { get; init; }
        public int SnapEndEarlierThreshold { get; init; }
        public int SnapEndLaterThreshold { get; init; }
        public int ChainThreshold { get; init; }
        public int ChainGap { get; init; }
    }
}
