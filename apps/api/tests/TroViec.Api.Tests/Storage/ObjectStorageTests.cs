using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using TroViec.Api.Infrastructure.Storage;

namespace TroViec.Api.Tests.Storage;

/// <summary>S3ObjectStorage against a real Garage node (same image and key permissions as dev/prod).</summary>
[Collection(nameof(InfraCollection))]
public sealed class ObjectStorageTests(InfraFixture infra) : IAsyncDisposable
{
    private readonly ApiFactory _factory = new(infra);

    public ValueTask DisposeAsync() => _factory.DisposeAsync();

    [Fact]
    public async Task PutGetExistsDelete_RoundTripsThroughGarage()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = _factory.Services.GetRequiredService<IObjectStorage>();
        var bucket = _factory.Services.GetRequiredService<IOptions<StorageBuckets>>().Value.ScanImages;
        var key = ObjectKeys.ScanImage(Guid.NewGuid(), 1);
        var content = TestImages.Encode(8, 8, SkiaSharp.SKEncodedImageFormat.Webp);

        await storage.PutAsync(bucket, key, content, "image/webp", ct);

        (await storage.ExistsAsync(bucket, key, ct)).ShouldBeTrue();
        (await storage.GetAsync(bucket, key, ct)).ShouldBe(content);

        await storage.DeleteAsync(bucket, key, ct);
        await storage.DeleteAsync(bucket, key, ct);   // idempotent
        (await storage.ExistsAsync(bucket, key, ct)).ShouldBeFalse();
        (await storage.GetAsync(bucket, key, ct)).ShouldBeNull();
    }

    [Fact]
    public void Buckets_ComeFromConfigurationDefaults()
    {
        var buckets = _factory.Services.GetRequiredService<IOptions<StorageBuckets>>().Value;

        (buckets.ListingImages, buckets.ScanImages, buckets.OwnerDocuments)
            .ShouldBe(("listing-images", "scan-images", "owner-documents"));
    }
}

public sealed class InMemoryObjectStorageTests
{
    [Fact]
    public async Task Fake_BehavesLikeTheRealStorage()
    {
        var ct = TestContext.Current.CancellationToken;
        var storage = new InMemoryObjectStorage();

        await storage.PutAsync("b", "k", [1, 2, 3], "image/webp", ct);

        (await storage.GetAsync("b", "k", ct)).ShouldBe(new byte[] { 1, 2, 3 });
        storage.ContentTypeOf("b", "k").ShouldBe("image/webp");
        await storage.DeleteAsync("b", "k", ct);
        (await storage.ExistsAsync("b", "k", ct)).ShouldBeFalse();
    }
}
