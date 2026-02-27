using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Entities;
using MfaSrv.Core.Enums;
using MfaSrv.Server.Data;
using MfaSrv.Server.Services;
using System.Text.Json;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PoliciesController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly PolicySyncStreamService _policySyncStream;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public PoliciesController(MfaSrvDbContext db, PolicySyncStreamService policySyncStream)
    {
        _db = db;
        _policySyncStream = policySyncStream;
    }

    [HttpGet]
    public async Task<IActionResult> GetPolicies()
    {
        var policies = await _db.Policies
            .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
            .Include(p => p.Actions)
            .OrderBy(p => p.Priority)
            .AsNoTracking()
            .ToListAsync();

        return Ok(policies);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetPolicy(string id)
    {
        var policy = await _db.Policies
            .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
            .Include(p => p.Actions)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id);

        if (policy == null) return NotFound();
        return Ok(policy);
    }

    [HttpPost]
    public async Task<IActionResult> CreatePolicy([FromBody] CreatePolicyRequest request)
    {
        var policy = new Policy
        {
            Name = request.Name,
            Description = request.Description,
            IsEnabled = request.IsEnabled,
            Priority = request.Priority,
            FailoverMode = request.FailoverMode
        };

        if (request.RuleGroups != null)
        {
            foreach (var rg in request.RuleGroups)
            {
                var group = new PolicyRuleGroup { PolicyId = policy.Id, Order = rg.Order };
                if (rg.Rules != null)
                {
                    foreach (var r in rg.Rules)
                    {
                        group.Rules.Add(new PolicyRule
                        {
                            RuleGroupId = group.Id,
                            RuleType = r.RuleType,
                            Operator = r.Operator,
                            Value = r.Value,
                            Negate = r.Negate
                        });
                    }
                }
                policy.RuleGroups.Add(group);
            }
        }

        if (request.Actions != null)
        {
            foreach (var a in request.Actions)
            {
                policy.Actions.Add(new PolicyAction
                {
                    PolicyId = policy.Id,
                    ActionType = a.ActionType,
                    RequiredMethod = a.RequiredMethod
                });
            }
        }

        _db.Policies.Add(policy);
        await _db.SaveChangesAsync();

        // Notify connected agents of the new policy
        if (policy.IsEnabled)
        {
            var policyJson = JsonSerializer.Serialize(policy, _jsonOptions);
            await _policySyncStream.NotifyPolicyChangeAsync(policy.Id, policyJson, deleted: false, policy.UpdatedAt);
        }

        return CreatedAtAction(nameof(GetPolicy), new { id = policy.Id }, policy);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdatePolicy(string id, [FromBody] CreatePolicyRequest request)
    {
        var policy = await _db.Policies
            .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
            .Include(p => p.Actions)
            .FirstOrDefaultAsync(p => p.Id == id);

        if (policy == null) return NotFound();

        policy.Name = request.Name;
        policy.Description = request.Description;
        policy.IsEnabled = request.IsEnabled;
        policy.Priority = request.Priority;
        policy.FailoverMode = request.FailoverMode;
        policy.UpdatedAt = DateTimeOffset.UtcNow;

        // Replace rule groups and actions
        _db.PolicyRules.RemoveRange(policy.RuleGroups.SelectMany(g => g.Rules));
        _db.PolicyRuleGroups.RemoveRange(policy.RuleGroups);
        _db.PolicyActions.RemoveRange(policy.Actions);
        policy.RuleGroups.Clear();
        policy.Actions.Clear();

        if (request.RuleGroups != null)
        {
            foreach (var rg in request.RuleGroups)
            {
                var group = new PolicyRuleGroup { PolicyId = policy.Id, Order = rg.Order };
                if (rg.Rules != null)
                {
                    foreach (var r in rg.Rules)
                    {
                        group.Rules.Add(new PolicyRule
                        {
                            RuleGroupId = group.Id,
                            RuleType = r.RuleType,
                            Operator = r.Operator,
                            Value = r.Value,
                            Negate = r.Negate
                        });
                    }
                }
                policy.RuleGroups.Add(group);
            }
        }

        if (request.Actions != null)
        {
            foreach (var a in request.Actions)
            {
                policy.Actions.Add(new PolicyAction
                {
                    PolicyId = policy.Id,
                    ActionType = a.ActionType,
                    RequiredMethod = a.RequiredMethod
                });
            }
        }

        await _db.SaveChangesAsync();

        // Notify connected agents of the policy update (or removal if disabled)
        var updatedPolicyJson = JsonSerializer.Serialize(policy, _jsonOptions);
        await _policySyncStream.NotifyPolicyChangeAsync(
            policy.Id, updatedPolicyJson, deleted: !policy.IsEnabled, policy.UpdatedAt);

        return Ok(policy);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeletePolicy(string id)
    {
        var policy = await _db.Policies.FindAsync(id);
        if (policy == null) return NotFound();

        _db.Policies.Remove(policy);
        await _db.SaveChangesAsync();

        // Notify connected agents of the policy deletion
        await _policySyncStream.NotifyPolicyChangeAsync(
            id, string.Empty, deleted: true, DateTimeOffset.UtcNow);

        return NoContent();
    }

    [HttpPatch("{id}/toggle")]
    public async Task<IActionResult> TogglePolicy(string id)
    {
        var policy = await _db.Policies.FindAsync(id);
        if (policy == null) return NotFound();

        policy.IsEnabled = !policy.IsEnabled;
        policy.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        // Notify agents: if disabled, send as deleted; if re-enabled, send full policy
        if (policy.IsEnabled)
        {
            // Reload with navigation properties for a complete JSON payload
            var fullPolicy = await _db.Policies
                .Include(p => p.RuleGroups).ThenInclude(g => g.Rules)
                .Include(p => p.Actions)
                .AsNoTracking()
                .FirstAsync(p => p.Id == id);

            var policyJson = JsonSerializer.Serialize(fullPolicy, _jsonOptions);
            await _policySyncStream.NotifyPolicyChangeAsync(id, policyJson, deleted: false, policy.UpdatedAt);
        }
        else
        {
            await _policySyncStream.NotifyPolicyChangeAsync(id, string.Empty, deleted: true, policy.UpdatedAt);
        }

        return Ok(new { policy.Id, policy.IsEnabled });
    }
}

public record CreatePolicyRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int Priority { get; init; }
    public FailoverMode FailoverMode { get; init; }
    public List<RuleGroupRequest>? RuleGroups { get; init; }
    public List<ActionRequest>? Actions { get; init; }
}

public record RuleGroupRequest
{
    public int Order { get; init; }
    public List<RuleRequest>? Rules { get; init; }
}

public record RuleRequest
{
    public PolicyRuleType RuleType { get; init; }
    public string Operator { get; init; } = "Equals";
    public string Value { get; init; } = string.Empty;
    public bool Negate { get; init; }
}

public record ActionRequest
{
    public PolicyActionType ActionType { get; init; }
    public MfaMethod? RequiredMethod { get; init; }
}
