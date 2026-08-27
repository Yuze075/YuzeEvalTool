#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine.UIElements;

namespace YuzeToolkit.UnityAgent
{
    internal static class UnityAgentProjectSettingsAsset
    {
        internal const string AssetPath = "Assets/Resources/UnityAgentProjectSettings.json";

        internal static event Action? Changed;

        internal static bool Exists => File.Exists(AbsoluteAssetPath);

        internal static string ReadSerializedSettings()
        {
            if (Exists) return File.ReadAllText(AbsoluteAssetPath, Encoding.UTF8);
            return UnityAgentProjectSettings.Serialize(UnityAgentProjectSettings.LoadPackageDefaults());
        }

        internal static AgentProjectSettingsDocument Load()
        {
            var packageDefaults = UnityAgentProjectSettings.LoadPackageDefaults();
            return UnityAgentProjectSettings.Deserialize(ReadSerializedSettings(), packageDefaults);
        }

        internal static void Save(AgentProjectSettingsDocument settings)
        {
            var json = UnityAgentProjectSettings.Serialize(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(AbsoluteAssetPath) ??
                                      throw new InvalidOperationException(
                                          "Project Settings asset path has no parent directory."));
            File.WriteAllText(AbsoluteAssetPath, json + "\n", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);
            Changed?.Invoke();
        }

        internal static void Save(AgentSettingsDocument settings) =>
            Save(AgentProjectSettingsDocument.FromSettings(settings));

        private static string AbsoluteAssetPath =>
            Path.GetFullPath(Path.Combine(AgentPaths.ProjectRoot, AssetPath));
    }

    /// <summary>UI Toolkit Project Settings surface for versioned, provider-free Agent defaults.</summary>
    internal sealed class UnityAgentProjectSettingsProvider : SettingsProvider
    {
        internal const string SettingsPath = "Project/YuzeToolkit/Yuze Agent Tool";

        private VisualElement? _root;
        private AgentProjectSettingsDocument? _editing;
        private AgentChoiceField? _permission;
        private AgentIntegerField? _toolTimeout;
        private AgentIntegerField? _maximumAgentSteps;
        private AgentTextField? _editorSystemPrompt;
        private AgentTextField? _runtimeSystemPrompt;
        private AgentPathListEditor? _agentsRoots;
        private AgentPathListEditor? _skillRoots;
        private AgentButton? _saveButton;
        private AgentButton? _revertButton;
        private Label? _messageLabel;
        private bool _dirty;
        private NoticeKind _messageKind = NoticeKind.Info;
        private string _message = string.Empty;

        private UnityAgentProjectSettingsProvider()
            : base(SettingsPath, SettingsScope.Project)
        {
            label = "Yuze Agent Tool";
            keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Yuze Agent Tool", "Agent", "Prompt", "Runtime", "Editor", "AGENTS.md", "Skills",
                "Tool timeout", "Maximum steps"
            };
        }

        [SettingsProvider]
        public static SettingsProvider CreateProvider() => new UnityAgentProjectSettingsProvider();

        internal static void Open() => SettingsService.OpenProjectSettings(SettingsPath);

        internal static void OverwriteFromMachineSettings(AgentSettingsDocument settings) =>
            UnityAgentProjectSettingsAsset.Save(settings);

        public override void OnActivate(string searchContext, VisualElement rootElement)
        {
            UnityAgentProjectSettingsAsset.Changed -= OnProjectSettingsChanged;
            UnityAgentProjectSettingsAsset.Changed += OnProjectSettingsChanged;
            _root = rootElement;
            Reload();
        }

        public override void OnDeactivate()
        {
            UnityAgentProjectSettingsAsset.Changed -= OnProjectSettingsChanged;
            _root = null;
        }

        private void OnProjectSettingsChanged() => Reload();

        private void Reload()
        {
            try
            {
                _editing = UnityAgentProjectSettingsAsset.Load();
                _dirty = false;
                _message = UnityAgentProjectSettingsAsset.Exists
                    ? "Loaded project defaults from " + UnityAgentProjectSettingsAsset.AssetPath + "."
                    : "The project asset does not exist yet. Package defaults are shown; save to create it.";
                _messageKind = NoticeKind.Info;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               FormatException or ArgumentException or InvalidOperationException or
                                               OverflowException)
            {
                _editing = null;
                _dirty = false;
                _message = "Project defaults could not be loaded: " + exception.Message;
                _messageKind = NoticeKind.Error;
            }
            Rebuild();
        }

