﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNet.Cryptography;
using Microsoft.AspNet.DataProtection.AuthenticatedEncryption;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.DataProtection.KeyManagement
{
    /// <summary>
    /// Implements policy for resolving the default key from a candidate keyring.
    /// </summary>
    internal sealed class DefaultKeyResolver : IDefaultKeyResolver
    {
        /// <summary>
        /// The window of time before the key expires when a new key should be created
        /// and persisted to the keyring to ensure uninterrupted service.
        /// </summary>
        /// <remarks>
        /// If the propagation time is 5 days and the current key expires within 5 days,
        /// a new key will be generated.
        /// </remarks>
        private readonly TimeSpan _keyPropagationWindow;

        private readonly ILogger _logger;

        /// <summary>
        /// The maximum skew that is allowed between servers.
        /// This is used to allow newly-created keys to be used across servers even though
        /// their activation dates might be a few minutes into the future.
        /// </summary>
        /// <remarks>
        /// If the max skew is 5 minutes and the best matching candidate default key has
        /// an activation date of less than 5 minutes in the future, we'll use it.
        /// </remarks>
        private readonly TimeSpan _maxServerToServerClockSkew;

        public DefaultKeyResolver(TimeSpan keyPropagationWindow, TimeSpan maxServerToServerClockSkew, IServiceProvider services)
        {
            _keyPropagationWindow = keyPropagationWindow;
            _maxServerToServerClockSkew = maxServerToServerClockSkew;
            _logger = services.GetLogger<DefaultKeyResolver>();
        }

        private bool CanCreateAuthenticatedEncryptor(IKey key)
        {
            try
            {
                var encryptorInstance = key.CreateEncryptorInstance() ?? CryptoUtil.Fail<IAuthenticatedEncryptor>("CreateEncryptorInstance returned null.");
                return true;
            }
            catch (Exception ex)
            {
                if (_logger.IsWarningLevelEnabled())
                {
                    _logger.LogWarningF(ex, $"Key {key.KeyId:B} is ineligible to be the default key because its {nameof(IKey.CreateEncryptorInstance)} method failed.");
                }
                return false;
            }
        }

        private IKey FindDefaultKey(DateTimeOffset now, IEnumerable<IKey> allKeys, out IKey fallbackKey, out bool callerShouldGenerateNewKey)
        {
            // find the preferred default key (allowing for server-to-server clock skew)
            var preferredDefaultKey = (from key in allKeys
                                       where key.ActivationDate <= now + _maxServerToServerClockSkew
                                       orderby key.ActivationDate descending, key.KeyId ascending
                                       select key).FirstOrDefault();

            if (preferredDefaultKey != null)
            {
                if (_logger.IsVerboseLevelEnabled())
                {
                    _logger.LogVerboseF($"Considering key {preferredDefaultKey.KeyId:B} with expiration date {preferredDefaultKey.ExpirationDate:u} as default key.");
                }

                // if the key has been revoked or is expired, it is no longer a candidate
                if (preferredDefaultKey.IsRevoked || preferredDefaultKey.IsExpired(now) || !CanCreateAuthenticatedEncryptor(preferredDefaultKey))
                {
                    if (_logger.IsVerboseLevelEnabled())
                    {
                        _logger.LogVerboseF($"Key {preferredDefaultKey.KeyId:B} is no longer under consideration as default key because it is expired, revoked, or cannot be deciphered.");
                    }
                    preferredDefaultKey = null;
                }
            }

            // Only the key that has been most recently activated is eligible to be the preferred default,
            // and only if it hasn't expired or been revoked. This is intentional: generating a new key is
            // an implicit signal that we should stop using older keys (even if they're not revoked), so
            // activating a new key should permanently mark all older keys as non-preferred.

            if (preferredDefaultKey != null)
            {
                // Does *any* key in the key ring fulfill the requirement that its activation date is prior
                // to the preferred default key's expiration date (allowing for skew) and that it will
                // remain valid one propagation cycle from now? If so, the caller doesn't need to add a
                // new key.
                callerShouldGenerateNewKey = !allKeys.Any(key =>
                   key.ActivationDate <= (preferredDefaultKey.ExpirationDate + _maxServerToServerClockSkew)
                   && !key.IsExpired(now + _keyPropagationWindow)
                   && !key.IsRevoked);

                if (callerShouldGenerateNewKey && _logger.IsVerboseLevelEnabled())
                {
                    _logger.LogVerbose("Default key expiration imminent and repository contains no viable successor. Caller should generate a successor.");
                }

                fallbackKey = null;
                return preferredDefaultKey;
            }

            // If we got this far, the caller must generate a key now.
            // We should locate a fallback key, which is a key that can be used to protect payloads if
            // the caller is configured not to generate a new key. We should try to make sure the fallback
            // key has propagated to all callers (so its creation date should be before the previous
            // propagation period), and we cannot use revoked keys. The fallback key may be expired.
            fallbackKey = (from key in (from key in allKeys
                                        where key.CreationDate <= now - _keyPropagationWindow
                                        orderby key.CreationDate descending
                                        select key).Concat(from key in allKeys
                                                           orderby key.CreationDate ascending
                                                           select key)
                           where !key.IsRevoked && CanCreateAuthenticatedEncryptor(key)
                           select key).FirstOrDefault();

            if (_logger.IsVerboseLevelEnabled())
            {
                _logger.LogVerbose("Repository contains no viable default key. Caller should generate a key with immediate activation.");
            }

            callerShouldGenerateNewKey = true;
            return null;
        }

        public DefaultKeyResolution ResolveDefaultKeyPolicy(DateTimeOffset now, IEnumerable<IKey> allKeys)
        {
            DefaultKeyResolution retVal = default(DefaultKeyResolution);
            retVal.DefaultKey = FindDefaultKey(now, allKeys, out retVal.FallbackKey, out retVal.ShouldGenerateNewKey);
            return retVal;
        }
    }
}
