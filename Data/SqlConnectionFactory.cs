using Microsoft.Data.SqlClient;
using System.Data;

namespace HAShop.Api.Data;

public interface ISqlConnectionFactory
{
    IDbConnection Create();
}

public class SqlConnectionFactory(IConfiguration config) : ISqlConnectionFactory
{
    public IDbConnection Create()
        => new SqlConnection(config.GetConnectionString("Default"));
}
