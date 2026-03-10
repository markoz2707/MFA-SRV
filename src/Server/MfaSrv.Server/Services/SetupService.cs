using System.Text.Json;
using System.Text.Json.Nodes;

namespace MfaSrv.Server.Services;

public record SetupModel
{
    public string LdapServer { get; init; } = string.Empty;
    public int LdapPort { get; init; } = 389;
    public string BaseDn { get; init; } = string.Empty;
    public string BindDn { get; init; } = string.Empty;
    public string BindPassword { get; init; } = string.Empty;
    public bool UseSsl { get; init; }
    public int HttpPort { get; init; } = 5080;
    public int GrpcPort { get; init; } = 5081;
    public string EncryptionKey { get; init; } = string.Empty;
}

public class SetupService
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<SetupService> _logger;

    public bool IsSetupCompleted { get; private set; }

    public SetupService(IConfiguration configuration, IHostEnvironment environment, ILogger<SetupService> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _logger = logger;
    }

    public bool IsSetupRequired()
    {
        if (IsSetupCompleted)
            return false;

        var ldapServer = _configuration["Ldap:Server"] ?? string.Empty;
        var bindDn = _configuration["Ldap:BindDn"] ?? string.Empty;
        var encryptionKey = _configuration["MfaSrv:EncryptionKey"] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(ldapServer) || ldapServer == "dc01.example.com")
            return true;

        if (string.IsNullOrWhiteSpace(bindDn) || bindDn == "CN=svc-mfasrv,OU=ServiceAccounts,DC=example,DC=com")
            return true;

        if (string.IsNullOrWhiteSpace(encryptionKey))
            return true;

        return false;
    }

    public async Task SaveConfigurationAsync(SetupModel model)
    {
        var appSettingsPath = Path.Combine(_environment.ContentRootPath, "appsettings.json");

        _logger.LogInformation("Saving setup configuration to {Path}", appSettingsPath);

        var json = await File.ReadAllTextAsync(appSettingsPath);
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;

        // LDAP settings
        var ldap = root["Ldap"]!.AsObject();
        ldap["Server"] = model.LdapServer;
        ldap["Port"] = model.LdapPort;
        ldap["BaseDn"] = model.BaseDn;
        ldap["BindDn"] = model.BindDn;
        ldap["BindPassword"] = model.BindPassword;
        ldap["UseSsl"] = model.UseSsl;

        // Encryption key
        var mfaSrv = root["MfaSrv"]!.AsObject();
        mfaSrv["EncryptionKey"] = model.EncryptionKey;

        // Kestrel ports
        var kestrel = root["Kestrel"]!["Endpoints"]!;
        kestrel["Http"]!["Url"] = $"http://0.0.0.0:{model.HttpPort}";
        kestrel["Grpc"]!["Url"] = $"http://0.0.0.0:{model.GrpcPort}";

        var options = new JsonSerializerOptions { WriteIndented = true };
        var updatedJson = root.ToJsonString(options);
        await File.WriteAllTextAsync(appSettingsPath, updatedJson);

        IsSetupCompleted = true;
        _logger.LogInformation("Setup configuration saved successfully. Service restart required.");
    }
}
