
using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AngleSharp;
using MailKit;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Configuration;
using MimeKit;

var configuration = new ConfigurationBuilder().AddJsonFile("appsettings.json", false).Build();
var options = configuration.Get<ClawConfiguration>();

static async ValueTask<DateOnly[]> GetTermine(string requestUrl)
{
    //Use the default configuration for AngleSharp
    var config = Configuration.Default;

    using var webCrawler = new HttpClient();
    webCrawler.DefaultRequestHeaders.Add("User-Agent", "TerminCrawler");
    var response = await webCrawler.GetAsync(requestUrl);
    using var contentStream = await response.Content.ReadAsStreamAsync();

    //Create a new context for evaluating webpages with the given config
    var context = BrowsingContext.New(config);

    //Just get the DOM representation
    var document = await context.OpenAsync(req => req.Content(contentStream));

    return document
        .QuerySelectorAll("div.j-product h4")
        .Select(e => e.TextContent)
        .Where(x => !x.Contains("Gutschein", StringComparison.InvariantCultureIgnoreCase))
        .Select(e => e.Split("|").Skip(1).Take(1).SingleOrDefault(string.Empty))
        .Select(e => e.Trim())
        .Select(e => DateOnly.ParseExact(e, "ddd, dd. MMMM yyyy", CultureInfo.CreateSpecificCulture("de-DE")))
        .ToArray();
}

static async ValueTask<DateOnly[]> GetTermineFromFile(string cacheFile)
{
    if (!File.Exists(cacheFile))
        await File.Create(cacheFile).DisposeAsync();

    return (await File
        .ReadAllLinesAsync(cacheFile))
        .Select(x => DateOnly.Parse(x))
        .ToArray();
}

static async Task UpdateCacheFile(string cacheFile, DateOnly[] termine)
{
    var strTermine = termine.Select(t => t.ToString());
    await File.WriteAllLinesAsync(cacheFile, strTermine);
}

var termine = await GetTermine(options.RequestUrl);
var storedTermine = await GetTermineFromFile(options.CacheFile);
var newTermine = termine.Except(storedTermine).ToArray();

if (newTermine.Length <= 0)
{
    Console.WriteLine("Nothing new :(");
    //    return;
}

await UpdateCacheFile(options.CacheFile, termine);

foreach (var newTermin in newTermine)
{
    Console.WriteLine($"Neuer Termin: {newTermin}");
}


var mimeMessage = CreateMessage(options.FromMailAddress, options.ToMailAddress, newTermine);

MimeMessage CreateMessage(string fromMail, string toMail, DateOnly[] termine)
{
    var contentBuilder = new StringBuilder("<h3>Es gibt neue Termine für den Barista Basic Kurs!</h3>");

    foreach (var termin in newTermine)
    {
        contentBuilder.Append("<p>📆 ");
        contentBuilder.Append(termin.ToString("dddd, dd.MM.yyyy", CultureInfo.CreateSpecificCulture("de-DE")));
        contentBuilder.Append("</p>");
    }

    contentBuilder.Append("<p>Gleich zuschlagen? 🔥<a href=\"").Append(options.RequestUrl).Append("\">Website von Kaufmanns</a> 🔥</p>");
    contentBuilder.Append("<p>Liebe Grüße vom Kaffe-Kurs-Crawler!</p>");

    var mimeMessage = new MimeMessage();
    mimeMessage.From.Add(new MailboxAddress(options.FromMailAddress.Split('@').First(), options.FromMailAddress));
    mimeMessage.To.Add(new MailboxAddress(options.ToMailAddress.Split('@').First(), options.ToMailAddress));
    mimeMessage.Subject = "Neue Barista-Kurs Termine ✨📆☕✨";

    var bodyBuilder = new BodyBuilder
    {
        HtmlBody = contentBuilder.ToString()
    };

    mimeMessage.Body = bodyBuilder.ToMessageBody();
    return mimeMessage;
}

await SendMail(options, mimeMessage);

static async ValueTask<string> SendMail(ClawConfiguration options, MimeMessage mimeMessage)
{
    var smtpClient = new SmtpClient
    {
        ServerCertificateValidationCallback = (object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) => true
    };
    await smtpClient.ConnectAsync(options.MailSmtpHost, 587, MailKit.Security.SecureSocketOptions.StartTls);
    await smtpClient.AuthenticateAsync(options.FromMailAddress, options.MailPassword);
    var result = await smtpClient.SendAsync(mimeMessage);
    await smtpClient.DisconnectAsync(true);
    return result;
}