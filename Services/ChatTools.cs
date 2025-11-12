using Microsoft.Data.SqlClient;
using Dapper;
using System.Text.Json;
using System.Security.Claims;

public sealed class ChatTools : IChatTools
{
    private readonly string _conn;
    public ChatTools(IConfiguration cfg) { _conn = cfg.GetConnectionString("Sql")!; }

    public async Task<string> ExecuteAsync(string tool, Dictionary<string, object> args, ClaimsPrincipal user)
    {
        // chỗ này để sau mình thêm JSON-tool nếu cần
        return JsonSerializer.Serialize(new { ok = false, msg = "tool not implemented" });
    }
}
