namespace MfaSrv.EndpointAgent;

public class EndpointAgentSettings
{
    public string CentralServerUrl { get; set; } = "https://mfasrv-server:5081";
    public string AgentId { get; set; } = string.Empty;
    public string PipeName { get; set; } = "MfaSrvEndpointAgent";
    public int PipeTimeoutMs { get; set; } = 3000;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int SessionTtlMinutes { get; set; } = 480;
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public string FailoverMode { get; set; } = "FailOpen";
    public string Hostname { get; set; } = Environment.MachineName;
}
