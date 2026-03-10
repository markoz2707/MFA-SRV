using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.DirectoryServices.Protocols;

namespace MfaSrv.Server.Configurator;

public class ConfigForm : Form
{
    private readonly string _installDir;
    private readonly string _configPath;

    // LDAP controls
    private TextBox _txtLdapServer = null!;
    private TextBox _txtLdapPort = null!;
    private TextBox _txtBaseDn = null!;
    private TextBox _txtBindDn = null!;
    private TextBox _txtBindPassword = null!;
    private CheckBox _chkUseSsl = null!;
    private Button _btnTestLdap = null!;

    // Network controls
    private TextBox _txtHttpPort = null!;
    private TextBox _txtGrpcPort = null!;

    // Security controls
    private TextBox _txtEncryptionKey = null!;
    private Button _btnGenerateKey = null!;

    // Action buttons
    private Button _btnSave = null!;
    private Button _btnCancel = null!;

    // Status
    private Label _lblStatus = null!;

    public ConfigForm(string installDir)
    {
        _installDir = installDir;
        _configPath = Path.Combine(installDir, "appsettings.json");
        InitializeComponents();
        LoadConfig();
    }

    private void InitializeComponents()
    {
        Text = "MfaSrv Server - Configuration";
        ClientSize = new Size(500, 540);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.White;
        Font = new Font("Segoe UI", 9f);

        // Header panel
        var headerPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 60,
            BackColor = Color.FromArgb(25, 55, 109)
        };
        var lblTitle = new Label
        {
            Text = "MfaSrv Server Configuration",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = false,
            Size = new Size(460, 30),
            Location = new Point(16, 8)
        };
        var lblSubtitle = new Label
        {
            Text = "Configure LDAP connection, network ports, and encryption",
            ForeColor = Color.FromArgb(180, 200, 230),
            Font = new Font("Segoe UI", 9f),
            AutoSize = false,
            Size = new Size(460, 20),
            Location = new Point(16, 36)
        };
        headerPanel.Controls.Add(lblTitle);
        headerPanel.Controls.Add(lblSubtitle);
        Controls.Add(headerPanel);

        int y = 72;

        // ── LDAP Connection ──
        var grpLdap = new GroupBox
        {
            Text = "LDAP Connection",
            Location = new Point(12, y),
            Size = new Size(476, 195),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(25, 55, 109)
        };
        var normalFont = new Font("Segoe UI", 9f);

        int gy = 22;
        AddLabelAndTextBox(grpLdap, "Server:", ref _txtLdapServer, 18, gy, 270, normalFont);
        AddLabel(grpLdap, "Port:", 350, gy, normalFont);
        _txtLdapPort = AddTextBox(grpLdap, 390, gy, 66, normalFont);
        gy += 30;

        AddLabelAndTextBox(grpLdap, "Base DN:", ref _txtBaseDn, 18, gy, 368, normalFont);
        gy += 30;

        AddLabelAndTextBox(grpLdap, "Bind DN:", ref _txtBindDn, 18, gy, 368, normalFont);
        gy += 30;

        AddLabelAndTextBox(grpLdap, "Bind Password:", ref _txtBindPassword, 18, gy, 368, normalFont);
        _txtBindPassword.UseSystemPasswordChar = true;
        gy += 30;

        _chkUseSsl = new CheckBox
        {
            Text = "Use SSL (LDAPS)",
            Location = new Point(88, gy),
            AutoSize = true,
            Font = normalFont,
            ForeColor = Color.Black
        };
        grpLdap.Controls.Add(_chkUseSsl);

        _btnTestLdap = new Button
        {
            Text = "Test Connection",
            Location = new Point(340, gy - 3),
            Size = new Size(118, 28),
            Font = normalFont,
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.System
        };
        _btnTestLdap.Click += BtnTestLdap_Click;
        grpLdap.Controls.Add(_btnTestLdap);

        Controls.Add(grpLdap);
        y += grpLdap.Height + 8;

        // ── Network Ports ──
        var grpNetwork = new GroupBox
        {
            Text = "Network Ports",
            Location = new Point(12, y),
            Size = new Size(476, 58),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(25, 55, 109)
        };

        AddLabel(grpNetwork, "HTTP Port:", 18, 24, normalFont);
        _txtHttpPort = AddTextBox(grpNetwork, 88, 22, 70, normalFont);
        AddLabel(grpNetwork, "gRPC Port:", 220, 24, normalFont);
        _txtGrpcPort = AddTextBox(grpNetwork, 295, 22, 70, normalFont);

        Controls.Add(grpNetwork);
        y += grpNetwork.Height + 8;

        // ── Security ──
        var grpSecurity = new GroupBox
        {
            Text = "Security",
            Location = new Point(12, y),
            Size = new Size(476, 58),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            ForeColor = Color.FromArgb(25, 55, 109)
        };

