using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Enums;
using MfaSrv.Core.Interfaces;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly ISessionManager _sessionManager;

    public SessionsController(MfaSrvDbContext db, ISessionManager sessionManager)
    {
        _db = db;
        _sessionManager = sessionManager;
    }

    [HttpGet]
    public async Task<IActionResult> GetActiveSessions([FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var query = _db.MfaSessions
            .Where(s => s.Status == SessionStatus.Active && s.ExpiresAt > DateTimeOffset.UtcNow)
            .AsNoTracking();

        var total = await query.CountAsync();
        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new
            {
                s.Id,
                s.UserId,
                s.SourceIp,
                s.TargetResource,
                s.VerifiedMethod,
                s.CreatedAt,
                s.ExpiresAt
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, data = sessions });
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> RevokeSession(string id)
    {
        await _sessionManager.RevokeSessionAsync(id);
        return NoContent();
    }
}
