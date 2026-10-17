using System.Security.Cryptography.X509Certificates;
using MailKit.Net.Imap;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

var protocol = Required("PROXY_E2E_DRIVER_PROTOCOL");
var host = Required("PROXY_E2E_DRIVER_HOST");
var port = int.Parse(Required("PROXY_E2E_DRIVER_PORT"), System.Globalization.CultureInfo.InvariantCulture);
var login = Required("PROXY_E2E_LOGIN");
var password = Required("PROXY_E2E_CLIENT_PASSWORD");
var hold = args.Contains("--hold", StringComparer.Ordinal);

try
{
    switch (protocol)
    {
        case "imap":
            await RunImapAsync(host, port, login, password, hold);
            break;
        case "smtp":
            await RunSmtpAsync(host, port, login, password, hold);
            break;
        default:
            throw new InvalidOperationException("Unsupported proxy harness protocol.");
    }
}
catch (Exception exception)
{
    var detail = exception.Message.Replace('\r', ' ').Replace('\n', ' ');
    detail = detail.Length > 160 ? detail[..160] : detail;
    Console.Error.WriteLine($"{protocol.ToUpperInvariant()} client failed: {exception.GetType().Name}: {detail}");
    return 1;
}

return 0;

static async Task RunImapAsync(string host, int port, string login, string password, bool hold)
{
    using var client = new ImapClient();
    await client.ConnectAsync(host, port, SecureSocketOptions.None);
    await client.AuthenticateAsync(login, password);
    _ = await client.GetFolderAsync("INBOX");
    Console.WriteLine("IMAP client authenticated and listed INBOX.");
    if (hold)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    await client.DisconnectAsync(true);
}

static async Task RunSmtpAsync(string host, int port, string login, string password, bool hold)
{
    var certificatePath = Required("PROXY_E2E_DRIVER_HARNESS_CERT");
    using var expected = X509Certificate2.CreateFromPemFile(certificatePath);
    using var client = new SmtpClient();
    client.ServerCertificateValidationCallback = (_, certificate, _, _) =>
        certificate is not null && certificate.GetCertHashString() == expected.GetCertHashString();
    await client.ConnectAsync(host, port, SecureSocketOptions.StartTls);
    await client.AuthenticateAsync(login, password);
    var message = new MimeMessage();
    message.From.Add(new MailboxAddress("Proxy Harness", "harness@example.test"));
    message.To.Add(new MailboxAddress("Backend Harness", "backend@example.test"));
    message.Subject = "Proxy harness SMTP proof";
    message.Body = new TextPart("plain") { Text = "Harness submission proof." };
    await client.SendAsync(message);
    Console.WriteLine("SMTP client completed STARTTLS, authenticated, and submitted one message.");
    if (hold)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    await client.DisconnectAsync(true);
}

static string Required(string name)
    => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"Required driver environment is absent: {name}.");
