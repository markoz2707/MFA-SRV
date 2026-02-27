using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MfaSrv.Core.ValueObjects;

namespace MfaSrv.DcAgent.Services;

public class NamedPipeServer : BackgroundService
{
    private readonly AuthDecisionService _authDecision;
    private readonly DcAgentSettings _settings;
    private readonly ILogger<NamedPipeServer> _logger;

    public NamedPipeServer(
        AuthDecisionService authDecision,
        IOptions<DcAgentSettings> settings,
        ILogger<NamedPipeServer> logger)
    {
        _authDecision = authDecision;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Named pipe server starting on \\\\.\\pipe\\{PipeName}", _settings.PipeName);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var pipeSecurity = new PipeSecurity();
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));
                pipeSecurity.AddAccessRule(new PipeAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    PipeAccessRights.ReadWrite,
                    AccessControlType.Allow));

                var pipeServer = NamedPipeServerStreamAcl.Create(
                    _settings.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous,
                    4096, 4096,
                    pipeSecurity);

                await pipeServer.WaitForConnectionAsync(stoppingToken);

                // Handle each connection in a separate task
                _ = HandleConnectionAsync(pipeServer, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in named pipe server loop");
                await Task.Delay(1000, stoppingToken);
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using (pipe)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(_settings.PipeTimeoutMs);

                // Read the query
                var buffer = new byte[4096];
                var bytesRead = await pipe.ReadAsync(buffer, cts.Token);

                if (bytesRead == 0) return;

                var json = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var query = JsonSerializer.Deserialize<AuthQueryMessage>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (query == null)
                {
                    _logger.LogWarning("Received invalid query from named pipe");
                    return;
                }

                _logger.LogDebug("Auth query: {User}@{Domain} from {Ip}", query.UserName, query.Domain, query.SourceIp);

                // Get decision
                var response = await _authDecision.EvaluateAsync(query, cts.Token);

                // Send response
                var responseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
                var responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await pipe.WriteAsync(responseBytes, cts.Token);
                await pipe.FlushAsync(cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Named pipe connection timed out");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling named pipe connection");
        }
    }
}
