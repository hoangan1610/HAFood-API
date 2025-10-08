using Microsoft.EntityFrameworkCore;

namespace HAShop.Api.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    // (Tuỳ chọn) Khai báo DbSet nếu cần dùng EF cho bảng nào đó
    // public DbSet<Product> Products => Set<Product>();
    // public DbSet<UserInfo> UserInfos => Set<UserInfo>();
}
