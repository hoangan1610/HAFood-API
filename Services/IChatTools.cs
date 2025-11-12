using System.Security.Claims;
using System.Collections.Generic;
using System.Threading.Tasks;

public interface IChatTools
{
    Task<string> ExecuteAsync(string tool, Dictionary<string, object> args, ClaimsPrincipal user);
}
