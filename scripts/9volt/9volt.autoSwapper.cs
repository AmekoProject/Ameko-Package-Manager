// SPDX-License-Identifier: MPL-2.0

using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AssCS;
using AssCS.History;
using Holo.Models;
using Holo.Providers;
using Holo.Scripting;
using Holo.Scripting.Models;

public class AutoSwapper() : HoloScript(_info)
{
    private static readonly PackageInfo _info = new()
    {
        DisplayName = Strings.DisplayName,
        QualifiedName = "9volt.autoSwapper",
        Headless = true,
        Exports =
        [
            new MethodInfo
            {
                DisplayName = Strings.SwapHonorifics,
                QualifiedName = "honorifics",
                Submenu = Strings.DisplayName,
            },
            new MethodInfo
            {
                DisplayName = Strings.SwapMahjongTerms,
                QualifiedName = "mahjong",
                Submenu = Strings.DisplayName,
            },
            new MethodInfo
            {
                DisplayName = Strings.SwapVerbalTics,
                QualifiedName = "verbalTics",
                Submenu = Strings.DisplayName,
            },
            new MethodInfo
            {
                DisplayName = Strings.Configuration,
                QualifiedName = "config",
                Submenu = Strings.DisplayName,
            },
        ],
    };

    private readonly IProjectProvider _prjProvider = ScriptServiceLocator.Get<IProjectProvider>();
    private readonly IScriptConfigurationService _config =
        ScriptServiceLocator.Get<IScriptConfigurationService>();
    private readonly IMessageBoxService _msgBoxSvc = ScriptServiceLocator.Get<IMessageBoxService>();

    public override async Task<ExecutionResult> ExecuteAsync(
        string? methodName,
        ScriptArgs? args = null
    )
    {
        if (string.IsNullOrEmpty(methodName))
            return ExecutionResult.Failure;

        if (methodName == "config")
        {
            await SetConfiguration();
            return ExecutionResult.Success;
        }

        return Swap(
            methodName switch
            {
                "honorifics" => '*',
                "mahjong" => '/',
                "verbalTics" => '<',
            }
        )
            ? ExecutionResult.Success
            : NoDocumentLoaded;
    }

    /// <summary>
    /// Swap according to the flag
    /// </summary>
    /// <param name="flag">Flag to use for swapping</param>
    /// <returns><see langword="true"/> if successful</returns>
    private bool Swap(char flag)
    {
        var wsp = _prjProvider.Current.WorkingSpace;
        if (wsp is null)
            return false;
        var doc = wsp.Document;

        // Build style patterns
        var styles = Styles
            .Select(s => new Regex(
                "^" + Regex.Escape(s).Replace("\\*", ".*").Replace("\\?", "."),
                RegexOptions.IgnoreCase
            ))
            .ToList();

        List<Event> changedEvents = [];

        foreach (var @event in doc.EventManager.Events)
        {
            var changed = false;

            // Toggle comment status of ***-tagged events
            if (@event.Effect == "***")
            {
                @event.IsComment = !@event.IsComment;
                changedEvents.Add(@event);
                changed = true;
            }

            // For text changes, skip anything not in the approved styles list
            if (!styles.Any(rgx => rgx.IsMatch(@event.Style)))
                continue;

            // Convert "{*}foo{*bar}" to "{*}bar{*foo}"
            var regex = new Regex($@"\{{\{flag}\}}([^\{{]+)\{{\{flag}([^\}}]*)\}}");
            if (regex.IsMatch(@event.Text))
            {
                @event.Text = regex.Replace(@event.Text, $@"{{{flag}}}$2{{{flag}$1}}");
                changedEvents.Add(@event);
                if (!changed)
                {
                    changedEvents.Add(@event);
                    changed = true;
                }
            }

            // Convert "foo{**-bar}" to "foo{*}-bar{*}"
            regex = new Regex($@"\{{\{flag}\{flag}([^\}}]*)\}}");
            if (regex.IsMatch(@event.Text))
            {
                @event.Text = regex.Replace(@event.Text, $@"{{{flag}}}$1{{{flag}}}");
                if (!changed)
                {
                    changedEvents.Add(@event);
                    changed = true;
                }
            }

            // Convert "foo{*}{*-bar}" to "foo{**-bar}"
            regex = new Regex($@"\{{\{flag}\}}\{{\{flag}");
            if (regex.IsMatch(@event.Text))
            {
                @event.Text = regex.Replace(@event.Text, $@"{{{flag}{flag}");
                if (!changed)
                    changedEvents.Add(@event);
            }
        }

        wsp.Commit(changedEvents, ChangeType.ComplexEvent);
        return true;
    }

