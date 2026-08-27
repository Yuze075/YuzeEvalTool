#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YuzeToolkit.UnityAgent
{
    public interface IAgentStore
    {
        Task<IReadOnlyList<AgentSessionDocument>> LoadSessionsAsync(CancellationToken cancellationToken);

        Task SaveSessionAsync(AgentSessionDocument session, CancellationToken cancellationToken);

        Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken);

        Task<AgentSettingsDocument> LoadSettingsAsync(CancellationToken cancellationToken);

        Task SaveSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken);
    }

    public sealed class FileAgentStore : IAgentStore, IDisposable
    {
        private const string LegacyMigrationMarkerName = ".legacy-store-migrated-v2";
        private const int MaximumDocumentCharacters = 64_000_000;
        private readonly string _settingsRootPath;
        private readonly bool _usesDefaultSettingsRoot;
        private readonly AgentProjectSettingsDocument? _projectDefaults;
        private readonly Exception? _projectDefaultsError;
        private readonly SemaphoreSlim _ioGate = new(1, 1);
        private readonly string _sessionsPath;
        private bool _settingsLoaded;

        public FileAgentStore(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath)) throw new ArgumentException("Storage root is required.", nameof(rootPath));
            _settingsRootPath = Path.GetFullPath(rootPath);
            _usesDefaultSettingsRoot = AgentPaths.PathsEqual(_settingsRootPath, GetDefaultRootPath());
            try
            {
                _projectDefaults = UnityAgentProjectSettings.Load();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               FormatException or ArgumentException or InvalidOperationException or
                                               OverflowException)
            {
                _projectDefaultsError = exception;
            }
            _sessionsPath = Path.Combine(_settingsRootPath, AgentPaths.AgentConversationsFolderName);
        }

        /// <summary>The fixed directory containing settings.json and providers.json.</summary>
        public string RootPath => _settingsRootPath;

        /// <summary>The fixed path containing user-owned Provider profiles.</summary>
        public string ProviderSettingsPath => Path.Combine(_settingsRootPath, AgentPaths.ProviderSettingsFileName);

        /// <summary>The fixed directory containing Agent conversation documents.</summary>
        public string HistoryRootPath => _sessionsPath;

        public static string GetDefaultRootPath() => AgentPaths.SettingsRoot;

        public async Task<IReadOnlyList<AgentSessionDocument>> LoadSessionsAsync(
            CancellationToken cancellationToken)
        {
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Directory.Exists(_sessionsPath)) return (IReadOnlyList<AgentSessionDocument>)Array.Empty<AgentSessionDocument>();
                    var sessions = new List<AgentSessionDocument>();
                    var sessionsById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    var rewrites = new List<(string Path, AgentSessionDocument Session)>();
                    var paths = Directory.EnumerateFiles(_sessionsPath, "*", SearchOption.TopDirectoryOnly)
                        .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(path => path, StringComparer.Ordinal);
                    foreach (var path in paths)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var requiresUpgrade = StoredSessionRequiresUpgrade(path, cancellationToken);
                        var session = ReadDocument(path, AgentDocumentCodec.DeserializeSession, cancellationToken);
                        ValidateLoadedSessionIdentity(path, session, sessionsById);
                        if (requiresUpgrade)
                            rewrites.Add((path, session));
                        sessions.Add(session);
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Validate the complete set before upgrading or restoring any document. A malformed
                    // later file must not leave the history directory partially rewritten.
                    foreach (var rewrite in rewrites)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        WriteAtomic(rewrite.Path, AgentDocumentCodec.SerializeSession(rewrite.Session),
                            cancellationToken);
                    }

                    return sessions.OrderByDescending(session => session.UpdatedAtUtc).ToList();
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveSessionAsync(AgentSessionDocument session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            var fileName = ValidateId(session.Id) + ".json";
            var json = AgentDocumentCodec.SerializeSession(session);
            await WriteAsync(Path.Combine(_sessionsPath, fileName), json, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken)
        {
            var path = Path.Combine(_sessionsPath, ValidateId(sessionId) + ".json");
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(path)) File.Delete(path);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task<AgentSettingsDocument> LoadSettingsAsync(CancellationToken cancellationToken)
        {
            var path = Path.Combine(_settingsRootPath, AgentPaths.SettingsFileName);
            var providerPath = ProviderSettingsPath;
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Directory.CreateDirectory(_settingsRootPath);
                    var legacyRoot = _usesDefaultSettingsRoot ? GetLegacyRootPath() : string.Empty;
                    var legacySettingsPath = string.IsNullOrEmpty(legacyRoot)
                        ? string.Empty
                        : Path.Combine(legacyRoot, AgentPaths.SettingsFileName);
                    var settings = LoadMachineSettings(path, legacySettingsPath, cancellationToken,
                        out var machineSettingsNeedsWrite);
                    var providerSettings = LoadProviderSettings(providerPath, cancellationToken,
                        out var providerNeedsWrite);
                    providerSettings.ApplyTo(settings);
                    UnityAgentHost.ValidateSettings(settings);

                    if (machineSettingsNeedsWrite)
                        WriteAtomic(path, AgentDocumentCodec.SerializeMachineSettings(settings), cancellationToken);
                    if (providerNeedsWrite)
                        WriteAtomic(providerPath, AgentDocumentCodec.SerializeProviderSettings(settings), cancellationToken);

                    ApplyLoadedHistoryPath(legacyRoot, cancellationToken);
                    return settings;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public async Task SaveSettingsAsync(AgentSettingsDocument settings, CancellationToken cancellationToken)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            UnityAgentHost.ValidateSettings(settings);
            var machineJson = AgentDocumentCodec.SerializeMachineSettings(settings);
            var providerJson = AgentDocumentCodec.SerializeProviderSettings(settings);
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteAtomic(Path.Combine(_settingsRootPath, AgentPaths.SettingsFileName), machineJson,
                        cancellationToken);
                    WriteAtomic(ProviderSettingsPath, providerJson, cancellationToken);
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public void Dispose()
        {
            _ioGate.Dispose();
        }

        private async Task WriteAsync(string path, string json, CancellationToken cancellationToken)
        {
            await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Task.Run(() => WriteAtomic(path, json, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ioGate.Release();
            }
        }

        private static void WriteAtomic(string path, string json, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (json.Length > MaximumDocumentCharacters)
                throw new InvalidDataException(
                    $"Agent document exceeds the {MaximumDocumentCharacters:N0} character storage limit: {path}");
            var directory = Path.GetDirectoryName(path)
                            ?? throw new InvalidOperationException("Storage path has no parent directory.");
            Directory.CreateDirectory(directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporary, path, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        ReplaceWithoutFileReplace(temporary, path, cancellationToken);
                    }
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            finally
            {
                TryDeleteTemporary(temporary);
            }
        }

        private static T ReadDocument<T>(
            string path,
            Func<string, T> deserialize,
            CancellationToken cancellationToken)
        {
            if (File.Exists(path))
            {
                try
                {
                    return deserialize(ReadStoredText(path, cancellationToken));
                }
                catch (Exception exception) when (IsRecoverableDocumentError(exception))
                {
                    throw new InvalidDataException($"Agent document is unreadable: {path}.", exception);
                }
            }

            throw new FileNotFoundException("Agent document was not found.", path);
        }

        private static string ReadStoredText(string path, CancellationToken cancellationToken)
        {
            var text = new StringBuilder(Math.Min(MaximumDocumentCharacters, 16_384));
            var buffer = new char[8_192];
            using var reader = new StreamReader(path, Encoding.UTF8, true);
            int read;
            while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (text.Length + read > MaximumDocumentCharacters)
                    throw new InvalidDataException(
                        $"Agent document exceeds the {MaximumDocumentCharacters:N0} character storage limit: {path}");
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }

        private static bool IsRecoverableDocumentError(Exception exception)
        {
            return exception is IOException ||
                   exception is UnauthorizedAccessException ||
                   exception is FormatException ||
                   exception is InvalidOperationException ||
                   exception is ArgumentException ||
                   exception is OverflowException;
        }

        private static void ReplaceWithoutFileReplace(
            string temporary,
            string path,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(temporary, path, true);
            File.Delete(temporary);
        }

        private static void TryDeleteTemporary(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
                // The destination write has already completed or failed; a stale unique temp file is recoverable.
            }
            catch (UnauthorizedAccessException)
            {
                // Preserve the original write result instead of replacing it with a cleanup-only failure.
            }
        }

        private static string ValidateId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Session id is required.", nameof(value));
            foreach (var character in value)
            {
                if (!char.IsLetterOrDigit(character) && character != '-' && character != '_')
                    throw new ArgumentException("Session id contains unsupported characters.", nameof(value));
            }

            return value;
        }

        private static void ValidateLoadedSessionIdentity(
            string primaryPath,
            AgentSessionDocument session,
            IDictionary<string, string> sessionsById)
        {
            string documentId;
            try
            {
                documentId = ValidateId(session.Id);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Agent session document '{primaryPath}' contains an invalid session id.", exception);
            }

            var fileName = Path.GetFileName(primaryPath);
            var expectedId = Path.GetFileNameWithoutExtension(fileName);
            try
            {
                ValidateId(expectedId);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException(
                    $"Agent session file name '{fileName}' does not contain a valid session id.", exception);
            }

            if (!string.Equals(documentId, expectedId, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Agent session file name '{fileName}' identifies session '{expectedId}', " +
                    $"but the document id is '{documentId}'.");
            }

            if (sessionsById.TryGetValue(documentId, out var existingPath))
            {
                throw new InvalidDataException(
                    $"Duplicate agent session id '{documentId}' is stored in '{existingPath}' and '{primaryPath}'.");
            }
            sessionsById.Add(documentId, primaryPath);
        }

        private void ApplyLoadedHistoryPath(string legacyRoot, CancellationToken cancellationToken)
        {
            if (!_settingsLoaded && _usesDefaultSettingsRoot)
                MigrateLegacySessionsOnce(legacyRoot, cancellationToken);
            _settingsLoaded = true;
        }

        private void MigrateLegacySessionsOnce(string legacyRoot, CancellationToken cancellationToken)
        {
            var marker = Path.Combine(_settingsRootPath, LegacyMigrationMarkerName);
            if (File.Exists(marker)) return;
            var legacyCandidates = new[]
            {
                Path.Combine(AgentPaths.LegacySettingsRoot, AgentPaths.AgentConversationsFolderName),
                Path.Combine(AgentPaths.LegacySettingsRoot, "Sessions"),
                string.IsNullOrEmpty(legacyRoot) ? string.Empty : Path.Combine(legacyRoot, "Sessions"),
                Path.Combine(_settingsRootPath, "Sessions")
            };
            foreach (var source in legacyCandidates.Where(value => !string.IsNullOrEmpty(value)))
                CopySessionDocuments(source, _sessionsPath, cancellationToken);
            WriteAtomic(marker, "UnityAgentTool legacy store migration completed.\n", cancellationToken);
        }

        private AgentSettingsDocument LoadMachineSettings(
            string path,
            string legacyPath,
            CancellationToken cancellationToken,
            out bool needsWrite)
        {
            needsWrite = false;
            var sourcePath = HasStoredDocument(path)
                ? path
                : HasStoredDocument(legacyPath) ? legacyPath : string.Empty;
            if (string.IsNullOrEmpty(sourcePath))
            {
                needsWrite = true;
                return CreateMachineDefaults();
            }

            try
            {
                needsWrite = !AgentPaths.PathsEqual(sourcePath, path);
                var settings = ReadDocument(sourcePath,
                    json => AgentDocumentCodec.DeserializeMachineSettings(json, _projectDefaults),
                    cancellationToken);
                UnityAgentHost.ValidateMachineSettings(settings);
                return settings;
            }
            catch (Exception exception) when (IsMalformedSettingsDocumentError(exception))
            {
                ArchiveMalformedSettings(sourcePath);
                needsWrite = true;
                return CreateMachineDefaults();
            }
        }

        private AgentProviderSettingsDocument LoadProviderSettings(
            string path,
            CancellationToken cancellationToken,
            out bool needsWrite)
        {
            needsWrite = false;
            if (HasStoredDocument(path))
            {
                try
                {
                    var settings = ReadDocument(path, AgentDocumentCodec.DeserializeProviderSettings,
                        cancellationToken);
                    UnityAgentHost.ValidateProviderSettings(settings);
                    return settings;
                }
                catch (Exception exception) when (IsMalformedSettingsDocumentError(exception))
                {
                    ArchiveMalformedSettings(path);
                    needsWrite = true;
                    return CreateProviderDefaults();
                }
            }
            needsWrite = true;
            return CreateProviderDefaults();
        }

        private static bool HasStoredDocument(string path)
        {
            return !string.IsNullOrEmpty(path) && File.Exists(path);
        }

        private AgentSettingsDocument CreateMachineDefaults()
        {
            if (_projectDefaults == null)
                throw new InvalidOperationException(
                    "Machine settings require recovery, but the effective project/package defaults are unavailable.",
                    _projectDefaultsError);
            var settings = new AgentSettingsDocument();
            _projectDefaults.ApplyTo(settings);
            return settings;
        }

        private static AgentProviderSettingsDocument CreateProviderDefaults()
        {
            return AgentProviderSettingsDocument.CreateDefault();
        }

        private static bool IsMalformedSettingsDocumentError(Exception exception)
        {
            if (exception is FormatException or ArgumentException or InvalidOperationException or OverflowException)
                return true;
            return exception is InvalidDataException &&
                   (exception.InnerException == null || IsMalformedSettingsDocumentError(exception.InnerException));
        }

        private static void ArchiveMalformedSettings(string path)
        {
            var suffix = ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            if (File.Exists(path)) File.Move(path, path + suffix);
        }

        private static string GetLegacyRootPath()
        {
            var recent = AgentPaths.LegacySettingsRoot;
            if (File.Exists(Path.Combine(recent, AgentPaths.SettingsFileName))) return recent;
            return Path.GetFullPath(AgentPaths.IsEditor
                ? Path.Combine(AgentPaths.ProjectRoot, "Library", "UnityAgentTool")
                : Path.Combine(AgentPaths.LegacySettingsRoot, "UnityAgentTool"));
        }

        private static void CopySessionDocuments(
            string sourceDirectory,
            string destinationDirectory,
            CancellationToken cancellationToken)
        {
            if (!Directory.Exists(sourceDirectory) || AgentPaths.PathsEqual(sourceDirectory, destinationDirectory))
                return;
            Directory.CreateDirectory(destinationDirectory);
            foreach (var source in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
                         .Where(path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => path, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = Path.Combine(destinationDirectory, Path.GetFileName(source));
                if (File.Exists(destination))
                {
                    if (!FilesEqual(source, destination, cancellationToken))
                        throw new IOException(
                            $"Conversation history migration found conflicting documents: {source} and {destination}");
                    continue;
                }

                var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.Copy(source, temporary, false);
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Move(temporary, destination);
                }
                finally
                {
                    TryDeleteTemporary(temporary);
                }
            }
        }

        private static bool FilesEqual(string first, string second, CancellationToken cancellationToken)
        {
            var firstInfo = new FileInfo(first);
            var secondInfo = new FileInfo(second);
            if (firstInfo.Length != secondInfo.Length) return false;
            const int bufferSize = 8192;
            var firstBuffer = new byte[bufferSize];
            var secondBuffer = new byte[bufferSize];
            using var firstStream = File.OpenRead(first);
            using var secondStream = File.OpenRead(second);
            int read;
            while ((read = firstStream.Read(firstBuffer, 0, firstBuffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (secondStream.Read(secondBuffer, 0, read) != read) return false;
                for (var index = 0; index < read; index++)
                {
                    if (firstBuffer[index] != secondBuffer[index]) return false;
                }
            }
            return secondStream.ReadByte() < 0;
        }

        private static bool StoredSessionRequiresUpgrade(
            string primaryPath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(primaryPath)) return false;
            try
            {
                var root = AgentJson.ParseObject(ReadStoredText(primaryPath, cancellationToken));
                return AgentJson.GetSchemaVersion(root) <
                       AgentSessionDocument.CurrentSchemaVersion;
            }
            catch (Exception exception) when (IsRecoverableDocumentError(exception))
            {
                // ReadDocument owns the user-facing error for malformed documents.
                return false;
            }
        }
    }
}
