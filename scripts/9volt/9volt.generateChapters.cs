// SPDX-License-Identifier: MPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Ameko.Services;
using AssCS.Utilities;
using Avalonia.Controls;
using Holo.Providers;
using Holo.Scripting;
using Holo.Scripting.Models;

/// <summary>
/// Script for generating chapters using Significance syntax
/// </summary>
public class GenerateChapters() : HoloScript(_info)
{
    private static readonly PackageInfo _info = new()
    {
        DisplayName = "Generate Chapters",
        QualifiedName = "9volt.generateChapters",
        Headless = true,
        Exports =
        [
            new MethodInfo
            {
                DisplayName = "Generate Chapters",
                QualifiedName = "generate",
                Submenu = "Generate Chapters",
            },
            new MethodInfo
            {
                DisplayName = "Configuration",
                QualifiedName = "config",
                Submenu = "Generate Chapters",
            },
        ],
    };

    private readonly IProjectProvider _projectProvider =
        ScriptServiceLocator.Get<IProjectProvider>();
    private readonly IScriptConfigurationService _config =
        ScriptServiceLocator.Get<IScriptConfigurationService>();
    private readonly IWindowService _windowService = ScriptServiceLocator.Get<IWindowService>();

    public override async Task<ExecutionResult> ExecuteAsync(
        string? methodName,
        ScriptArgs? args = null
    )
    {
        if (string.IsNullOrEmpty(methodName))
            return ExecutionResult.Failure;

        return methodName switch
        {
            "generate" => await Generate(),
            "config" => await Config(),
            _ => new ExecutionResult { Status = ExecutionStatus.Failure },
        };
    }

    /// <summary>
    /// Generate XML chapters from Significance syntax
    /// </summary>
    /// <remarks>
    /// Chapter file will be placed next to the subtitle file
    /// </remarks>
    private async Task<ExecutionResult> Generate()
    {
        // Get config
        ReadConfig(out var idField, out var nameField, out var marker, out var generateIntro);

        // Get all the events containing chapters
        var wsp = _projectProvider.Current.WorkingSpace;
        var doc = wsp?.Document;
        if (doc is null)
            return NoDocumentLoaded;
        if (_projectProvider.Current.WorkingSpace?.SavePath is null)
            return DocumentNotSaved;

        var chapterEvents = doc
            .EventManager.Events.Where(@event =>
                idField switch
                {
                    "effect" => @event.Effect,
                    "text" => @event.Text,
                    _ => @event.Actor,
                } == marker
            )
            .ToList();

        // Build the chapters
        List<Chapter> chapters = [];
        if (generateIntro && chapterEvents.All(@event => @event.Start.TotalMilliseconds != 0))
        {
            chapters.Add(new Chapter { Name = "Intro", Start = "0:00:00.000" });
        }

        chapters.AddRange(
            chapterEvents.Select(@event => new Chapter
            {
                Name = nameField switch
                {
                    "actor" => @event.Actor,
                    "effect" => @event.Effect,
                    _ => @event.Text.Replace("{", string.Empty).Replace("}", string.Empty), // TODO: Do this properly
                },
                Start =
                    $"{@event.Start.Hours:D2}:{@event.Start.Minutes:D2}:{@event.Start.Seconds:D2}.{@event.Start.Milliseconds:D3}",
            })
        );

        // Build the XML structure
        var mkvChapters = new Chapters
        {
            EditionEntry = new EditionEntry
            {
                ChapterAtoms = chapters
                    .Select(c => new ChapterAtom
                    {
                        ChapterDisplay = new ChapterDisplay { ChapterString = c.Name },
                        ChapterTimeStart = c.Start,
                    })
                    .ToList(),
            },
        };

        // Write the file
        var fileName = Path.ChangeExtension(wsp!.SavePath!.LocalPath, ".chapters.xml");

        try
        {
            var xmlSerializer = new XmlSerializer(typeof(Chapters));
            await using var writer = new StreamWriter(
                fileName,
                Encoding.UTF8,
                new FileStreamOptions
                {
                    Access = FileAccess.Write,
                    Mode = FileMode.Create,
                    Share = FileShare.None,
                }
            );
            await using var xmlWriter = XmlWriter.Create(
                writer,
                new XmlWriterSettings
                {
                    Indent = true,
                    Encoding = Encoding.UTF8,
                    Async = true,
                }
            );
            await xmlWriter.WriteDocTypeAsync("Chapters", null, "matroskachapters.dtd", null);
            xmlSerializer.Serialize(
                xmlWriter,
                mkvChapters,
                new XmlSerializerNamespaces([XmlQualifiedName.Empty])
            );
        }
        catch (Exception e)
        {
            return new ExecutionResult { Status = ExecutionStatus.Failure, Message = e.ToString() };
        }

        return ExecutionResult.Success;
    }

