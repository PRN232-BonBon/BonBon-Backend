using System.Net;
using System.Net.Mail;
using BonBon.Business.Configuration;
using BonBon.Business.Services.Interfaces;
using Microsoft.Extensions.Options;

namespace BonBon.API.Services;

/// <summary>
/// Gửi mail qua Gmail SMTP (cần App Password, xem SmtpOptions).
/// Chưa cấu hình: ở Development ghi OTP ra log để dev/test; ở môi trường khác
/// ném lỗi để PasswordResetService trả EMAIL_SEND_FAILED thay vì im lặng.
/// </summary>
public sealed class SmtpEmailSender(
    IOptions<SmtpOptions> options,
    ILogger<SmtpEmailSender> logger,
    IWebHostEnvironment env) : IEmailSender
{
    private readonly SmtpOptions _smtp = options.Value;
    private readonly ILogger<SmtpEmailSender> _logger = logger;
    private readonly IWebHostEnvironment _env = env;

    public async Task SendPasswordResetOtpAsync(string toEmail, string otp, int expiryMinutes, CancellationToken ct = default)
    {
        if (!_smtp.IsConfigured)
        {
            if (_env.IsDevelopment())
            {
                _logger.LogWarning("Smtp chưa cấu hình — OTP đặt lại mật khẩu (dev) cho {Email}: {Otp}", toEmail, otp);
                return;
            }
            throw new InvalidOperationException("Smtp is not configured.");
        }

        using var client = new SmtpClient(_smtp.Host, _smtp.Port)
        {
            EnableSsl = true,
            Credentials = new NetworkCredential(_smtp.Username, _smtp.AppPassword),
            Timeout = 15_000,
        };
        using var message = new MailMessage
        {
            From = new MailAddress(_smtp.FromAddress, _smtp.FromName),
            Subject = "[BonBon] Mã OTP đặt lại mật khẩu",
            IsBodyHtml = true,
            Body = BuildBody(otp, expiryMinutes),
        };
        message.To.Add(toEmail);

        await client.SendMailAsync(message, ct);
        _logger.LogInformation("Đã gửi OTP đặt lại mật khẩu cho {Email}", toEmail);
    }

    private static string BuildBody(string otp, int expiryMinutes) => $"""
        <div style="font-family:Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;border:1px solid #eee;border-radius:12px">
          <h2 style="color:#1b160b">Đặt lại mật khẩu BonBon</h2>
          <p>Bạn (hoặc ai đó) vừa yêu cầu đặt lại mật khẩu. Mã xác thực của bạn:</p>
          <p style="font-size:32px;font-weight:800;letter-spacing:8px;color:#b17900">{otp}</p>
          <p>Mã có hiệu lực trong <strong>{expiryMinutes} phút</strong>. Nếu bạn không yêu cầu, hãy bỏ qua email này.</p>
        </div>
        """;
}