    private async Task SetConfiguration()
    {
        var content = string.Join(";", Styles.Select(s => s.Trim()));

        var result = await _msgBoxSvc.ShowInputAsync(
            Strings.ConfigWindowTitle,
            Strings.ConfigWindowText,
            content,
            MsgBoxButtonSet.OkCancel,
            MsgBoxButton.Ok
        );

        if (result is null)
            return;

        var (boxResult, userInput) = result.Value;

        if (boxResult == MsgBoxButton.Ok)
            Styles = userInput.Split(';');
    }

    /// <summary>
    /// Styles to use for swapping
    /// </summary>
    private string[] Styles
    {
        get
        {
            if (!_config.TryGet(this, "styles", out string? styles))
            {
                styles = string.Join(";", DefaultStyles);
                _config.Set(this, "styles", styles);
            }

            return styles!.Split(';');
        }
        set => _config.Set(this, "styles", string.Join(";", value.Select(s => s.Trim())));
    }

    /// <summary>
    /// Default styles. * and ? wildcards are permitted
    /// </summary>
    private static readonly string[] DefaultStyles =
    [
        "*Default*",
        "*Main*",
        "*Alt*",
        "*Top*",
        "*Italics*",
        "*Overlap*",
        "*Dialogue*",
        "*Flashback*",
        "*Thoughts*",
    ];

    private static readonly ExecutionResult NoDocumentLoaded = new()
    {
        Status = ExecutionStatus.Failure,
        Message = "No document loaded",
    };

    #region Localization

    /// <summary>
    /// Get the strings for the current language (falls back to en-US)
    /// </summary>
    private static ILocalization Strings => ScriptServiceLocator
        .Get<ICultureService>()
        .CurrentLanguage.Locale switch
    {
        "en-US" => new LocalizationEnUs(),
        "es-419" => new LocalizationEs419(),
        _ => new LocalizationEnUs(),
    };

    private interface ILocalization
    {
        string DisplayName { get; }
        string SwapHonorifics { get; }
        string SwapMahjongTerms { get; }
        string SwapVerbalTics { get; }
        string Configuration { get; }
        string ConfigWindowTitle { get; }
        string ConfigWindowText { get; }
    }

    private class LocalizationEnUs : ILocalization
    {
        public string DisplayName => "AutoSwapper";
        public string SwapHonorifics => "Swap Honorifics";
        public string SwapMahjongTerms => "Swap Mahjong Terms";
        public string SwapVerbalTics => "Swap Verbal Tics";
        public string Configuration => "Configuration";
        public string ConfigWindowTitle => "AutoSwapper Config";
        public string ConfigWindowText =>
            "List of styles to swap, separated by semicolons. Wildcards (*, ?) are permitted.";
    }

    private class LocalizationEs419 : ILocalization
    {
        public string DisplayName => "Intercambiador automático";
        public string SwapHonorifics => "Intercambiar honoríficos";
        public string SwapMahjongTerms => "Intercambiar términos de Mahjong";
        public string SwapVerbalTics => "Intercambiar tics verbales";
        public string Configuration => "Configuración";
        public string ConfigWindowTitle => "Configuración del intercambiador automático";
        public string ConfigWindowText =>
            "Lista de estilos para intercambiar, separados por punto y coma. Se permiten comodines (*, ?).";
    }

    #endregion
}
