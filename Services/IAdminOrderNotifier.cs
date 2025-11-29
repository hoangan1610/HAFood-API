using System;
using System.Threading;
using System.Threading.Tasks;

namespace HAShop.Api.Services
{
    public interface IAdminOrderNotifier
    {
        /// <summary>
        /// Thông báo cho admin khi có đơn mới.
        /// </summary>
        Task NotifyNewOrderAsync(
            long orderId,
            string orderCode,
            decimal payTotal,
            string shipName,
            string shipPhone,
            string? shipAddressShort,
            byte? paymentMethod,
            DateTime? placedAt,
            CancellationToken ct);
    }
}
