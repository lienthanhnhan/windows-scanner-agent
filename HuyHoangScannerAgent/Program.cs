using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ScannerAgentOptions>(builder.Configuration.GetSection("ScannerAgent"));
builder.Services.AddCors();
builder.Services
    .AddHttpClient("LaravelUpload")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false
    });

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
        scannedFile = ScanWithRetry(settings.Value);
    }
    catch (Exception exception)
    {
        return Results.Problem(
            title: "Scanner failed",
            detail: exception.Message,
            statusCode: 500);
    }

    var uploadResult = await UploadScan(scannedFile, uploadUri, request.FieldName ?? settings.Value.UploadFieldName, httpClientFactory);
    var localFileDeleted = DeleteLocalScanIfUploaded(scannedFile, uploadResult);

    return Results.Json(new
    {
        ok = uploadResult.IsSuccessStatusCode,
        statusCode = (int) uploadResult.StatusCode,
        file = ToScanFileResponse(scannedFile),
        localFileDeleted,
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
    var localFileDeleted = DeleteLocalScanIfUploaded(latest, uploadResult);

    return Results.Json(new
    {
        ok = uploadResult.IsSuccessStatusCode,
        statusCode = (int) uploadResult.StatusCode,
        file = ToScanFileResponse(latest),
        localFileDeleted,
        response = uploadResult.ResponseText
    }, statusCode: uploadResult.IsSuccessStatusCode ? 200 : 502);
});

app.Run();

static FileInfo ScanWithRetry(ScannerAgentOptions options)
{
    Exception? lastException = null;
    var attempts = Math.Max(1, options.ScanRetryAttempts);
    var delayMs = Math.Max(0, options.ScanRetryDelayMs);

    for (var attempt = 1; attempt <= attempts; attempt++)
    {
        try
        {
            return ScanWithWia(options);
        }
        catch (Exception exception) when (IsScannerBusy(exception) && attempt < attempts)
        {
            lastException = exception;
            Thread.Sleep(delayMs);
        }
        catch (Exception exception) when (IsScannerBusy(exception))
        {
            lastException = exception;
        }
    }

    if (lastException != null && IsScannerBusy(lastException))
    {
        throw new InvalidOperationException(
            "The scanner is busy. Please close PaperStream ClickScan, Windows Scan, or any open scanner dialog, wait a few seconds, then try again.",
            lastException);
    }

    throw lastException ?? new InvalidOperationException("Scanner failed.");
}

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

static bool IsScannerBusy(Exception exception)
{
    if (exception is COMException comException)
    {
        // WIA busy/general communication errors often arrive as COM exceptions.
        return comException.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)
            || comException.HResult == unchecked((int) 0x80210006)
            || comException.HResult == unchecked((int) 0x8021000A);
    }

    return exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)
        || (exception.InnerException != null && IsScannerBusy(exception.InnerException));
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

    // WIA constants: 1 = scanner device, color intent. JPEG format ID is WIA's built-in image format.
    const int scannerDeviceType = 1;
    const int colorIntent = 1;
    const int noBias = 0;
    const string jpegFormatId = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    var preferredScanner = FindPreferredScanner(options, scannerDeviceType);

    if (preferredScanner != null)
    {
        try
        {
            dynamic device = preferredScanner.Connect();
            dynamic item = device.Items[1];
            dynamic preferredImageFile = item.Transfer(jpegFormatId);
            preferredImageFile.SaveFile(filePath);

            return new FileInfo(filePath);
        }
        catch
        {
            // Fall back to the Windows scanner UI if direct preferred-device scanning fails.
        }
    }

    dynamic commonDialog = Activator.CreateInstance(commonDialogType)
        ?? throw new InvalidOperationException("Could not open Windows scanner dialog.");

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

static dynamic? FindPreferredScanner(ScannerAgentOptions options, int scannerDeviceType)
{
    if (string.IsNullOrWhiteSpace(options.PreferredScannerNameContains))
    {
        return null;
    }

    var deviceManagerType = Type.GetTypeFromProgID("WIA.DeviceManager");

    if (deviceManagerType == null)
    {
        return null;
    }

    dynamic deviceManager = Activator.CreateInstance(deviceManagerType)
        ?? throw new InvalidOperationException("Could not open Windows scanner device manager.");

    foreach (dynamic deviceInfo in deviceManager.DeviceInfos)
    {
        if ((int) deviceInfo.Type != scannerDeviceType)
        {
            continue;
        }

        var scannerText = DeviceInfoText(deviceInfo);

        if (scannerText.Contains(options.PreferredScannerNameContains, StringComparison.OrdinalIgnoreCase))
        {
            return deviceInfo;
        }
    }

    return null;
}

static string DeviceInfoText(dynamic deviceInfo)
{
    var values = new List<string>();

    foreach (dynamic property in deviceInfo.Properties)
    {
        values.Add(Convert.ToString(property.Value) ?? "");
    }

    return string.Join(" ", values);
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

    var client = httpClientFactory.CreateClient("LaravelUpload");
    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    var response = await client.PostAsync(uploadUri, form);
    var responseText = await response.Content.ReadAsStringAsync();
    var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
    var redirectLocation = response.Headers.Location?.ToString();
    string? uploadedPath = null;
    string? uploadedUrl = null;
    string? uploadedName = null;

    if ((int) response.StatusCode >= 300 && (int) response.StatusCode <= 399)
    {
        return new UploadResult(
            false,
            response.StatusCode,
            $"Laravel redirected the upload request. Upload URL: {uploadUri}. Redirect location: {redirectLocation ?? "not provided"}. This usually means the route is still behind login/admin middleware or the new public scanner route is not deployed.",
            null,
            null,
            null);
    }

    if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase)
        || responseText.TrimStart().StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
        || responseText.TrimStart().StartsWith("<html", StringComparison.OrdinalIgnoreCase))
    {
        var snippet = responseText.Trim();

        if (snippet.Length > 500)
        {
            snippet = snippet[..500];
        }

        return new UploadResult(
            false,
            response.StatusCode,
            $"Laravel returned HTML instead of JSON. Upload URL: {uploadUri}. Status: {(int) response.StatusCode}. Content-Type: {contentType}. HTML starts with: {snippet}",
            null,
            null,
            null);
    }

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

static bool DeleteLocalScanIfUploaded(FileInfo scanFile, UploadResult uploadResult)
{
    if (!uploadResult.IsSuccessStatusCode || !scanFile.Exists)
    {
        return false;
    }

    try
    {
        scanFile.Delete();
        return true;
    }
    catch
    {
        return false;
    }
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
    public string PreferredScannerNameContains { get; set; } = "fi-65F";
    public int ScanRetryAttempts { get; set; } = 3;
    public int ScanRetryDelayMs { get; set; } = 1500;
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