        AddLabel(grpSecurity, "Encryption Key:", 18, 24, normalFont);
        _txtEncryptionKey = AddTextBox(grpSecurity, 118, 22, 228, normalFont);
        _btnGenerateKey = new Button
        {
            Text = "Generate",
            Location = new Point(354, 20),
            Size = new Size(104, 28),
            Font = normalFont,
            ForeColor = Color.Black,
            FlatStyle = FlatStyle.System
        };
        _btnGenerateKey.Click += BtnGenerateKey_Click;
        grpSecurity.Controls.Add(_btnGenerateKey);

        Controls.Add(grpSecurity);
        y += grpSecurity.Height + 16;

        // ── Status label ──
        _lblStatus = new Label
        {
            Text = "",
            Location = new Point(12, y),
            Size = new Size(476, 20),
            Font = normalFont,
            ForeColor = Color.Gray,
            TextAlign = ContentAlignment.MiddleLeft
        };
        Controls.Add(_lblStatus);
        y += 26;

        // ── Action buttons ──
        _btnSave = new Button
        {
            Text = "Save && Start Service",
            Location = new Point(230, y),
            Size = new Size(150, 35),
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            FlatStyle = FlatStyle.System
        };
        _btnSave.Click += BtnSave_Click;
        Controls.Add(_btnSave);

        _btnCancel = new Button
        {
            Text = "Cancel",
            Location = new Point(390, y),
            Size = new Size(96, 35),
            Font = normalFont,
            FlatStyle = FlatStyle.System
        };
        _btnCancel.Click += (_, _) => Close();
        Controls.Add(_btnCancel);

