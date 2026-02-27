using Microsoft.EntityFrameworkCore;
using MfaSrv.Core.Entities;
using MfaSrv.Server.Services;

namespace MfaSrv.Server.Data;

public class MfaSrvDbContext : DbContext
{
    public MfaSrvDbContext(DbContextOptions<MfaSrvDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<UserGroupMembership> UserGroupMemberships => Set<UserGroupMembership>();
    public DbSet<MfaEnrollment> MfaEnrollments => Set<MfaEnrollment>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyRuleGroup> PolicyRuleGroups => Set<PolicyRuleGroup>();
    public DbSet<PolicyRule> PolicyRules => Set<PolicyRule>();
    public DbSet<PolicyAction> PolicyActions => Set<PolicyAction>();
    public DbSet<MfaSession> MfaSessions => Set<MfaSession>();
    public DbSet<MfaChallenge> MfaChallenges => Set<MfaChallenge>();
    public DbSet<AuditLogEntry> AuditLog => Set<AuditLogEntry>();
    public DbSet<AgentRegistration> AgentRegistrations => Set<AgentRegistration>();
    public DbSet<LeaderLease> LeaderLeases => Set<LeaderLease>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ObjectGuid).IsUnique();
            e.HasIndex(x => x.SamAccountName);
            e.HasIndex(x => x.UserPrincipalName);
            e.Property(x => x.SamAccountName).HasMaxLength(256);
            e.Property(x => x.UserPrincipalName).HasMaxLength(512);
            e.Property(x => x.DisplayName).HasMaxLength(512);
            e.Property(x => x.Email).HasMaxLength(512);
            e.Property(x => x.DistinguishedName).HasMaxLength(2048);
        });

        modelBuilder.Entity<UserGroupMembership>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.GroupSid }).IsUnique();
            e.HasOne(x => x.User).WithMany(u => u.GroupMemberships).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.GroupSid).HasMaxLength(256);
            e.Property(x => x.GroupName).HasMaxLength(512);
        });

        modelBuilder.Entity<MfaEnrollment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Method });
            e.HasOne(x => x.User).WithMany(u => u.Enrollments).HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Policy>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Priority);
            e.Property(x => x.Name).HasMaxLength(256);
        });

        modelBuilder.Entity<PolicyRuleGroup>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Policy).WithMany(p => p.RuleGroups).HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PolicyRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.RuleGroup).WithMany(g => g.Rules).HasForeignKey(x => x.RuleGroupId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Value).HasMaxLength(1024);
            e.Property(x => x.Operator).HasMaxLength(64);
        });

        modelBuilder.Entity<PolicyAction>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne(x => x.Policy).WithMany(p => p.Actions).HasForeignKey(x => x.PolicyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MfaSession>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasIndex(x => new { x.UserId, x.SourceIp, x.Status });
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MfaChallenge>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
        });

        modelBuilder.Entity<AuditLogEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.EventType);
            e.Property(x => x.Details).HasMaxLength(4096);
        });

        modelBuilder.Entity<AgentRegistration>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Hostname);
            e.Property(x => x.Hostname).HasMaxLength(256);
        });

        modelBuilder.Entity<LeaderLease>(e =>
        {
            e.HasKey(x => x.LeaseKey);
            e.Property(x => x.LeaseKey).HasMaxLength(64);
            e.Property(x => x.HolderId).HasMaxLength(256);
        });
    }
}
