using System.DirectoryServices.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Interfaces;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Services;

public class LdapSettings
{
    public string Server { get; set; } = string.Empty;
    public int Port { get; set; } = 389;
    public string BaseDn { get; set; } = string.Empty;
    public string BindDn { get; set; } = string.Empty;
    public string BindPassword { get; set; } = string.Empty;
    public bool UseSsl { get; set; }
    public string UserFilter { get; set; } = "(&(objectCategory=person)(objectClass=user)(!(userAccountControl:1.2.840.113556.1.4.803:=2)))";
    public string GroupFilter { get; set; } = "(objectClass=group)";
    public int SyncIntervalMinutes { get; set; } = 15;
}

public class UserSyncService : IUserSyncService
{
    private readonly MfaSrvDbContext _db;
    private readonly LdapSettings _settings;
    private readonly ILogger<UserSyncService> _logger;

    public UserSyncService(MfaSrvDbContext db, IOptions<LdapSettings> settings, ILogger<UserSyncService> logger)
    {
        _db = db;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SyncUsersAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("Starting LDAP user sync from {Server}", _settings.Server);

        try
        {
            using var connection = CreateConnection();
            var searchRequest = new SearchRequest(
                _settings.BaseDn,
                _settings.UserFilter,
                SearchScope.Subtree,
                "objectGUID", "sAMAccountName", "userPrincipalName", "displayName", "mail", "telephoneNumber", "distinguishedName", "memberOf");

            var response = (SearchResponse)connection.SendRequest(searchRequest);
            var syncTime = DateTimeOffset.UtcNow;
            var syncedCount = 0;

            foreach (SearchResultEntry entry in response.Entries)
            {
                if (ct.IsCancellationRequested) break;

                var objectGuid = entry.Attributes["objectGUID"]?[0] is byte[] guidBytes
                    ? new Guid(guidBytes).ToString()
                    : null;

                if (objectGuid == null) continue;

                var samAccountName = entry.Attributes["sAMAccountName"]?[0]?.ToString() ?? string.Empty;
                var upn = entry.Attributes["userPrincipalName"]?[0]?.ToString() ?? string.Empty;
                var displayName = entry.Attributes["displayName"]?[0]?.ToString() ?? string.Empty;
                var email = entry.Attributes["mail"]?[0]?.ToString();
                var phone = entry.Attributes["telephoneNumber"]?[0]?.ToString();
                var dn = entry.Attributes["distinguishedName"]?[0]?.ToString() ?? string.Empty;

                var user = await _db.Users.FirstOrDefaultAsync(u => u.ObjectGuid == objectGuid, ct);
                if (user == null)
                {
                    user = new User { ObjectGuid = objectGuid };
                    _db.Users.Add(user);
                }

                user.SamAccountName = samAccountName;
                user.UserPrincipalName = upn;
                user.DisplayName = displayName;
                user.Email = email;
                user.PhoneNumber = phone;
                user.DistinguishedName = dn;
                user.LastSyncAt = syncTime;
                user.UpdatedAt = syncTime;

                // Sync group memberships
                if (entry.Attributes["memberOf"] != null)
                {
                    var existingMemberships = await _db.UserGroupMemberships
                        .Where(m => m.UserId == user.Id)
                        .ToListAsync(ct);
                    _db.UserGroupMemberships.RemoveRange(existingMemberships);

                    for (int i = 0; i < entry.Attributes["memberOf"].Count; i++)
                    {
                        var groupDn = entry.Attributes["memberOf"][i]?.ToString() ?? string.Empty;
                        var groupName = ExtractCnFromDn(groupDn);

                        _db.UserGroupMemberships.Add(new UserGroupMembership
                        {
                            UserId = user.Id,
                            GroupDn = groupDn,
                            GroupName = groupName,
                            GroupSid = groupDn, // SID resolution would require additional lookup
                            SyncedAt = syncTime
                        });
                    }
                }

                syncedCount++;
            }

            await _db.SaveChangesAsync(ct);
            _logger.LogInformation("LDAP user sync completed. Synced {Count} users", syncedCount);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP user sync failed");
            throw;
        }
    }

    public async Task SyncGroupsAsync(CancellationToken ct = default)
    {
        // Group sync is handled as part of user sync (memberOf attribute)
        await SyncUsersAsync(ct);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            using var connection = CreateConnection();
            var request = new SearchRequest(_settings.BaseDn, "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
            connection.SendRequest(request);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LDAP connection test failed");
            return false;
        }
    }

    private LdapConnection CreateConnection()
    {
        var connection = new LdapConnection(new LdapDirectoryIdentifier(_settings.Server, _settings.Port));
        connection.Credential = new System.Net.NetworkCredential(_settings.BindDn, _settings.BindPassword);
        connection.AuthType = AuthType.Basic;
        connection.SessionOptions.ProtocolVersion = 3;

        if (_settings.UseSsl)
            connection.SessionOptions.SecureSocketLayer = true;

        connection.Bind();
        return connection;
    }

    private static string ExtractCnFromDn(string dn)
    {
        if (string.IsNullOrEmpty(dn)) return string.Empty;
        var parts = dn.Split(',');
        if (parts.Length > 0 && parts[0].StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
            return parts[0][3..];
        return dn;
    }
}
