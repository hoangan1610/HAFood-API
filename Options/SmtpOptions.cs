using System;

namespace HAShop.Api.Options
{
    public sealed class SmtpOptions
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 587;

        // Cho trùng với "EnableSsl" trong appsettings
        public bool EnableSsl { get; set; } = true;

        public string User { get; set; } = "";
        public string Password { get; set; } = "";

        public string FromEmail { get; set; } = "";
        public string FromName { get; set; } = "HAFood";
    }
}
