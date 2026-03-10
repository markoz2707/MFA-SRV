using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using MfaSrv.Server.Services;

namespace MfaSrv.Server.Controllers;

public class SetupController : ControllerBase
{
    private readonly SetupService _setupService;
    private readonly ILogger<SetupController> _logger;

    public SetupController(SetupService setupService, ILogger<SetupController> logger)
    {
        _setupService = setupService;
        _logger = logger;
    }

    [HttpGet("/setup")]
    public IActionResult GetSetupPage()
    {
        if (!_setupService.IsSetupRequired())
            return NotFound();

        return Content(SetupHtml, "text/html");
    }

    [HttpGet("/api/setup/status")]
    public IActionResult GetStatus()
    {
        return Ok(new
        {
            required = _setupService.IsSetupRequired(),
            completed = _setupService.IsSetupCompleted
        });
    }

    [HttpPost("/api/setup/test-ldap")]
    public IActionResult TestLdap([FromBody] LdapTestRequest request)
    {
        if (!_setupService.IsSetupRequired())
            return NotFound();

        try
        {
            var identifier = new LdapDirectoryIdentifier(request.Server, request.Port);
            var credential = new NetworkCredential(request.BindDn, request.BindPassword);
            using var connection = new LdapConnection(identifier);
            connection.Credential = credential;
            connection.AuthType = AuthType.Basic;
            connection.SessionOptions.ProtocolVersion = 3;

            if (request.UseSsl)
                connection.SessionOptions.SecureSocketLayer = true;

            var searchRequest = new SearchRequest(request.BaseDn, "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
            connection.SendRequest(searchRequest);

            return Ok(new { success = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LDAP test connection failed for {Server}:{Port}", request.Server, request.Port);
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpPost("/api/setup/save")]
    public async Task<IActionResult> SaveConfiguration([FromBody] SetupModel model)
    {
        if (!_setupService.IsSetupRequired())
            return NotFound();

        if (string.IsNullOrWhiteSpace(model.LdapServer))
            return BadRequest(new { error = "LDAP Server is required." });

        if (string.IsNullOrWhiteSpace(model.BindDn))
            return BadRequest(new { error = "Bind DN is required." });

        if (string.IsNullOrWhiteSpace(model.BaseDn))
            return BadRequest(new { error = "Base DN is required." });

        if (string.IsNullOrWhiteSpace(model.EncryptionKey))
            return BadRequest(new { error = "Encryption Key is required." });

        try
        {
            await _setupService.SaveConfigurationAsync(model);
            return Ok(new { success = true, message = "Configuration saved. Please restart the MfaSrv Server service for changes to take effect." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save setup configuration");
            return StatusCode(500, new { error = $"Failed to save configuration: {ex.Message}" });
        }
    }

    public record LdapTestRequest
    {
        public string Server { get; init; } = string.Empty;
        public int Port { get; init; } = 389;
        public string BaseDn { get; init; } = string.Empty;
        public string BindDn { get; init; } = string.Empty;
        public string BindPassword { get; init; } = string.Empty;
        public bool UseSsl { get; init; }
    }

    private const string SetupHtml = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>MfaSrv Server - Initial Setup</title>
<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; background: #f0f2f5; color: #1a1a2e; min-height: 100vh; display: flex; align-items: center; justify-content: center; padding: 20px; }
  .container { background: #fff; border-radius: 12px; box-shadow: 0 4px 24px rgba(0,0,0,0.08); max-width: 640px; width: 100%; padding: 40px; }
  .logo { text-align: center; margin-bottom: 32px; }
  .logo h1 { font-size: 28px; font-weight: 700; color: #1a1a2e; }
  .logo h1 span { color: #4f6ef7; }
  .logo p { color: #6b7280; margin-top: 4px; font-size: 14px; }
  .section { margin-bottom: 28px; }
  .section h2 { font-size: 16px; font-weight: 600; color: #374151; margin-bottom: 16px; padding-bottom: 8px; border-bottom: 2px solid #e5e7eb; }
  .field { margin-bottom: 14px; }
  .field label { display: block; font-size: 13px; font-weight: 500; color: #4b5563; margin-bottom: 4px; }
  .field input[type="text"], .field input[type="password"], .field input[type="number"] {
    width: 100%; padding: 9px 12px; border: 1px solid #d1d5db; border-radius: 6px; font-size: 14px;
    transition: border-color 0.15s; outline: none; }
  .field input:focus { border-color: #4f6ef7; box-shadow: 0 0 0 3px rgba(79,110,247,0.1); }
  .row { display: flex; gap: 14px; }
  .row .field { flex: 1; }
  .checkbox { display: flex; align-items: center; gap: 8px; margin-bottom: 14px; }
  .checkbox input { width: 16px; height: 16px; accent-color: #4f6ef7; }
  .checkbox label { font-size: 13px; color: #4b5563; margin-bottom: 0; }
  .btn { display: inline-flex; align-items: center; gap: 6px; padding: 9px 18px; border: none; border-radius: 6px; font-size: 14px; font-weight: 500; cursor: pointer; transition: background 0.15s; }
  .btn-primary { background: #4f6ef7; color: #fff; }
  .btn-primary:hover { background: #3b5de7; }
  .btn-primary:disabled { background: #93a3f8; cursor: not-allowed; }
  .btn-secondary { background: #f3f4f6; color: #374151; border: 1px solid #d1d5db; }
  .btn-secondary:hover { background: #e5e7eb; }
  .btn-group { display: flex; gap: 10px; align-items: center; }
  .key-row { display: flex; gap: 10px; }
  .key-row input { flex: 1; }
  .alert { padding: 12px 16px; border-radius: 6px; font-size: 13px; margin-top: 16px; display: none; }
  .alert-success { background: #ecfdf5; color: #065f46; border: 1px solid #a7f3d0; }
  .alert-error { background: #fef2f2; color: #991b1b; border: 1px solid #fecaca; }
  .alert-info { background: #eff6ff; color: #1e40af; border: 1px solid #bfdbfe; }
  .ldap-status { font-size: 13px; margin-left: 8px; }
  .spinner { display: inline-block; width: 14px; height: 14px; border: 2px solid #d1d5db; border-top-color: #4f6ef7; border-radius: 50%; animation: spin 0.6s linear infinite; }
  @keyframes spin { to { transform: rotate(360deg); } }
  .footer { text-align: center; margin-top: 24px; color: #9ca3af; font-size: 12px; }
</style>
</head>
<body>
<div class="container">
  <div class="logo">
    <h1>Mfa<span>Srv</span> Server</h1>
    <p>Initial Configuration</p>
  </div>

  <form id="setupForm" onsubmit="return false;">
    <div class="section">
      <h2>LDAP Connection</h2>
      <div class="row">
        <div class="field" style="flex:3">
          <label for="ldapServer">Server</label>
          <input type="text" id="ldapServer" placeholder="dc01.corp.example.com" required />
        </div>
        <div class="field" style="flex:1">
          <label for="ldapPort">Port</label>
          <input type="number" id="ldapPort" value="389" min="1" max="65535" required />
        </div>
      </div>
      <div class="field">
        <label for="baseDn">Base DN</label>
        <input type="text" id="baseDn" placeholder="DC=corp,DC=example,DC=com" required />
      </div>
      <div class="field">
        <label for="bindDn">Bind DN (Service Account)</label>
        <input type="text" id="bindDn" placeholder="CN=svc-mfasrv,OU=ServiceAccounts,DC=corp,DC=example,DC=com" required />
      </div>
      <div class="field">
        <label for="bindPassword">Bind Password</label>
        <input type="password" id="bindPassword" placeholder="Service account password" />
      </div>
      <div class="checkbox">
        <input type="checkbox" id="useSsl" onchange="document.getElementById('ldapPort').value = this.checked ? 636 : 389" />
        <label for="useSsl">Use SSL (LDAPS)</label>
      </div>
      <div class="btn-group">
        <button type="button" class="btn btn-secondary" onclick="testLdap()">Test Connection</button>
        <span id="ldapStatus" class="ldap-status"></span>
      </div>
    </div>

    <div class="section">
      <h2>Network Ports</h2>
      <div class="row">
        <div class="field">
          <label for="httpPort">HTTP / REST API Port</label>
          <input type="number" id="httpPort" value="5080" min="1" max="65535" required />
        </div>
        <div class="field">
          <label for="grpcPort">gRPC Agent Port</label>
          <input type="number" id="grpcPort" value="5081" min="1" max="65535" required />
        </div>
      </div>
    </div>

    <div class="section">
      <h2>Encryption Key</h2>
      <p style="font-size:13px; color:#6b7280; margin-bottom:10px;">AES-256 key used to encrypt MFA secrets. Click Generate to create a secure random key.</p>
      <div class="key-row">
        <input type="text" id="encryptionKey" placeholder="Base64-encoded 32-byte key" required />
        <button type="button" class="btn btn-secondary" onclick="generateKey()">Generate</button>
      </div>
    </div>

    <button type="button" class="btn btn-primary" style="width:100%; justify-content:center; padding: 12px;" onclick="saveConfig()" id="saveBtn">
      Save Configuration
    </button>

    <div id="alertSuccess" class="alert alert-success"></div>
    <div id="alertError" class="alert alert-error"></div>
  </form>

  <div class="footer">MfaSrv Server &mdash; Multi-Factor Authentication for Active Directory</div>
</div>

<script>
function generateKey() {
  const bytes = new Uint8Array(32);
  crypto.getRandomValues(bytes);
  const base64 = btoa(String.fromCharCode(...bytes));
  document.getElementById('encryptionKey').value = base64;
}

async function testLdap() {
  const status = document.getElementById('ldapStatus');
  status.innerHTML = '<span class="spinner"></span> Testing...';
  try {
    const res = await fetch('/api/setup/test-ldap', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        server: document.getElementById('ldapServer').value,
        port: parseInt(document.getElementById('ldapPort').value),
        baseDn: document.getElementById('baseDn').value,
        bindDn: document.getElementById('bindDn').value,
        bindPassword: document.getElementById('bindPassword').value,
        useSsl: document.getElementById('useSsl').checked
      })
    });
    const data = await res.json();
    if (data.success) {
      status.innerHTML = '<span style="color:#059669">&#10003; Connected successfully</span>';
    } else {
      status.innerHTML = '<span style="color:#dc2626">&#10007; ' + (data.error || 'Connection failed') + '</span>';
    }
  } catch (e) {
    status.innerHTML = '<span style="color:#dc2626">&#10007; Request failed</span>';
  }
}

async function saveConfig() {
  const btn = document.getElementById('saveBtn');
  const alertSuccess = document.getElementById('alertSuccess');
  const alertError = document.getElementById('alertError');
  alertSuccess.style.display = 'none';
  alertError.style.display = 'none';
  btn.disabled = true;
  btn.textContent = 'Saving...';

  try {
    const res = await fetch('/api/setup/save', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ldapServer: document.getElementById('ldapServer').value,
        ldapPort: parseInt(document.getElementById('ldapPort').value),
        baseDn: document.getElementById('baseDn').value,
        bindDn: document.getElementById('bindDn').value,
        bindPassword: document.getElementById('bindPassword').value,
        useSsl: document.getElementById('useSsl').checked,
        httpPort: parseInt(document.getElementById('httpPort').value),
        grpcPort: parseInt(document.getElementById('grpcPort').value),
        encryptionKey: document.getElementById('encryptionKey').value
      })
    });
    const data = await res.json();
    if (data.success) {
      alertSuccess.textContent = data.message;
      alertSuccess.style.display = 'block';
      btn.textContent = 'Configuration Saved';
    } else {
      alertError.textContent = data.error || 'Save failed.';
      alertError.style.display = 'block';
      btn.disabled = false;
      btn.textContent = 'Save Configuration';
    }
  } catch (e) {
    alertError.textContent = 'Request failed. Check the server logs.';
    alertError.style.display = 'block';
    btn.disabled = false;
    btn.textContent = 'Save Configuration';
  }
}
</script>
</body>
</html>
""";
}
