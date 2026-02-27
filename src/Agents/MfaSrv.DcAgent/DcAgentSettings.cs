namespace MfaSrv.DcAgent;

public class DcAgentSettings
{
    public string CentralServerUrl { get; set; } = "https://mfasrv-server:5081";
    public string AgentId { get; set; } = string.Empty;
    public string PipeName { get; set; } = "MfaSrvDcAgent";
    public int PipeTimeoutMs { get; set; } = 3000;
    public int HeartbeatIntervalSeconds { get; set; } = 30;
    public int PolicySyncIntervalSeconds { get; set; } = 60;
    public int SessionTtlMinutes { get; set; } = 480; // 8 hours
    public string CertificatePath { get; set; } = string.Empty;
    public string CertificatePassword { get; set; } = string.Empty;
    public string[] GossipPeers { get; set; } = Array.Empty<string>();
    public string FailoverMode { get; set; } = "FailOpen";
    public string CacheDbPath { get; set; } = "dcagent_cache.db";
}
