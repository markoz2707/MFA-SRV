using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Interfaces;
using MfaSrv.Server.Data;

namespace MfaSrv.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly MfaSrvDbContext _db;
    private readonly IUserSyncService _userSyncService;

    public UsersController(MfaSrvDbContext db, IUserSyncService userSyncService)
    {
        _db = db;
        _userSyncService = userSyncService;
    }

    [HttpGet]
    public async Task<IActionResult> GetUsers([FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? search = null)
    {
        var query = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(u =>
                u.SamAccountName.Contains(search) ||
                u.DisplayName.Contains(search) ||
                u.UserPrincipalName.Contains(search));
        }

        var total = await query.CountAsync();
        var users = await query
            .OrderBy(u => u.DisplayName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(u => new
            {
                u.Id,
                u.SamAccountName,
                u.UserPrincipalName,
                u.DisplayName,
                u.Email,
                u.IsEnabled,
                u.MfaEnabled,
                u.LastAuthAt,
                u.LastSyncAt,
                EnrollmentCount = u.Enrollments.Count(e => e.Status == Core.Enums.EnrollmentStatus.Active)
            })
            .ToListAsync();

        return Ok(new { total, page, pageSize, data = users });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetUser(string id)
    {
        var user = await _db.Users
            .Include(u => u.Enrollments)
            .Include(u => u.GroupMemberships)
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == id);

        if (user == null) return NotFound();
        return Ok(user);
    }

    [HttpPost("sync")]
    public async Task<IActionResult> TriggerSync()
    {
        await _userSyncService.SyncUsersAsync();
        return Ok(new { message = "Sync completed" });
    }

    [HttpGet("sync/test")]
    public async Task<IActionResult> TestLdapConnection()
    {
        var result = await _userSyncService.TestConnectionAsync();
        return Ok(new { connected = result });
    }
}
