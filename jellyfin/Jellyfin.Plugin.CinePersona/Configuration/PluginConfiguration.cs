using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.CinePersona.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string ApiKey { get; set; } = string.Empty;

    public string ServerUrl { get; set; } = "https://cinepersona.com";

    public bool ReverseSyncEnabled { get; set; } = true;

    public string SyncUserId { get; set; } = string.Empty;

    public bool InitialSyncCompleted { get; set; }

    public DateTime? LastSyncAt { get; set; }

    public string LastSyncUserId { get; set; } = string.Empty;

    /// <summary>
    /// Each Jellyfin account owns its own CinePersona connection.
    /// The legacy single-user fields above are retained for migration.
    /// </summary>
    public List<UserSyncProfile> UserProfiles { get; set; } = new();

    public UserSyncProfile? FindUserProfile(string userId)
    {
        var normalizedUserId = (userId ?? string.Empty).Trim();
        if (normalizedUserId.Length == 0)
        {
            return null;
        }

        return (UserProfiles ?? new List<UserSyncProfile>())
            .FirstOrDefault(profile => profile is not null
                && string.Equals(profile.UserId, normalizedUserId, StringComparison.OrdinalIgnoreCase));
    }

    public UserSyncProfile? GetOrCreateUserProfile(string userId)
    {
        var normalizedUserId = (userId ?? string.Empty).Trim();
        if (normalizedUserId.Length == 0)
        {
            return null;
        }

        UserProfiles ??= new List<UserSyncProfile>();
        var profile = FindUserProfile(normalizedUserId);
        if (profile is not null)
        {
            return profile;
        }

        profile = new UserSyncProfile { UserId = normalizedUserId };
        UserProfiles.Add(profile);
        return profile;
    }

    public IEnumerable<UserSyncProfile> GetConfiguredUserProfiles()
    {
        // Migrate the old configuration lazily. This only applies when the
        // old setting explicitly named its target user, never to all users.
        UserProfiles ??= new List<UserSyncProfile>();
        if (!string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(SyncUserId)
            && !UserProfiles.Any(profile => profile is not null
                && string.Equals(profile.UserId, SyncUserId, StringComparison.OrdinalIgnoreCase)))
        {
            UserProfiles.Add(new UserSyncProfile
            {
                UserId = SyncUserId.Trim(),
                ApiKey = ApiKey.Trim(),
                Enabled = true,
                InitialSyncCompleted = InitialSyncCompleted,
                LastSyncAt = LastSyncAt
            });
        }

        foreach (var profile in UserProfiles)
        {
            if (profile is not null && !string.IsNullOrWhiteSpace(profile.UserId))
            {
                yield return profile;
            }
        }
    }
}

public class UserSyncProfile
{
    public string UserId { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool InitialSyncCompleted { get; set; }

    public DateTime? LastSyncAt { get; set; }
}
