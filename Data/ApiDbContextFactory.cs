using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MonitoringApi.Data;

public sealed class ApiDbContextFactory : IDesignTimeDbContextFactory<ApiDbContext>
{
    public ApiDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<ApiDbContext>()
        .UseSqlite("Data Source=monitoring.db;Foreign Keys=True").Options);
}
