#nullable enable
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using UnityEngine;

namespace YuzeToolkit.Eval
{
    public sealed class UnityEvalToolAuthorizationSettings : ScriptableObject
    {
        public const string ResourceName = "UnityEvalToolAuthorizationSettings";
        public const string AssetPath = "Assets/Resources/UnityEvalToolAuthorizationSettings.asset";
        public const string AlgorithmVersion = "PBKDF2-HMAC-SHA256-v1";
        public const int Iterations = 120_000;
        public const int MaxTokenLength = 256;

        [SerializeField] private bool _requireToken;
        [SerializeField] private string _algorithm = AlgorithmVersion;
        [SerializeField] private string _saltBase64 = string.Empty;
        [SerializeField] private string _verifierBase64 = string.Empty;

        public bool RequireToken => _requireToken;
        public string Algorithm => _algorithm;
        public bool HasVerifier => !string.IsNullOrWhiteSpace(_saltBase64) &&
                                   !string.IsNullOrWhiteSpace(_verifierBase64);

        public static UnityEvalToolAuthorizationSettings? Load() =>
            Resources.Load<UnityEvalToolAuthorizationSettings>(ResourceName);

        public void ConfigureToken(string token)
        {
            ValidateToken(token);
            var salt = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(salt);
            _algorithm = AlgorithmVersion;
            _saltBase64 = Convert.ToBase64String(salt);
            _verifierBase64 = Convert.ToBase64String(DeriveVerifier(token, salt));
            _requireToken = true;
        }

        public void SetRequireToken(bool requireToken)
        {
            if (requireToken && !HasVerifier)
                throw new InvalidOperationException("Configure a Yuze Eval Tool token before enabling verification.");
            _requireToken = requireToken;
        }

        public void ClearToken()
        {
            _requireToken = false;
            _algorithm = AlgorithmVersion;
            _saltBase64 = string.Empty;
            _verifierBase64 = string.Empty;
        }

        public AuthorizationVerifier CreateVerifier()
        {
            if (!_requireToken) return AuthorizationVerifier.Disabled;
            ValidateStoredConfiguration();
            return new AuthorizationVerifier(true, Convert.FromBase64String(_saltBase64),
                Convert.FromBase64String(_verifierBase64));
        }

        public static string GenerateToken(string? name)
        {
            var normalizedName = name ?? string.Empty;
            if (normalizedName.Length > 0) ValidateToken(normalizedName);
            var randomBytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(randomBytes);
            var randomPart = Convert.ToBase64String(randomBytes).TrimEnd('=')
                .Replace('+', '-').Replace('/', '_');
            return normalizedName.Length == 0 ? randomPart : normalizedName + "_" + randomPart;
        }

        public static void ValidateToken(string token)
        {
            if (string.IsNullOrEmpty(token))
                throw new ArgumentException("Yuze Eval Tool token cannot be empty.", nameof(token));
            if (token.Length > MaxTokenLength)
                throw new ArgumentException($"Yuze Eval Tool token cannot exceed {MaxTokenLength} characters.", nameof(token));
            foreach (var character in token)
            {
                var allowed = character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-';
                if (!allowed)
                    throw new ArgumentException(
                        "Yuze Eval Tool token may contain only ASCII letters, digits, underscore, and hyphen.", nameof(token));
            }
        }

        private void ValidateStoredConfiguration()
        {
            if (!string.Equals(_algorithm, AlgorithmVersion, StringComparison.Ordinal))
                throw new InvalidOperationException($"Unsupported Yuze Eval Tool token algorithm '{_algorithm}'.");
            if (!HasVerifier)
                throw new InvalidOperationException("Yuze Eval Tool token verification is enabled without a verifier.");
            try
            {
                if (Convert.FromBase64String(_saltBase64).Length != 16 ||
                    Convert.FromBase64String(_verifierBase64).Length != 32)
                    throw new InvalidOperationException("Yuze Eval Tool token verifier has invalid byte lengths.");
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException("Yuze Eval Tool token verifier is not valid Base64.", ex);
            }
        }

        private static byte[] DeriveVerifier(string token, byte[] salt)
        {
            using var derive = new Rfc2898DeriveBytes(token, salt, Iterations, HashAlgorithmName.SHA256);
            return derive.GetBytes(32);
        }

        private static bool FixedTimeEquals(IReadOnlyList<byte> left, IReadOnlyList<byte> right)
        {
            if (left.Count != right.Count) return false;
            var difference = 0;
            for (var index = 0; index < left.Count; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }

        public sealed class AuthorizationVerifier
        {
            public static AuthorizationVerifier Disabled { get; } =
                new(false, Array.Empty<byte>(), Array.Empty<byte>());

            private readonly byte[] _salt;
            private readonly byte[] _expected;

            internal AuthorizationVerifier(bool requireToken, byte[] salt, byte[] expected)
            {
                RequireToken = requireToken;
                _salt = salt;
                _expected = expected;
            }

            public bool RequireToken { get; }

            public bool VerifyTokens(IReadOnlyList<string> tokens)
            {
                if (!RequireToken) return true;
                foreach (var token in tokens)
                {
                    try
                    {
                        ValidateToken(token);
                    }
                    catch (ArgumentException)
                    {
                        continue;
                    }
                    if (FixedTimeEquals(_expected, DeriveVerifier(token, _salt))) return true;
                }
                return false;
            }
        }
    }
}
