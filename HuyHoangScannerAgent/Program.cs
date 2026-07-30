using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ScannerAgentOptions>(builder.Configuration.GetSection("ScannerAgent"));
builder.Services.AddCors();
builder.Services.AddHttpClient();

var options = builder.Configuration.GetSection("ScannerAgent").Get<ScannerAgentOptions>() ?? new ScannerAgentOptions();
builder.WebHost.UseUrls($"http://127.0.0.1:{options.Port}");

var app = builder.Build();

app.UseCors(policy =>
{
    policy
        .SetIsOriginAllowed(origin => IsAllowedOrigin(origin, app.Services.GetRequiredService<IOptions<ScannerAgentOptions>>().Value))
        .AllowAnyHeader()
        .AllowAnyMethod()
        .WithExposedHeaders("Content-Disposition");
});

app.MapGet("/", (IOptions<ScannerAgentOptions> settings) => Results.Json(new
{
    name = "Huy Hoang Scanner Agent",
    status = "running",
    scanFolder = settings.Value.ScanFolder
}));

app.MapGet("/health", () => Results.Json(new { ok = true }));

app.MapGet("/latest", (IOptions<ScannerAgentOptions> settings) =>
{
    var latest = FindLatestScan(settings.Value);

    if (latest == null)
    {
        return Results.NotFound(new
        {
            ok = false,
            message = $"No scan file found in {settings.Value.ScanFolder}."
        });
    }

    return Results.Json(ToScanFileResponse(latest));
});

app.MapGet("/download-latest", (IOptions<ScannerAgentOptions> settings) =>
{
    var latest = FindLatestScan(settings.Value);

    if (latest == null)
    {
        return Results.NotFound(new
        {
            ok = false,
            message = $"No scan file found in {settings.Value.ScanFolder}."
        });
    }

    return Results.File(latest.FullName, GetContentType(latest.Extension), latest.Name);
});

app.MapPost("/scan", async (
    ScanRequest request,
    IOptions<ScannerAgentOptions> settings,
    IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.UploadUrl) || !Uri.TryCreate(request.UploadUrl, UriKind.Absolute, out var uploadUri))
    {
        return Results.BadRequest(new { ok = false, message = "uploadUrl is required." });
    }

    FileInfo scannedFile;

    try
    {
        scannedFile = ScanWithWia(settings.Value);
    }
    catch (Exception exception)
    {
        return Results.Problem(
            title: "Scanner failed",
            detail: exception.Message,
            statusCode: 500);
    }

    var uploadResult = await UploadScan(scannedFile, uploadUri, request.FieldName ?? settings.Value.UploadFieldName, httpClientFactory);

    return Results.Json(new
    {
        ok = uploadResult.IsSuccessStatusCode,
        statusCode = (int) uploadResult.StatusCode,
        file = ToScanFileResponse(scannedFile),
        response = uploadResult.ResponseText,
        path = uploadResult.UploadedPath,
        url = uploadResult.UploadedUrl,
        name = uploadResult.UploadedName ?? scannedFile.Name
    }, statusCode: uploadResult.IsSuccessStatusCode ? 200 : 502);
});

app.MapPost("/upload-latest", async (
    UploadLatestRequest request,
    IOptions<ScannerAgentOptions> settings,
    IHttpClientFactory httpClientFactory) =>
{
    if (string.IsNullOrWhiteSpace(request.UploadUrl) || !Uri.TryCreate(request.UploadUrl, UriKind.Absolute, out var uploadUri))
    {
        return Results.BadRequest(new { ok = false, message = "uploadUrl is required." });
    }

    var latest = FindLatestScan(settings.Value);

    if (latest == null)
    {
        return Results.NotFound(new
        {
            ok = false,
            message = $"No scan file found in {settings.Value.ScanFolder}."
        });
    }

    var uploadResult = await UploadScan(latest, uploadUri, request.FieldName ?? settings.Value.UploadFieldName, httpClientFactory);

    return Results.Json(new
    {
        ok = uploadResult.IsSuccessStatusCode,
        statusCode = (int) uploadResult.StatusCode,
        file = ToScanFileResponse(latest),
        response = uploadResult.ResponseText
    }, statusCode: uploadResult.IsSuccessStatusCode ? 200 : 502);
});

app.Run();

static FileInfo ScanWithWia(ScannerAgentOptions options)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new InvalidOperationException("Scanner control is only available on Windows.");
    }

    FileInfo? scannedFile = null;
    Exception? scanException = null;

    var thread = new Thread(() =>
    {
        try
        {
            scannedFile = ScanWithWiaOnStaThread(options);
        }
        catch (Exception exception)
        {
            scanException = exception;
        }
    });

    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();

    if (scanException != null)
    {
        throw scanException;
    }

    return scannedFile ?? throw new InvalidOperationException("Scanner did not return a file.");
}

