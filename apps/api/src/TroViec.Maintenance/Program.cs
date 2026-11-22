// Nightly maintenance jobs (docs/03-database.md, Job đêm; docs/08-deployment.md, Tác vụ định kỳ).
// Runs with ConnectionStrings__Maintenance (role app_maintenance), never the API connection string.
// Exit code 1 when any job failed, so the systemd timer shows the failure.

using Amazon.Runtime;
using Amazon.S3;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using TroViec.Maintenance;

var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
using var loggers = LoggerFactory.Create(b => b.AddJsonConsole(o => o.UseUtcTimestamp = true));

var connection = config["ConnectionStrings:Maintenance"];
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("ConnectionStrings__Maintenance is not set");
    return 2;
}

await using var db = NpgsqlDataSource.Create(connection);
using var s3 = S3Deleter.CreateClient(config);
var jobs = new NightlyJobs(db, new S3Deleter(s3), config["S3:BucketScanImages"] ?? "scan-images", loggers.CreateLogger<NightlyJobs>());
var results = await jobs.RunAsync(CancellationToken.None);
return results.All(r => r.Ok) ? 0 : 1;

/// <summary>S3 (Garage) deletes; same client settings as the API (path style, checksums only when required).</summary>
public sealed class S3Deleter(IAmazonS3 s3) : IObjectDeleter
{
    public static IAmazonS3 CreateClient(IConfiguration config) => new AmazonS3Client(
        new BasicAWSCredentials(config["S3:AccessKey"], config["S3:SecretKey"]),
        new AmazonS3Config
        {
            ServiceURL = config["S3:Endpoint"] ?? "http://localhost:3900",
            AuthenticationRegion = config["S3:Region"] ?? "garage",
            ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        });

    public Task DeleteAsync(string bucket, string key, CancellationToken ct) => s3.DeleteObjectAsync(bucket, key, ct);
}
