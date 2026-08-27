#nullable enable
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace YuzeToolkit
{
    internal static class UnityEvalToolAuthorizationSettingsProvider
    {
        private const string SettingsPath = "Project/YuzeToolkit/Yuze Eval Tool";

        [SettingsProvider]
        public static SettingsProvider CreateProvider()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "Yuze Eval Tool",
                keywords = new HashSet<string>(new[] { "Yuze Eval Tool", "UnityEvalTool", "Broker", "MCP", "CLI", "Token", "Player" }),
                activateHandler = (_, root) => Build(root)
            };
        }

        private static void Build(VisualElement root)
        {
            root.Clear();
            root.style.paddingLeft = 18;
            root.style.paddingRight = 18;
            root.style.paddingTop = 14;
            root.style.maxWidth = 780;

            var title = new Label("Yuze Eval Tool Authorization");
            title.style.fontSize = 19;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 6;
            root.Add(title);
            root.Add(new HelpBox(
                "Token verification is disabled by default. When enabled, the Resources asset stores only a salted verifier and is included in Player builds; the original token belongs in the computer-level Broker auth.json.",
                HelpBoxMessageType.Info));

            var settings = LoadSettings();
            var requireToken = new Toggle("Require token for Broker commands")
            {
                value = settings != null && settings.RequireToken
            };
            requireToken.style.marginTop = 12;
            root.Add(requireToken);

            var status = new Label(settings == null || !settings.HasVerifier
                ? "Verifier: not configured"
                : $"Verifier: configured ({settings.Algorithm})");
            status.style.marginTop = 6;
            status.style.marginBottom = 10;
            root.Add(status);

            var tokenName = new TextField("Token name") { value = string.Empty };
            tokenName.tooltip = "Optional readable prefix. Allowed characters: A-Z, a-z, 0-9, underscore, hyphen.";
            root.Add(tokenName);

            var token = new TextField("Token") { isPasswordField = true };
            token.tooltip = "The original token is used only to create the verifier and is never serialized into the Unity project.";
            root.Add(token);

            var reveal = new Toggle("Show token");
            reveal.RegisterValueChangedCallback(evt => token.isPasswordField = !evt.newValue);
            root.Add(reveal);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 10;
            actions.style.marginBottom = 10;
            var generate = new Button(() =>
            {
                try
                {
                    token.value = UnityEvalToolAuthorizationSettings.GenerateToken(tokenName.value);
                    token.isPasswordField = false;
                    reveal.SetValueWithoutNotify(true);
                    GUIUtility.systemCopyBuffer = token.value;
                    status.text = "Generated token copied to the clipboard. Apply it to create the project verifier.";
                }
                catch (ArgumentException ex)
                {
                    status.text = ex.Message;
                }
            }) { text = "Generate and Copy" };
            actions.Add(generate);

            var apply = new Button(() =>
            {
                try
                {
                    var asset = EnsureSettings();
                    Undo.RecordObject(asset, "Configure Yuze Eval Tool token");
                    asset.ConfigureToken(token.value);
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                    requireToken.SetValueWithoutNotify(true);
                    status.text = $"Verifier: configured ({asset.Algorithm}); original token was not saved in Unity.";
                    token.value = string.Empty;
                    reveal.SetValueWithoutNotify(false);
                    token.isPasswordField = true;
                    EditorBrokerBootstrap.Reconnect();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
                {
                    status.text = ex.Message;
                }
            }) { text = "Apply Token" };
            apply.style.marginLeft = 8;
            actions.Add(apply);

            var clear = new Button(() =>
            {
                var asset = LoadSettings();
                if (asset == null) return;
                Undo.RecordObject(asset, "Clear Yuze Eval Tool token");
                asset.ClearToken();
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
                requireToken.SetValueWithoutNotify(false);
                status.text = "Verifier: not configured";
                EditorBrokerBootstrap.Reconnect();
            }) { text = "Clear" };
            clear.style.marginLeft = 8;
            actions.Add(clear);
            root.Add(actions);

            root.Add(new HelpBox(
                "Recommended token format: readable-name_random-43-character-suffix. MCP may supply multiple tokens separated by '/'; CLI uses --token. Reconnecting is required after changing this project setting.",
                HelpBoxMessageType.Info));
            var path = new Label("Player configuration asset: " + UnityEvalToolAuthorizationSettings.AssetPath);
            path.style.marginTop = 8;
            root.Add(path);

            requireToken.RegisterValueChangedCallback(evt =>
            {
                try
                {
                    var asset = evt.newValue ? EnsureSettings() : LoadSettings();
                    if (asset == null)
                    {
                        requireToken.SetValueWithoutNotify(false);
                        return;
                    }
                    Undo.RecordObject(asset, "Change Yuze Eval Tool token requirement");
                    asset.SetRequireToken(evt.newValue);
                    EditorUtility.SetDirty(asset);
                    AssetDatabase.SaveAssets();
                    status.text = evt.newValue
                        ? $"Verifier: configured ({asset.Algorithm})"
                        : asset.HasVerifier ? $"Verifier: configured but disabled ({asset.Algorithm})" : "Verifier: not configured";
                    EditorBrokerBootstrap.Reconnect();
                }
                catch (InvalidOperationException ex)
                {
                    requireToken.SetValueWithoutNotify(false);
                    status.text = ex.Message;
                }
            });
        }

        private static UnityEvalToolAuthorizationSettings? LoadSettings() =>
            AssetDatabase.LoadAssetAtPath<UnityEvalToolAuthorizationSettings>(
                UnityEvalToolAuthorizationSettings.AssetPath);

        private static UnityEvalToolAuthorizationSettings EnsureSettings()
        {
            var existing = LoadSettings();
            if (existing != null) return existing;
            if (!AssetDatabase.IsValidFolder("Assets/Resources"))
                AssetDatabase.CreateFolder("Assets", "Resources");
            var created = ScriptableObject.CreateInstance<UnityEvalToolAuthorizationSettings>();
            AssetDatabase.CreateAsset(created, UnityEvalToolAuthorizationSettings.AssetPath);
            AssetDatabase.SaveAssets();
            return created;
        }
    }
}
