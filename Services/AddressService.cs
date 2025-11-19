using System.Data;
using System.Data.Common;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services;

public interface IAddressService
{
    Task<IReadOnlyList<AddressDto>> ListAsync(long userId, bool onlyActive, CancellationToken ct = default);

    Task<AddressDto> AddAsync(long userId, AddressCreateRequest req, CancellationToken ct = default);

    Task<AddressDto> UpdateAsync(long userId, long addressId, AddressUpdateRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<AddressDto>> DeleteSoftAsync(long userId, long addressId, CancellationToken ct = default);

    Task<AddressDto> SetDefaultAsync(long userId, long addressId, CancellationToken ct = default);
}

public sealed class AddressService(ISqlConnectionFactory dbFactory, ILogger<AddressService> logger) : IAddressService
{
    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public async Task<IReadOnlyList<AddressDto>> ListAsync(long userId, bool onlyActive, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        var p = new DynamicParameters();
        p.Add("@user_info_id", userId, DbType.Int64);
        p.Add("@only_active", onlyActive, DbType.Boolean);

        var rows = await con.QueryAsync<AddressDto>(
            new CommandDefinition("dbo.usp_address_list_by_user", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<AddressDto> AddAsync(long userId, AddressCreateRequest req, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        // (tuỳ chính sách) đặt session user_id cho thống nhất logging/audit
        await con.ExecuteAsync(new CommandDefinition(
            "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
            new { v = userId }, commandType: CommandType.Text, cancellationToken: ct));

        var p = new DynamicParameters();
        p.Add("@user_info_id", userId, DbType.Int64);
        p.Add("@type", req.Type, DbType.Byte);
        p.Add("@label", Norm(req.Label), DbType.String);
        p.Add("@is_default", req.IsDefault, DbType.Boolean);
        p.Add("@fullname", Norm(req.FullName), DbType.String);
        p.Add("@phone", Norm(req.Phone), DbType.String);
        p.Add("@full_address", Norm(req.FullAddress), DbType.String);
        p.Add("@status", (byte)1, DbType.Byte);
        p.Add("@city_code", Norm(req.CityCode), DbType.String);
        p.Add("@city_name", Norm(req.CityName), DbType.String);
        p.Add("@ward_code", Norm(req.WardCode), DbType.String);
        p.Add("@ward_name", Norm(req.WardName), DbType.String);


        try
        {
            var row = await con.QuerySingleAsync<AddressDto>(new CommandDefinition(
                "dbo.usp_address_add", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return row;
        }
        catch (SqlException ex) when (ex.Number is 50401 or 50402 or 50403 or 50404)
        {
            logger.LogWarning(ex, "Add address failed for {UserId}", userId);
            throw;
        }
    }

    public async Task<AddressDto> UpdateAsync(long userId, long addressId, AddressUpdateRequest req, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        await con.ExecuteAsync(new CommandDefinition(
            "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
            new { v = userId }, commandType: CommandType.Text, cancellationToken: ct));

        var p = new DynamicParameters();
        p.Add("@id", addressId, DbType.Int64);
        p.Add("@user_info_id", userId, DbType.Int64);
        p.Add("@type", req.Type, DbType.Byte);
        p.Add("@label", Norm(req.Label), DbType.String);
        p.Add("@is_default", req.IsDefault, DbType.Boolean);
        p.Add("@fullname", Norm(req.FullName), DbType.String);
        p.Add("@phone", Norm(req.Phone), DbType.String);
        p.Add("@full_address", Norm(req.FullAddress), DbType.String);
        p.Add("@status", req.Status, DbType.Byte);
        p.Add("@city_code", Norm(req.CityCode), DbType.String);
        p.Add("@city_name", Norm(req.CityName), DbType.String);
        p.Add("@ward_code", Norm(req.WardCode), DbType.String);
        p.Add("@ward_name", Norm(req.WardName), DbType.String);


        try
        {
            var row = await con.QuerySingleAsync<AddressDto>(new CommandDefinition(
                "dbo.usp_address_update", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return row;
        }
        catch (SqlException ex) when (ex.Number == 50401) // ADDRESS_NOT_FOUND
        {
            throw;
        }
    }

    public async Task<IReadOnlyList<AddressDto>> DeleteSoftAsync(long userId, long addressId, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        await con.ExecuteAsync(new CommandDefinition(
            "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
            new { v = userId }, commandType: CommandType.Text, cancellationToken: ct));

        var p = new DynamicParameters();
        p.Add("@id", addressId, DbType.Int64);
        p.Add("@user_info_id", userId, DbType.Int64);

        // SP trả về danh sách active còn lại
        var rows = await con.QueryAsync<AddressDto>(new CommandDefinition(
            "dbo.usp_address_delete_soft", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return rows.ToList();
    }

    public async Task<AddressDto> SetDefaultAsync(long userId, long addressId, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();
        await ((DbConnection)con).OpenAsync(ct);

        await con.ExecuteAsync(new CommandDefinition(
            "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
            new { v = userId }, commandType: CommandType.Text, cancellationToken: ct));

        var p = new DynamicParameters();
        p.Add("@id", addressId, DbType.Int64);
        p.Add("@user_info_id", userId, DbType.Int64);

        try
        {
            var row = await con.QuerySingleAsync<AddressDto>(new CommandDefinition(
                "dbo.usp_address_set_default", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return row;
        }
        catch (SqlException ex) when (ex.Number == 50401) // ADDRESS_NOT_FOUND_OR_INACTIVE
        {
            throw;
        }
    }
}
