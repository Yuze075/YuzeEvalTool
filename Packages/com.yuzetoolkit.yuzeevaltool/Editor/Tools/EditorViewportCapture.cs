#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace YuzeToolkit
{
    internal static class EditorViewportCapture
    {
        private const int MaxAllowedLongEdge = 8192;
        private const long MaxEditorWindowSourcePixels = 8_388_608L;
        private const long MaxOutputPixels = 16_777_216L;
        private const int MaxEncodedPngBytes = 32 * 1024 * 1024;
        private const BindingFlags InstanceMembers =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo? EditorWindowParentField =
            typeof(EditorWindow).GetField("m_Parent", BindingFlags.Instance | BindingFlags.NonPublic);

        public static Dictionary<string, object?> Capture(
            string target,
            int maxLongEdge,
            string windowQuery)
        {
            var normalizedTarget = (target ?? string.Empty).Trim().ToLowerInvariant();
            if (normalizedTarget != "game" && normalizedTarget != "scene" && normalizedTarget != "editor_window")
                throw new InvalidOperationException(
                    $"Invalid target '{target}'. Expected 'game', 'scene', or 'editor_window'.");
            if (maxLongEdge < 0 || maxLongEdge > MaxAllowedLongEdge)
                throw new InvalidOperationException(
                    $"Argument 'maxLongEdge' must be 0 or an integer from 1 to {MaxAllowedLongEdge}.");

            var window = ResolveWindow(normalizedTarget, windowQuery);
            EnsureWindowIsVisible(window);

            Texture2D? texture = null;
            try
            {
                var source = normalizedTarget switch
                {
                    "game" => "game_view_render_texture",
                    "scene" => "scene_view_render_texture",
                    _ => "visible_editor_window"
                };
                var capture = normalizedTarget switch
                {
                    "game" => CaptureRenderTexture(window, "m_RenderTexture", "Game View", maxLongEdge),
                    "scene" => CaptureRenderTexture(window, "m_SceneTargetTexture", "Scene View", maxLongEdge),
                    _ => CaptureVisibleEditorWindow(window, maxLongEdge)
                };

                texture = capture.Texture;
                var png = texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                    throw new InvalidOperationException("Unity encoded an empty PNG for the requested viewport.");
                if (png.Length > MaxEncodedPngBytes)
                {
                    throw new InvalidOperationException(
                        $"The encoded PNG is {png.Length} bytes, exceeding the hard limit of " +
                        $"{MaxEncodedPngBytes} bytes. Set a smaller 'maxLongEdge'.");
                }

                var windowType = window.GetType();
                return EvalData.Obj(
                    ("target", normalizedTarget),
                    ("source", source),
                    ("title", GetWindowTitle(window)),
                    ("windowType", windowType.FullName ?? windowType.Name),
                    ("sourceWidth", capture.SourceWidth),
                    ("sourceHeight", capture.SourceHeight),
                    ("width", texture.width),
                    ("height", texture.height),
                    ("maxLongEdge", maxLongEdge),
                    ("mimeType", "image/png"),
                    ("__image", EvalData.Obj(
                        ("base64", Convert.ToBase64String(png)),
                        ("mimeType", "image/png"))));
            }
            finally
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static EditorWindow ResolveWindow(string target, string windowQuery)
        {
            if (target == "game")
            {
                var type = typeof(EditorWindow).Assembly.GetType("UnityEditor.GameView");
                if (type == null)
                    throw new InvalidOperationException("This Unity version does not expose the Game View type.");
                return FindWindow(type) ?? throw new InvalidOperationException(
                    "No open Game View was found. Open a Game View before capturing it.");
            }

            if (target == "scene")
            {
                var sceneView = FindWindow(typeof(SceneView)) as SceneView ?? SceneView.lastActiveSceneView;
                return sceneView != null
                    ? sceneView
                    : throw new InvalidOperationException(
                        "No open Scene View was found. Open a Scene View before capturing it.");
            }

            return FindEditorWindow(windowQuery);
        }

        private static EditorWindow? FindWindow(Type windowType)
        {
            EditorWindow? first = null;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null || !windowType.IsInstanceOfType(window))
                    continue;
                first ??= window;
                if (IsWindowSelected(window))
                    return window;
            }
            return first;
        }

        private static EditorWindow FindEditorWindow(string windowQuery)
        {
            var query = (windowQuery ?? string.Empty).Trim();
            if (query.Length == 0)
            {
                return EditorWindow.focusedWindow ?? EditorWindow.mouseOverWindow
                    ?? throw new InvalidOperationException(
                        "Argument 'windowQuery' is required when no Editor window is focused or under the pointer.");
            }

            EditorWindow? exactMatch = null;
            EditorWindow? partialMatch = null;
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window == null)
                    continue;
                if (WindowMatches(window, query, exact: true))
                {
                    if (IsWindowSelected(window))
                        return window;
                    exactMatch ??= window;
                }
                else if (WindowMatches(window, query, exact: false))
                {
                    if (IsWindowSelected(window))
                        partialMatch = window;
                    else
                        partialMatch ??= window;
                }
            }
            return exactMatch ?? partialMatch ?? throw new InvalidOperationException(
                $"No open Editor window matched title or C# type '{query}'.");
        }

        private static bool WindowMatches(EditorWindow window, string query, bool exact)
        {
            var comparison = StringComparison.OrdinalIgnoreCase;
            var type = window.GetType();
            var values = new[] { GetWindowTitle(window), type.Name, type.FullName ?? string.Empty };
            foreach (var value in values)
            {
                if (exact ? string.Equals(value, query, comparison) : value.IndexOf(query, comparison) >= 0)
                    return true;
            }
            return false;
        }

        private static string GetWindowTitle(EditorWindow window)
        {
            var title = window.titleContent?.text;
            return !string.IsNullOrWhiteSpace(title) ? title! : window.GetType().Name;
        }

        private static void EnsureWindowIsVisible(EditorWindow window)
        {
            if (!IsWindowSelected(window))
            {
                throw new InvalidOperationException(
                    $"Editor window '{GetWindowTitle(window)}' exists but its tab is not visible. " +
                    "Select that tab before capturing it.");
            }

            if (!IsFiniteRect(window.position) || window.position.width <= 1f || window.position.height <= 1f)
                throw new InvalidOperationException($"Editor window '{GetWindowTitle(window)}' has no visible pixels.");
        }

        private static bool IsWindowSelected(EditorWindow window)
        {
            var host = GetHostView(window);
            if (host == null)
                return false;
            var actualView = FindProperty(host.GetType(), "actualView");
            return actualView != null && ReferenceEquals(actualView.GetValue(host), window);
        }

        private static object? GetHostView(EditorWindow window) => EditorWindowParentField?.GetValue(window);

        private static CapturedViewport CaptureRenderTexture(
            EditorWindow window,
            string textureFieldName,
            string displayName,
            int maxLongEdge)
        {
            var field = FindField(window.GetType(), textureFieldName);
            if (field == null || !typeof(RenderTexture).IsAssignableFrom(field.FieldType))
            {
                throw new InvalidOperationException(
                    $"This Unity version does not expose the {displayName} render texture.");
            }

            var source = field.GetValue(window) as RenderTexture;
            if (source == null || !source.IsCreated() || source.width <= 0 || source.height <= 0)
            {
                throw new InvalidOperationException(
                    $"The {displayName} has not rendered visible pixels yet. Repaint the view, then capture again.");
            }

            var sourceWidth = source.width;
            var sourceHeight = source.height;
            ValidateDimensions(sourceWidth, sourceHeight, $"{displayName} render-texture source");
            if (maxLongEdge == 0)
            {
                ValidatePixelCount(
                    sourceWidth,
                    sourceHeight,
                    MaxOutputPixels,
                    $"Unscaled {displayName} render-texture source");
            }

            GetOutputDimensions(sourceWidth, sourceHeight, maxLongEdge, out var outputWidth, out var outputHeight);
            ValidatePixelCount(outputWidth, outputHeight, MaxOutputPixels, "Capture output");

            RenderTexture? normalized = null;
            var previous = RenderTexture.active;
            try
            {
                normalized = RenderTexture.GetTemporary(
                    outputWidth,
                    outputHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default,
                    1);
                if (SystemInfo.graphicsUVStartsAtTop)
                    Graphics.Blit(source, normalized, new Vector2(1f, -1f), new Vector2(0f, 1f));
                else
                    Graphics.Blit(source, normalized);
                return new CapturedViewport(
                    ReadRenderTexture(normalized, outputWidth, outputHeight),
                    sourceWidth,
                    sourceHeight);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Failed to synchronously read the {displayName} render texture: {exception.Message}",
                    exception);
            }
            finally
            {
                RenderTexture.active = previous;
                if (normalized != null)
                    RenderTexture.ReleaseTemporary(normalized);
            }
        }

        private static CapturedViewport CaptureVisibleEditorWindow(EditorWindow window, int maxLongEdge)
        {
            if (!InternalEditorUtility.isApplicationActive)
            {
                throw new InvalidOperationException(
                    "Capturing an arbitrary Editor window requires Unity to be the foreground application on this platform.");
            }

            var host = GetHostView(window);
            if (host == null || !TryReadRect(host, "screenPosition", out var screenRect))
                throw new InvalidOperationException("Unity did not expose a visible screen rectangle for the Editor window.");

            var width = RoundScreenDimension(screenRect.width, "width");
            var height = RoundScreenDimension(screenRect.height, "height");
            if (width <= 1 || height <= 1)
                throw new InvalidOperationException("The Editor window has no visible pixels.");

            var sourcePixelCount = ValidatePixelCount(
                width,
                height,
                MaxEditorWindowSourcePixels,
                "Editor-window screen source");
            GetOutputDimensions(width, height, maxLongEdge, out var outputWidth, out var outputHeight);
            ValidatePixelCount(outputWidth, outputHeight, MaxOutputPixels, "Capture output");

            var pixels = InternalEditorUtility.ReadScreenPixel(screenRect.position, width, height);
            if (pixels == null || pixels.LongLength != sourcePixelCount)
            {
                throw new InvalidOperationException(
                    $"Unity returned no usable pixels for the Editor window (expected {sourcePixelCount}, got {pixels?.LongLength ?? 0}).");
            }

            var texture = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                if (outputWidth == width && outputHeight == height)
                    return new CapturedViewport(texture, width, height);

                var resized = ResizeTexture(texture, outputWidth, outputHeight);
                UnityEngine.Object.DestroyImmediate(texture);
                return new CapturedViewport(resized, width, height);
            }
            catch
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        private static Texture2D ResizeTexture(Texture2D source, int outputWidth, int outputHeight)
        {
            RenderTexture? resized = null;
            var previous = RenderTexture.active;
            try
            {
                resized = RenderTexture.GetTemporary(
                    outputWidth,
                    outputHeight,
                    0,
                    RenderTextureFormat.ARGB32,
                    RenderTextureReadWrite.Default,
                    1);
                Graphics.Blit(source, resized);
                return ReadRenderTexture(resized, outputWidth, outputHeight);
            }
            finally
            {
                RenderTexture.active = previous;
                if (resized != null)
                    RenderTexture.ReleaseTemporary(resized);
            }
        }

        private static Texture2D ReadRenderTexture(RenderTexture source, int width, int height)
        {
            var previous = RenderTexture.active;
            Texture2D? texture = null;
            try
            {
                RenderTexture.active = source;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0f, 0f, width, height), 0, 0, false);
                texture.Apply(false, false);
                return texture;
            }
            catch
            {
                if (texture != null)
                    UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
            finally
            {
                RenderTexture.active = previous;
            }
        }

        private static void GetOutputDimensions(
            int sourceWidth,
            int sourceHeight,
            int maxLongEdge,
            out int outputWidth,
            out int outputHeight)
        {
            ValidateDimensions(sourceWidth, sourceHeight, "Capture source");
            var longEdge = Math.Max(sourceWidth, sourceHeight);
            if (maxLongEdge == 0 || longEdge <= maxLongEdge)
            {
                outputWidth = sourceWidth;
                outputHeight = sourceHeight;
                return;
            }

            var scale = (double)maxLongEdge / longEdge;
            outputWidth = Math.Max(1, checked((int)Math.Round(sourceWidth * scale)));
            outputHeight = Math.Max(1, checked((int)Math.Round(sourceHeight * scale)));
        }

        private static int RoundScreenDimension(float value, string name)
        {
            if (!IsFinite(value) || value > int.MaxValue)
            {
                throw new InvalidOperationException(
                    $"The Editor window {name} '{value}' cannot be represented as a supported pixel dimension.");
            }
            return Mathf.RoundToInt(value);
        }

        private static void ValidateDimensions(int width, int height, string description)
        {
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"{description} has invalid dimensions {width}x{height}.");
        }

        private static long ValidatePixelCount(
            int width,
            int height,
            long maxPixelCount,
            string description)
        {
            ValidateDimensions(width, height, description);
            long pixelCount;
            try
            {
                pixelCount = checked((long)width * height);
            }
            catch (OverflowException exception)
            {
                throw new InvalidOperationException(
                    $"{description} dimensions {width}x{height} overflow the supported pixel count.",
                    exception);
            }

            if (pixelCount > maxPixelCount)
            {
                throw new InvalidOperationException(
                    $"{description} contains {pixelCount} pixels ({width}x{height}), exceeding the hard limit " +
                    $"of {maxPixelCount} pixels. Set a smaller 'maxLongEdge' when the capture path supports scaling.");
            }
            return pixelCount;
        }

        private static bool TryReadRect(object instance, string propertyName, out Rect rect)
        {
            rect = default;
            try
            {
                var property = FindProperty(instance.GetType(), propertyName);
                if (property?.PropertyType != typeof(Rect) || property.GetValue(instance) is not Rect value)
                    return false;
                rect = value;
                return IsFiniteRect(rect) && rect.width > 0f && rect.height > 0f;
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Failed to inspect Unity's '{propertyName}' window property: {exception.Message}",
                    exception);
            }
        }

        private static bool IsFiniteRect(Rect rect)
        {
            return IsFinite(rect.x) && IsFinite(rect.y) && IsFinite(rect.width) && IsFinite(rect.height);
        }

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        private static PropertyInfo? FindProperty(Type? type, string name)
        {
            while (type != null)
            {
                var property = type.GetProperty(name, InstanceMembers | BindingFlags.DeclaredOnly);
                if (property != null)
                    return property;
                type = type.BaseType;
            }
            return null;
        }

        private static FieldInfo? FindField(Type? type, string name)
        {
            while (type != null)
            {
                var field = type.GetField(name, InstanceMembers | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
                type = type.BaseType;
            }
            return null;
        }

        private readonly struct CapturedViewport
        {
            public CapturedViewport(Texture2D texture, int sourceWidth, int sourceHeight)
            {
                Texture = texture;
                SourceWidth = sourceWidth;
                SourceHeight = sourceHeight;
            }

            public Texture2D Texture { get; }
            public int SourceWidth { get; }
            public int SourceHeight { get; }
        }
    }
}