    /// <summary>
    /// Show a configuration window
    /// </summary>
    private async Task<ExecutionResult> Config()
    {
        ReadConfig(out var idField, out var nameField, out var marker, out var generateIntro);

        var win = new Window { Title = "Generate Chapters Configuration" };

        var idFieldLabel = new Label { Content = "Field containing chapter markers" };
        var idActorRb = new RadioButton
        {
            Content = "actor",
            GroupName = "idField",
            IsChecked = idField == "actor",
        };
        var idEffectRb = new RadioButton
        {
            Content = "effect",
            GroupName = "idField",
            IsChecked = idField == "effect",
        };

        var nameFieldLabel = new Label { Content = "Field containing chapter name" };
        var nameActorRb = new RadioButton
        {
            Content = "actor",
            GroupName = "nameField",
            IsChecked = nameField == "actor",
        };
        var nameEffectRb = new RadioButton
        {
            Content = "effect",
            GroupName = "nameField",
            IsChecked = nameField == "effect",
        };
        var nameTextRb = new RadioButton
        {
            Content = "text",
            GroupName = "nameField",
            IsChecked = nameField == "text",
        };

        var markerLabel = new Label { Content = "Marker" };
        var markerBox = new TextBox { Text = marker };

        var generateIntroBox = new CheckBox
        {
            Content = "Generate intro chapter if no chapter is at t=0",
            IsChecked = generateIntro,
        };

        var saveButton = new Button { Content = "Save" };

        saveButton.Click += (_, _) =>
        {
            _config.Set(this, "idField", idActorRb.IsChecked ?? true ? "actor" : "effect");
            _config.Set(
                this,
                "nameField",
                nameTextRb.IsChecked ?? true ? "text"
                    : nameEffectRb.IsChecked ?? true ? "effect"
                    : "actor"
            );
            _config.Set(this, "marker", markerBox.Text);
            _config.Set(this, "generateIntro", generateIntroBox.IsChecked ?? true);
            win.Close(); // Close the window
        };

        var panel = new StackPanel();
        panel.Children.AddRange(
            [
                idFieldLabel,
                idActorRb,
                idEffectRb,
                nameFieldLabel,
                nameActorRb,
                nameEffectRb,
                nameTextRb,
                markerLabel,
                markerBox,
                generateIntroBox,
                saveButton,
            ]
        );

        win.Content = panel;
        await _windowService.ShowDialogAsync(win);

        return ExecutionResult.Success;
    }

    /// <summary>
    /// Get script configuration
    /// </summary>
    /// <param name="idField">Name of the field identifying the event as a chapter</param>
    /// <param name="nameField">Name of the field containing the chapter name</param>
    /// <param name="marker">String for the <paramref name="idField"/></param>
    /// <param name="generateIntro">Generate an Intro chapter if there isn't a chapter at t=0</param>
    private void ReadConfig(
        out string? idField,
        out string? nameField,
        out string? marker,
        out bool generateIntro
    )
    {
        if (!_config.TryGet(this, "idField", out idField))
        {
            idField = "actor";
            _config.Set(this, "idField", idField);
        }

        if (!_config.TryGet(this, "nameField", out nameField))
        {
            nameField = "text";
            _config.Set(this, "nameField", nameField);
        }

        if (!_config.TryGet(this, "marker", out marker))
        {
            marker = "chapter";
            _config.Set(this, "marker", marker);
        }

        if (!_config.TryGet(this, "generateIntro", out generateIntro))
        {
            generateIntro = true;
            _config.Set(this, "generateIntro", generateIntro);
        }
    }

    private static readonly ExecutionResult NoDocumentLoaded = new()
    {
        Status = ExecutionStatus.Failure,
        Message = "No document loaded",
    };
    private static readonly ExecutionResult DocumentNotSaved = new()
    {
        Status = ExecutionStatus.Failure,
        Message = "Document has not been saved to disk",
    };

    private class Chapter
    {
        public required string Name { get; init; }
        public required string Start { get; init; }
    }

    //
    // XML Serialization classes
    //

    [XmlRoot("Chapters")]
    public class Chapters
    {
        [XmlElement("EditionEntry")]
        public required EditionEntry EditionEntry { get; set; }
    }

    public class EditionEntry
    {
        [XmlElement("ChapterAtom")]
        public required List<ChapterAtom> ChapterAtoms { get; set; }
    }

    public class ChapterAtom
    {
        [XmlElement("ChapterTimeStart")]
        public required string ChapterTimeStart { get; set; }

        [XmlElement("ChapterDisplay")]
        public required ChapterDisplay ChapterDisplay { get; set; }
    }

    public class ChapterDisplay
    {
        [XmlElement("ChapterString")]
        public required string ChapterString { get; set; }

        [XmlElement("ChapterLanguage")]
        public string ChapterLanguage { get; set; } = "eng";
    }
}
