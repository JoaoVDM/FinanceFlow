using Microsoft.EntityFrameworkCore;

namespace FinanceFlow.Infrastructure.Persistence.Migrations;

// Frozen initial schema, shared with the initial migration target. Do not change when evolving the runtime model.
internal static class InitialIdentityModel
{
    internal static void Build(ModelBuilder model)
    {
        model.HasAnnotation("ProductVersion", "10.0.13").HasAnnotation("Relational:MaxIdentifierLength", 128);
        model.UseIdentityColumns();
        const string userType = "FinanceFlow.Infrastructure.Identity.ApplicationUser";
        const string roleType = "Microsoft.AspNetCore.Identity.IdentityRole<System.Guid>";
        const string claimType = "Microsoft.AspNetCore.Identity.IdentityUserClaim<System.Guid>";
        const string roleClaimType = "Microsoft.AspNetCore.Identity.IdentityRoleClaim<System.Guid>";
        const string loginType = "Microsoft.AspNetCore.Identity.IdentityUserLogin<System.Guid>";
        const string tokenType = "Microsoft.AspNetCore.Identity.IdentityUserToken<System.Guid>";
        const string userRoleType = "Microsoft.AspNetCore.Identity.IdentityUserRole<System.Guid>";
        model.Entity(userType, user =>
        {
            user.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uniqueidentifier");
            user.Property<string>("DisplayName").IsRequired().HasMaxLength(100).HasColumnType("nvarchar(100)");
            foreach (var name in new[] { "Email", "NormalizedEmail", "UserName", "NormalizedUserName" })
                user.Property<string>(name).IsRequired(name is "Email" or "NormalizedEmail").HasMaxLength(256).HasColumnType("nvarchar(256)");
            foreach (var name in new[] { "PasswordHash", "SecurityStamp", "ConcurrencyStamp", "PhoneNumber" })
                user.Property<string>(name).HasColumnType("nvarchar(max)");
            user.Property<string>("ConcurrencyStamp").IsConcurrencyToken();
            foreach (var name in new[] { "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "IsActive" })
                user.Property<bool>(name).HasColumnType("bit");
            user.Property<int>("AccessFailedCount").HasColumnType("int");
            user.Property<long>("TokenVersion").IsConcurrencyToken().HasColumnType("bigint");
            user.Property<DateTimeOffset>("CreatedAtUtc").HasColumnType("datetimeoffset");
            foreach (var name in new[] { "LockoutEnd", "UpdatedAtUtc", "DeactivatedAtUtc" })
                user.Property<DateTimeOffset?>(name).HasColumnType("datetimeoffset");
            user.HasKey("Id");
            user.HasIndex("NormalizedEmail").IsUnique().HasDatabaseName("EmailIndex");
            user.HasIndex("NormalizedUserName").IsUnique().HasDatabaseName("UserNameIndex").HasFilter("[NormalizedUserName] IS NOT NULL");
            user.ToTable("AspNetUsers", table =>
            {
                table.HasCheckConstraint("CK_AspNetUsers_DisplayName", "LEN(LTRIM(RTRIM([DisplayName]))) > 0");
                table.HasCheckConstraint("CK_AspNetUsers_TokenVersion", "[TokenVersion] >= 0");
            });
        });
        model.Entity(roleType, role =>
        {
            role.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("uniqueidentifier");
            role.Property<string>("Name").HasMaxLength(256).HasColumnType("nvarchar(256)");
            role.Property<string>("NormalizedName").HasMaxLength(256).HasColumnType("nvarchar(256)");
            role.Property<string>("ConcurrencyStamp").IsConcurrencyToken().HasColumnType("nvarchar(max)");
            role.HasKey("Id"); role.HasIndex("NormalizedName").IsUnique().HasDatabaseName("RoleNameIndex").HasFilter("[NormalizedName] IS NOT NULL");
            role.ToTable("AspNetRoles");
        });
        foreach (var (type, foreignKey, table) in new[] { (claimType, "UserId", "AspNetUserClaims"), (roleClaimType, "RoleId", "AspNetRoleClaims") })
            model.Entity(type, claim =>
            {
                claim.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("int").UseIdentityColumn();
                claim.Property<Guid>(foreignKey).HasColumnType("uniqueidentifier");
                claim.Property<string>("ClaimType").HasColumnType("nvarchar(max)");
                claim.Property<string>("ClaimValue").HasColumnType("nvarchar(max)");
                claim.HasKey("Id"); claim.HasIndex(foreignKey); claim.ToTable(table);
            });
        model.Entity(loginType, login =>
        {
            login.Property<string>("LoginProvider").HasMaxLength(128).HasColumnType("nvarchar(128)");
            login.Property<string>("ProviderKey").HasMaxLength(128).HasColumnType("nvarchar(128)");
            login.Property<string>("ProviderDisplayName").HasColumnType("nvarchar(max)");
            login.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            login.HasKey("LoginProvider", "ProviderKey"); login.HasIndex("UserId"); login.ToTable("AspNetUserLogins");
        });
        model.Entity(tokenType, token =>
        {
            token.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            token.Property<string>("LoginProvider").HasMaxLength(128).HasColumnType("nvarchar(128)");
            token.Property<string>("Name").HasMaxLength(128).HasColumnType("nvarchar(128)");
            token.Property<string>("Value").HasColumnType("nvarchar(max)");
            token.HasKey("UserId", "LoginProvider", "Name"); token.ToTable("AspNetUserTokens");
        });
        model.Entity(userRoleType, role =>
        {
            role.Property<Guid>("UserId").HasColumnType("uniqueidentifier");
            role.Property<Guid>("RoleId").HasColumnType("uniqueidentifier");
            role.HasKey("UserId", "RoleId"); role.HasIndex("RoleId"); role.ToTable("AspNetUserRoles");
        });
        foreach (var type in new[] { claimType, loginType, tokenType, userRoleType })
            model.Entity(type).HasOne(userType, null).WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade).IsRequired();
        foreach (var type in new[] { roleClaimType, userRoleType })
            model.Entity(type).HasOne(roleType, null).WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade).IsRequired();
    }
}