        private void Rebuild()
        {
            if (_root == null) return;
            _root.Clear();
            _root.style.flexGrow = 1;
            _root.style.minWidth = 0;
            _root.style.minHeight = 0;
            _root.style.backgroundColor = AgentUi.Background;
            AgentUi.ApplyRoot(_root);
            AgentTooltip.UseAsRoot(_root);

            if (_editing == null)
            {
                var failure = ProjectCard("Project defaults unavailable",
                    "The settings asset could not be read. Correct the reported data or file-system error, then retry.");
                failure.style.marginTop = 18;
                failure.Add(CreateWrappedLabel(_message, AgentUi.Error));
                failure.Add(AgentUi.Button("Retry loading", "Read the project defaults asset again.", Reload, 140,
                    AgentUi.Surface3, AgentUi.TextSecondary, AgentIconKind.Refresh));
                _root.Add(failure);
                return;
            }

            var scroll = AgentUi.Scroll(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.width = new Length(100, LengthUnit.Percent);
            scroll.style.minWidth = 0;
            scroll.style.minHeight = 0;
            scroll.contentContainer.style.width = new Length(100, LengthUnit.Percent);
            scroll.contentContainer.style.minWidth = 0;
            scroll.contentContainer.style.alignItems = Align.Stretch;
            scroll.contentContainer.style.paddingLeft = 24;
            scroll.contentContainer.style.paddingRight = 24;
            scroll.contentContainer.style.paddingTop = 20;
            scroll.contentContainer.style.paddingBottom = 24;
            _root.Add(scroll);

            scroll.Add(AgentUi.PageHeading("Yuze Agent Tool project defaults",
                "Versioned, provider-free defaults used only when this Editor or Player has no machine settings."));

            scroll.Add(ProjectCard("Configuration boundary",
                "These defaults are included in Player builds. Provider profiles, models, endpoints, and API keys " +
                "remain machine-local in the Yuze Agent Tool window."));

            BuildAgentDefaults(scroll, _editing);
            BuildPromptDefaults(scroll, _editing);
            BuildRoots(scroll, _editing);
            BuildFooter(scroll);
            RefreshFooter();
        }

        private static VisualElement ProjectCard(string title, string subtitle)
        {
            var card = AgentUi.Card(title, subtitle);
            card.style.maxWidth = StyleKeyword.None;
            card.style.alignSelf = Align.Stretch;
            return card;
        }

        private void BuildAgentDefaults(VisualElement parent, AgentProjectSettingsDocument settings)
        {
            var card = ProjectCard("Agent defaults", "Applied when a new conversation is created.");
            parent.Add(card);

            _permission = AgentUi.Dropdown("Permission mode", Enum.GetNames(typeof(AgentPermissionMode)));
            _permission.SetValueWithoutNotify(settings.PermissionMode.ToString());
            _permission.RegisterValueChangedCallback(evt =>
            {
                if (Enum.TryParse<AgentPermissionMode>(evt.newValue, out var value)) settings.PermissionMode = value;
                MarkDirty();
            });
            card.Add(_permission);

            _toolTimeout = new AgentIntegerField("Tool timeout (seconds)");
            _toolTimeout.SetValueWithoutNotify(settings.DefaultToolTimeoutSeconds);
            AgentTooltip.Attach(_toolTimeout, "Used when a Tool call does not provide its own timeout.");
            _toolTimeout.RegisterValueChangedCallback(evt =>
            {
                settings.DefaultToolTimeoutSeconds = evt.newValue;
                MarkDirty();
            });
            card.Add(_toolTimeout);

            _maximumAgentSteps = new AgentIntegerField("Maximum Agent steps");
            _maximumAgentSteps.SetValueWithoutNotify(settings.MaximumAgentSteps);
            AgentTooltip.Attach(_maximumAgentSteps, "Finite model-step limit for one Agent turn.");
            _maximumAgentSteps.RegisterValueChangedCallback(evt =>
            {
                settings.MaximumAgentSteps = evt.newValue;
                MarkDirty();
            });
            card.Add(_maximumAgentSteps);
        }

        private void BuildPromptDefaults(VisualElement parent, AgentProjectSettingsDocument settings)
        {
            var card = ProjectCard("System prompts",
                "Editor Prompt is used in Edit Mode and Editor Play Mode. Runtime Prompt is used only by a standalone Player.");
            parent.Add(card);

            _editorSystemPrompt = CreatePromptField("Editor Prompt", settings.EditorSystemPrompt);
            _editorSystemPrompt.RegisterValueChangedCallback(evt =>
            {
                settings.EditorSystemPrompt = evt.newValue;
                MarkDirty();
            });
            card.Add(_editorSystemPrompt);

            _runtimeSystemPrompt = CreatePromptField("Runtime Prompt", settings.RuntimeSystemPrompt);
            _runtimeSystemPrompt.RegisterValueChangedCallback(evt =>
            {
                settings.RuntimeSystemPrompt = evt.newValue;
                MarkDirty();
            });
            card.Add(_runtimeSystemPrompt);
        }

        private void BuildRoots(VisualElement parent, AgentProjectSettingsDocument settings)
        {
            var agentsCard = ProjectCard("AGENTS.md discovery roots",
                "Ordered highest priority first. Availability controls direct Editor/Player discovery; " +
                "embedding independently copies a build-time snapshot into Player.");
            _agentsRoots = new AgentPathListEditor("AGENTS.md roots", "Add AGENTS.md root", false, ShowPathError);
            _agentsRoots.SetItems(settings.AgentsRoots);
            _agentsRoots.Changed += MarkDirty;
            agentsCard.Add(_agentsRoots);
            parent.Add(agentsCard);

            var skillsCard = ProjectCard("Skill discovery roots",
                $"Each root may insert {AgentPaths.SettingsDirectoryName}, then always adds " +
                $"{AgentPaths.SkillDirectoryName}. An optional relative path selects a child directory; " +
                "ordering controls discovery priority.");
            _skillRoots = new AgentPathListEditor("Skill roots", "Add Skill root", true, ShowPathError);
            _skillRoots.SetItems(settings.SkillRoots);
            _skillRoots.Changed += MarkDirty;
            skillsCard.Add(_skillRoots);
            parent.Add(skillsCard);
        }

        private void BuildFooter(VisualElement parent)
        {
            var footer = ProjectCard("Save project defaults",
                "Saving changes only the versioned project asset. Existing machine settings remain unchanged.");
            _messageLabel = CreateWrappedLabel(_message, AgentUi.TextSecondary);
            footer.Add(_messageLabel);

            var actions = AgentUi.WrapRow();
            actions.style.marginTop = 10;
            _saveButton = AgentUi.Button("Save Project Defaults", "Validate and write the project settings asset.",
                Save, 190, AgentUi.Accent, AgentUi.AccentForeground);
            _revertButton = AgentUi.Button("Revert Unsaved Changes", "Reload the current project settings asset.",
                Reload, 190);
            actions.Add(_saveButton);
            actions.Add(_revertButton);
            actions.Add(AgentUi.Button("Restore Package Defaults",
                "Stage the package defaults without saving them yet.", RestoreDefaults, 190, AgentUi.Surface3,
                AgentUi.TextSecondary, AgentIconKind.Refresh));
            footer.Add(actions);
            parent.Add(footer);
        }

        private static AgentTextField CreatePromptField(string label, string value)
        {
            var field = AgentUi.Field(label, value, string.Empty);
            field.multiline = true;
            field.style.minHeight = 150;
            field.style.whiteSpace = WhiteSpace.Normal;
            return field;
        }

        private static Label CreateWrappedLabel(string text, UnityEngine.Color color)
        {
            var label = new Label(text);
            AgentUi.ApplyTypography(label, AgentTypography.Caption, false);
            label.style.color = color;
            label.style.whiteSpace = WhiteSpace.Normal;
            return label;
        }

        private void MarkDirty()
        {
            _dirty = true;
            RefreshFooter();
        }

        private void RefreshFooter()
        {
            _saveButton?.SetEnabled(_dirty);
            _revertButton?.SetEnabled(_dirty);
            if (_messageLabel == null) return;
            _messageLabel.text = _message;
            _messageLabel.style.color = _messageKind switch
            {
                NoticeKind.Warning => AgentUi.Warning,
                NoticeKind.Error => AgentUi.Error,
                _ => AgentUi.TextSecondary
            };
        }

        private void RestoreDefaults()
        {
            _editing = UnityAgentProjectSettings.LoadPackageDefaults();
            _dirty = true;
            _message = "Package defaults are staged. Save Project Defaults to write them.";
            _messageKind = NoticeKind.Warning;
            Rebuild();
        }

        private void ShowPathError(string message)
        {
            _message = "Path root is invalid: " + message;
            _messageKind = NoticeKind.Error;
            RefreshFooter();
        }

        private void Save()
        {
            if (_editing == null) return;
            try
            {
                CollectCurrentValues(_editing);
                UnityAgentProjectSettingsAsset.Save(_editing);
                _editing = UnityAgentProjectSettingsAsset.Load();
                _dirty = false;
                _message = "Project defaults saved. Existing Editor and Player machine settings are unchanged.";
                _messageKind = NoticeKind.Info;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               FormatException or ArgumentException or InvalidOperationException or
                                               OverflowException)
            {
                _message = "Project defaults were not saved: " + exception.Message;
                _messageKind = NoticeKind.Error;
            }
            RefreshFooter();
        }

        private void CollectCurrentValues(AgentProjectSettingsDocument settings)
        {
            if (_permission != null && Enum.TryParse<AgentPermissionMode>(_permission.value, out var permission))
                settings.PermissionMode = permission;
            if (_toolTimeout != null) settings.DefaultToolTimeoutSeconds = _toolTimeout.value;
            if (_maximumAgentSteps != null) settings.MaximumAgentSteps = _maximumAgentSteps.value;
            if (_editorSystemPrompt != null) settings.EditorSystemPrompt = _editorSystemPrompt.value;
            if (_runtimeSystemPrompt != null) settings.RuntimeSystemPrompt = _runtimeSystemPrompt.value;
            if (_agentsRoots != null) settings.AgentsRoots = _agentsRoots.GetItems();
            if (_skillRoots != null) settings.SkillRoots = _skillRoots.GetItems();
        }

        private enum NoticeKind
        {
            Info,
            Warning,
            Error
        }
    }
}