        AcceptButton = _btnSave;
        CancelButton = _btnCancel;
    }

    private void LoadConfig()
    {
        try
        {
            var json = File.ReadAllText(_configPath);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip
            });
            if (root == null) return;

            var ldap = root["Ldap"];
            if (ldap != null)
            {
                _txtLdapServer.Text = ldap["Server"]?.GetValue<string>() ?? "";
                _txtLdapPort.Text = (ldap["Port"]?.GetValue<int>() ?? 389).ToString();
                _txtBaseDn.Text = ldap["BaseDn"]?.GetValue<string>() ?? "";
                _txtBindDn.Text = ldap["BindDn"]?.GetValue<string>() ?? "";
                _txtBindPassword.Text = ldap["BindPassword"]?.GetValue<string>() ?? "";
                _chkUseSsl.Checked = ldap["UseSsl"]?.GetValue<bool>() ?? false;
            }

            var kestrel = root["Kestrel"]?["Endpoints"];
            if (kestrel != null)
            {
                var httpUrl = kestrel["Http"]?["Url"]?.GetValue<string>() ?? "";
                var grpcUrl = kestrel["Grpc"]?["Url"]?.GetValue<string>() ?? "";
                _txtHttpPort.Text = ExtractPort(httpUrl, "5080");
                _txtGrpcPort.Text = ExtractPort(grpcUrl, "5081");
            }

            var encKey = root["MfaSrv"]?["EncryptionKey"]?.GetValue<string>() ?? "";
            _txtEncryptionKey.Text = encKey;

            // Clear placeholder values
            if (_txtLdapServer.Text == "dc01.example.com")
                _txtLdapServer.Text = "";
            if (_txtBindDn.Text == "CN=svc-mfasrv,OU=ServiceAccounts,DC=example,DC=com")
                _txtBindDn.Text = "";
            if (_txtBaseDn.Text == "DC=example,DC=com")
                _txtBaseDn.Text = "";

            _lblStatus.Text = "Configuration loaded from appsettings.json";
        }
        catch (Exception ex)
        {
            _lblStatus.ForeColor = Color.Red;
            _lblStatus.Text = $"Error loading config: {ex.Message}";
        }
    }

    private static string ExtractPort(string url, string defaultPort)
    {
        if (string.IsNullOrEmpty(url)) return defaultPort;
        var lastColon = url.LastIndexOf(':');
        if (lastColon < 0) return defaultPort;
        var portStr = url[(lastColon + 1)..];
        return int.TryParse(portStr, out _) ? portStr : defaultPort;
    }

    private async void BtnTestLdap_Click(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_txtLdapServer.Text))
        {
            SetStatus("Enter an LDAP server address first.", Color.Red);
            return;
        }

        _btnTestLdap.Enabled = false;
        SetStatus("Testing LDAP connection...", Color.Gray);

        try
        {
            await Task.Run(() =>
            {
                int port = int.TryParse(_txtLdapPort.Text, out var p) ? p : 389;
                var identifier = new LdapDirectoryIdentifier(_txtLdapServer.Text, port);
                using var connection = new LdapConnection(identifier);
                connection.AuthType = AuthType.Basic;
                connection.Credential = new NetworkCredential(_txtBindDn.Text, _txtBindPassword.Text);
                connection.SessionOptions.ProtocolVersion = 3;

                if (_chkUseSsl.Checked)
                    connection.SessionOptions.SecureSocketLayer = true;

                connection.Bind();
            });

            SetStatus("LDAP connection successful!", Color.Green);
        }
        catch (Exception ex)
        {
            SetStatus($"LDAP connection failed: {ex.Message}", Color.Red);
        }
        finally
        {
            _btnTestLdap.Enabled = true;
        }
    }

    private void BtnGenerateKey_Click(object? sender, EventArgs e)
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        _txtEncryptionKey.Text = Convert.ToBase64String(bytes);
        SetStatus("Encryption key generated.", Color.Green);
    }

    private async void BtnSave_Click(object? sender, EventArgs e)
    {
        // Validate required fields
        if (string.IsNullOrWhiteSpace(_txtLdapServer.Text))
        {
            SetStatus("LDAP Server is required.", Color.Red);
            _txtLdapServer.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtBindDn.Text))
        {
            SetStatus("Bind DN is required.", Color.Red);
            _txtBindDn.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_txtEncryptionKey.Text))
        {
            SetStatus("Encryption key is required. Click Generate to create one.", Color.Red);
            _txtEncryptionKey.Focus();
            return;
        }

        _btnSave.Enabled = false;
        SetStatus("Saving configuration...", Color.Gray);

        try
        {
            // Read and update appsettings.json
            var json = await File.ReadAllTextAsync(_configPath);
            var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip
            })!;

            // LDAP
            var ldap = root["Ldap"]!.AsObject();
            ldap["Server"] = _txtLdapServer.Text.Trim();
            ldap["Port"] = int.TryParse(_txtLdapPort.Text, out var ldapPort) ? ldapPort : 389;
            ldap["BaseDn"] = _txtBaseDn.Text.Trim();
            ldap["BindDn"] = _txtBindDn.Text.Trim();
            ldap["BindPassword"] = _txtBindPassword.Text;
            ldap["UseSsl"] = _chkUseSsl.Checked;

            // Kestrel ports
            int httpPort = int.TryParse(_txtHttpPort.Text, out var hp) ? hp : 5080;
            int grpcPort = int.TryParse(_txtGrpcPort.Text, out var gp) ? gp : 5081;
            var endpoints = root["Kestrel"]!["Endpoints"]!;
            endpoints["Http"]!["Url"] = $"http://0.0.0.0:{httpPort}";
            endpoints["Grpc"]!["Url"] = $"http://0.0.0.0:{grpcPort}";

            // Encryption key
            root["MfaSrv"]!["EncryptionKey"] = _txtEncryptionKey.Text.Trim();

            // Connection string (use ProgramData path)
            root["ConnectionStrings"]!["DefaultConnection"] =
                @"Data Source=C:\ProgramData\MfaSrv\Server\mfasrv.db";

            // Write back
            var options = new JsonSerializerOptions { WriteIndented = true };
            var updatedJson = root.ToJsonString(options);
            await File.WriteAllTextAsync(_configPath, updatedJson);

            SetStatus("Configuration saved. Starting service...", Color.Green);

            // Start the Windows service
            await Task.Run(() =>
            {
                try
                {
                    using var sc = new ServiceController("MfaSrvServer");
                    if (sc.Status == ServiceControllerStatus.Stopped ||
                        sc.Status == ServiceControllerStatus.Paused)
                    {
                        sc.Start();
                        sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }
                }
                catch (InvalidOperationException)
                {
                    // Service not installed (e.g. standalone mode) - that's OK
                }
            });

            SetStatus("Configuration saved and service started successfully!", Color.Green);

            MessageBox.Show(
                "Configuration has been saved and the MfaSrv Server service has been started.\n\n" +
                $"Admin portal: http://localhost:{httpPort}\n" +
                $"gRPC endpoint: http://localhost:{grpcPort}",
                "MfaSrv Configurator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);

            Close();
        }
        catch (Exception ex)
        {
            SetStatus($"Error: {ex.Message}", Color.Red);
            MessageBox.Show(
                $"An error occurred:\n\n{ex.Message}\n\n" +
                "Configuration may have been saved. Try starting the service manually:\n" +
                "  net start MfaSrvServer",
                "MfaSrv Configurator",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _btnSave.Enabled = true;
        }
    }

    private void SetStatus(string text, Color color)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetStatus(text, color));
            return;
        }
        _lblStatus.ForeColor = color;
        _lblStatus.Text = text;
    }

    // ── Helper methods for building the form layout ──

    private static void AddLabelAndTextBox(Control parent, string labelText, ref TextBox textBox,
        int x, int y, int textBoxWidth, Font font)
    {
        var label = new Label
        {
            Text = labelText,
            Location = new Point(x, y + 3),
            AutoSize = true,
            Font = font,
            ForeColor = Color.Black
        };
        parent.Controls.Add(label);

        textBox = new TextBox
        {
            Location = new Point(88, y),
            Size = new Size(textBoxWidth, 23),
            Font = font
        };
        parent.Controls.Add(textBox);
    }

    private static Label AddLabel(Control parent, string text, int x, int y, Font font)
    {
        var label = new Label
        {
            Text = text,
            Location = new Point(x, y + 3),
            AutoSize = true,
            Font = font,
            ForeColor = Color.Black
        };
        parent.Controls.Add(label);
        return label;
    }

    private static TextBox AddTextBox(Control parent, int x, int y, int width, Font font)
    {
        var textBox = new TextBox
        {
            Location = new Point(x, y),
            Size = new Size(width, 23),
            Font = font
        };
        parent.Controls.Add(textBox);
        return textBox;
    }
}