static FileInfo ScanWithWiaOnStaThread(ScannerAgentOptions options)
{
    Directory.CreateDirectory(options.ScanFolder);

    var fileName = $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.jpg";
    var filePath = Path.Combine(options.ScanFolder, fileName);

    var commonDialogType = Type.GetTypeFromProgID("WIA.CommonDialog");

    if (commonDialogType == null)
    {
        throw new InvalidOperationException("Windows Image Acquisition is not available on this computer.");
    }

    dynamic commonDialog = Activator.CreateInstance(commonDialogType)
        ?? throw new InvalidOperationException("Could not open Windows scanner dialog.");

    // WIA constants: 1 = scanner device, color intent. JPEG format ID is WIA's built-in image format.
    const int scannerDeviceType = 1;
    const int colorIntent = 1;
    const int noBias = 0;
    const string jpegFormatId = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    dynamic imageFile = commonDialog.ShowAcquireImage(
        scannerDeviceType,
        colorIntent,
        noBias,
        jpegFormatId,
        options.AlwaysSelectScanner,
        options.ShowScannerUi,
        true);

    imageFile.SaveFile(filePath);

    return new FileInfo(filePath);
}

static async Task<UploadResult> UploadScan(
    FileInfo scanFile,
    Uri uploadUri,
    string fieldName,
    IHttpClientFactory httpClientFactory)
{
    await using var stream = File.OpenRead(scanFile.FullName);
    using var form = new MultipartFormDataContent();
    using var fileContent = new StreamContent(stream);
    fileContent.Headers.ContentType = new MediaTypeHeaderValue(GetContentType(scanFile.Extension));
    form.Add(fileContent, fieldName, scanFile.Name);

    var client = httpClientFactory.CreateClient();

    var response = await client.PostAsync(uploadUri, form);
    var responseText = await response.Content.ReadAsStringAsync();
    string? uploadedPath = null;
    string? uploadedUrl = null;
    string? uploadedName = null;

    if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(responseText))
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(responseText);
            uploadedPath = json.RootElement.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;
            uploadedUrl = json.RootElement.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            uploadedName = json.RootElement.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
        }
        catch
        {
            // Keep the raw response for troubleshooting if Laravel returns non-JSON.
        }
    }

    return new UploadResult(response.IsSuccessStatusCode, response.StatusCode, responseText, uploadedPath, uploadedUrl, uploadedName);
}

static FileInfo? FindLatestScan(ScannerAgentOptions options)
{
    if (!Directory.Exists(options.ScanFolder))
    {
        Directory.CreateDirectory(options.ScanFolder);
    }

    var extensions = options.AllowedExtensions
        .Select(extension => extension.StartsWith('.') ? extension.ToLowerInvariant() : $".{extension.ToLowerInvariant()}")
        .ToHashSet();

    return new DirectoryInfo(options.ScanFolder)
        .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
        .Where(file => extensions.Contains(file.Extension.ToLowerInvariant()))
        .OrderByDescending(file => file.LastWriteTimeUtc)
        .FirstOrDefault();
}

static object ToScanFileResponse(FileInfo file)
{
    return new
    {
        name = file.Name,
        path = file.FullName,
        extension = file.Extension,
        size = file.Length,
        modifiedAt = file.LastWriteTime
    };
}

static string GetContentType(string extension)
{
    return extension.ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".tif" or ".tiff" => "image/tiff",
        _ => "application/octet-stream"
    };
}

static bool IsAllowedOrigin(string? origin, ScannerAgentOptions options)
{
    if (string.IsNullOrWhiteSpace(origin))
    {
        return true;
    }

    return options.AllowedOrigins.Any(allowed =>
        origin.StartsWith(allowed, StringComparison.OrdinalIgnoreCase));
}

public sealed class ScannerAgentOptions
{
    public int Port { get; set; } = 9876;
    public string ScanFolder { get; set; } = @"C:\HuyHoangScans";
    public string UploadFieldName { get; set; } = "scan_file";
    public bool AlwaysSelectScanner { get; set; } = false;
    public bool ShowScannerUi { get; set; } = true;
    public string[] AllowedOrigins { get; set; } = [];
    public string[] AllowedExtensions { get; set; } = [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];
}

public sealed class ScanRequest
{
    public string? UploadUrl { get; set; }
    public string? FieldName { get; set; }
}

public sealed class UploadLatestRequest
{
    public string? UploadUrl { get; set; }
    public string? FieldName { get; set; }
    public string? CsrfToken { get; set; }
    public Dictionary<string, string>? Fields { get; set; }
}

public sealed record UploadResult(
    bool IsSuccessStatusCode,
    System.Net.HttpStatusCode StatusCode,
    string ResponseText,
    string? UploadedPath,
    string? UploadedUrl,
    string? UploadedName);
