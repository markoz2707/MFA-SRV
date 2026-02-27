using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AgentsController : ControllerBase
{
    private readonly MfaSrvDbContext _db;

    public AgentsController(MfaSrvDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<IActionResult> GetAgents()
    {
        var agents = await _db.AgentRegistrations
            .OrderBy(a => a.Hostname)
            .AsNoTracking()
            .ToListAsync();

        return Ok(agents);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetAgent(string id)
    {
        var agent = await _db.AgentRegistrations.FindAsync(id);
        if (agent == null) return NotFound();
        return Ok(agent);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> RemoveAgent(string id)
    {
        var agent = await _db.AgentRegistrations.FindAsync(id);
        if (agent == null) return NotFound();
        _db.AgentRegistrations.Remove(agent);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
